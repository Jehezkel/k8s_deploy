using System.Text.Json;
using k8s;
using k8s.Models;

namespace KDS.Dashboard.Services;

public sealed record RunInfo(int Version, Guid Id, string Name, string? Alias, string Context, string Namespace,
    string Values, bool Rebuild, LoadTestOptions? LoadTest, DateTimeOffset Start, DateTimeOffset? End = null,
    string Result = "W toku", string? StorageError = null);
public sealed record RunFrame(DateTimeOffset Time, ResourceSnapshot<V1Pod> Pods,
    ResourceSnapshot<V1ReplicaSet> ReplicaSets, ResourceSnapshot<V1Deployment> Deployments);
public sealed record RunEntry(DateTimeOffset Time, RunFrame? Frame = null, DeploymentStageEvent? Stage = null, LoadTestProgress? Progress = null);
public sealed record RunView(RunInfo Info, TimelineSnapshot Timeline, RunFrame[] Frames, RunEntry[] Entries);

/// <summary>Only explicit dashboard tests own journals. All journal writes and reads are serialized.</summary>
public sealed class RunArchive
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<Guid, RunInfo> _runs = [];
    private readonly List<string> _errors = [];
    private readonly List<RunEntry> _entries = [];
    private TimelineStore _timeline = new();
    private Guid? _active;
    private HashSet<string> _previousResources = [];
    public RunArchive(IConfiguration configuration)
    {
        _path = Path.GetFullPath(configuration["Runs:Path"] ?? "artifacts/runs");
        try
        {
            Directory.CreateDirectory(_path);
            foreach (var file in Directory.EnumerateFiles(_path, "*.json"))
            {
                try
                {
                    var info = JsonSerializer.Deserialize<RunInfo>(File.ReadAllText(file)) ?? throw new InvalidDataException("Puste metadane");
                    if (info.Version != 1) throw new InvalidDataException("Nieobsługiwana wersja nagrania");
                    _runs[info.Id] = info;
                    if (info.End is null)
                    {
                        var entries = ReadEntries(info.Id);
                        _runs[info.Id] = _runs[info.Id] with { End = entries.LastOrDefault()?.Time ?? info.Start, Result = "Przerwany" };
                        SaveInfo(info.Id);
                    }
                }
                catch (Exception ex) { _errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { _errors.Add(ex.Message); }
    }
    public string[] Errors { get { lock (_sync) return _errors.ToArray(); } }
    public Guid? ActiveId { get { lock (_sync) return _active; } }
    public RunInfo[] List() { lock (_sync) return _runs.Values.OrderByDescending(r => r.Start).ToArray(); }
    private string Metadata(Guid id) => Path.Combine(_path, $"{id:N}.json");
    private string Journal(Guid id) => Path.Combine(_path, $"{id:N}.jsonl");
    private void StorageFailure(Guid id, Exception ex)
    {
        _runs[id] = _runs[id] with { StorageError = $"Nagranie nie zostało w pełni zapisane: {ex.Message}" };
    }
    private void SaveInfo(Guid id)
    {
        try
        {
            var path = Metadata(id);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_runs[id]));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) { StorageFailure(id, ex); }
    }
    public void Begin(Guid id, DateTimeOffset time, string context, string ns, string values, bool rebuild, LoadTestOptions? load, RunFrame? baseline = null)
    {
        lock (_sync)
        {
            _active = id;
            _entries.Clear();
            _timeline = new();
            _runs[id] = new(1, id, $"{HelmService.Release} · {ns} · {time:yyyy-MM-dd HH:mm:ss} UTC", null, context, ns, values, rebuild, load, time);
            SaveInfo(id);
            _previousResources = baseline is null ? [] : baseline.Pods.Items.Select(p => p.Metadata.Uid)
                .Concat(baseline.ReplicaSets.Items.Select(r => r.Metadata.Uid))
                .Concat(baseline.Deployments.Items.Select(d => d.Metadata.Uid)).ToHashSet();
            if (baseline is not null) Observe(baseline);
        }
    }
    private static void Apply(TimelineStore timeline, Guid id, RunEntry entry)
    {
        if (entry.Frame is { } f)
        {
            timeline.ObserveConnection("ReplicaSety", f.ReplicaSets.Connected, f.Time);
            timeline.ObservePods(f.Pods, f.ReplicaSets.Items.ToDictionary(r => r.Metadata.Uid), f.Time);
            timeline.ObserveDeployments(f.Deployments, f.Time);
        }
        if (entry.Stage is { } stage) timeline.Stage(stage);
        if (entry.Progress is { SampleTime: { } time } p) timeline.Traffic(id, time, p.Requests, p.HttpErrors);
    }
    private void Append(RunEntry entry)
    {
        if (_active is not { } id) return;
        // Clone Kubernetes resources: watch caches and UI must never mutate archived frames.
        entry = KubernetesJson.Deserialize<RunEntry>(KubernetesJson.Serialize(entry));
        _entries.Add(entry);
        Apply(_timeline, id, entry);
        try { File.AppendAllText(Journal(id), KubernetesJson.Serialize(entry) + "\n"); }
        catch (Exception ex) { StorageFailure(id, ex); }
    }
    public void Observe(RunFrame frame)
    {
        lock (_sync)
        {
            if (_active is null) return;
            // Deleting the previous namespace has completed, but watch delivery may still lag.
            frame = frame with
            {
                Pods = frame.Pods with { Items = frame.Pods.Items.Where(p => !_previousResources.Contains(p.Metadata.Uid)).ToArray() },
                ReplicaSets = frame.ReplicaSets with { Items = frame.ReplicaSets.Items.Where(r => !_previousResources.Contains(r.Metadata.Uid)).ToArray() },
                Deployments = frame.Deployments with { Items = frame.Deployments.Items.Where(d => !_previousResources.Contains(d.Metadata.Uid)).ToArray() }
            };
            Append(new(frame.Time, Frame: frame));
        }
    }
    public void Stage(DeploymentStageEvent stage) { lock (_sync) Append(new(stage.Time, Stage: stage)); }
    public void Progress(LoadTestProgress progress) { lock (_sync) Append(new(DateTimeOffset.UtcNow, Progress: progress)); }
    public void Complete(Guid id, string result, DateTimeOffset time)
    {
        lock (_sync)
        {
            if (!_runs.TryGetValue(id, out var info)) return;
            if (result == "Przekroczono czas" && info.LoadTest is null && time > info.Start.AddSeconds(120)) time = info.Start.AddSeconds(120);
            _runs[id] = info with { End = time, Result = result };
            SaveInfo(id);
            if (_active == id) _active = null;
        }
    }
    public void Alias(Guid id, string? alias)
    {
        lock (_sync) { _runs[id] = _runs[id] with { Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim() }; SaveInfo(id); }
    }
    private RunEntry[] ReadEntries(Guid id)
    {
        var entries = new List<RunEntry>();
        try
        {
            foreach (var line in File.ReadLines(Journal(id)))
            {
                try { entries.Add(KubernetesJson.Deserialize<RunEntry>(line)); }
                catch (Exception ex) { StorageFailure(id, ex); break; }
            }
        }
        catch (Exception ex) { StorageFailure(id, ex); }
        return entries.ToArray();
    }
    public RunView Get(Guid id)
    {
        lock (_sync)
        {
            var entries = _active == id ? _entries.ToArray() : ReadEntries(id);
            var timeline = _active == id ? _timeline : new TimelineStore();
            if (_active != id) foreach (var entry in entries) Apply(timeline, id, entry);
            var info = _runs[id];
            return new(info, timeline.Snapshot(info.End ?? DateTimeOffset.UtcNow), entries.Where(e => e.Frame is not null).Select(e => e.Frame!).ToArray(), entries);
        }
    }
}
