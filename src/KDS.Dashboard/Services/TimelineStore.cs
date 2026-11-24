using System.Text.Json.Nodes;
using k8s;
using k8s.Models;

namespace KDS.Dashboard.Services;

public enum DeploymentStage { Reset, NamespaceCreating, InstallStarted, InstallReady, UpgradeStarted, UpgradeReady, Completed, Failed }
public sealed record DeploymentStageEvent(Guid TestId, DateTimeOffset Time, string Deployment, DeploymentStage Stage, string? Image = null);
public sealed record TrafficPoint(Guid TestId, DateTimeOffset Start, DateTimeOffset End, double RequestsPerSecond, double ErrorsPerSecond, double? ErrorPercent);
public sealed record PodInterval(string Uid, string Name, string ReplicaSet, string Deployment, DateTimeOffset Start, DateTimeOffset? End, string State, string Details);
public sealed record TimelineEvent(DateTimeOffset Time, string Kind, string Name, string Details);
public sealed record RolloutInterval(string Uid, string Name, string Image, DateTimeOffset Start, DateTimeOffset? End = null, string Result = "W toku", Guid? TestId = null);
public sealed record ReplicaPoint(string Uid, string Name, DateTimeOffset Time, int Ready, int Desired, bool Known);
public sealed record WatchGap(string Source, DateTimeOffset Start, DateTimeOffset? End);
public sealed record TimelineSnapshot(TrafficPoint[] Traffic, PodInterval[] Pods, RolloutInterval[] Rollouts, ReplicaPoint[] Replicas, TimelineEvent[] Events, WatchGap[] Gaps);

/// <summary>History for one recorded run. All writes and snapshots share one lock.</summary>
public sealed class TimelineStore
{
    private readonly object _sync = new();
    private readonly List<TrafficPoint> _traffic = [];
    private readonly List<PodInterval> _pods = [];
    private readonly List<RolloutInterval> _rollouts = [];
    private readonly List<ReplicaPoint> _replicas = [];
    private readonly List<TimelineEvent> _events = [];
    private readonly List<WatchGap> _gaps = [];
    private readonly Dictionary<string, V1Deployment> _deployments = [];
    private readonly Dictionary<string, bool> _connected = [];
    private readonly Dictionary<string, DeploymentStageEvent> _pending = [];
    private (Guid Id, DateTimeOffset Time, double Requests, double Errors)? _lastTraffic;

    public void Traffic(Guid id, DateTimeOffset time, double requests, double errors)
    {
        lock (_sync)
        {
            if (_lastTraffic is { } previous && previous.Id == id && time <= previous.Time) return;
            if (_lastTraffic is { } p && p.Id == id && time > p.Time && time - p.Time <= TimeSpan.FromSeconds(5) && requests >= p.Requests && errors >= p.Errors)
            {
                var seconds = (time - p.Time).TotalSeconds;
                var count = requests - p.Requests;
                _traffic.Add(new(id, p.Time, time, count / seconds, (errors - p.Errors) / seconds, count > 0 ? 100 * (errors - p.Errors) / count : null));
            }
            _lastTraffic = (id, time, requests, errors);
        }
    }

    public void Stage(DeploymentStageEvent stage)
    {
        lock (_sync)
        {
            if (stage.Stage is DeploymentStage.InstallStarted or DeploymentStage.UpgradeStarted)
                _pending[stage.Deployment] = stage;
            else if (stage.Stage is DeploymentStage.Completed or DeploymentStage.Failed)
            {
                foreach (var key in _pending.Where(p => p.Value.TestId == stage.TestId).Select(p => p.Key).ToArray()) _pending.Remove(key);
                _lastTraffic = null;
            }
            // Rollout boundaries come from the watch. Test phases are kept separately, not duplicated as deploy markers.
            _events.Add(new(stage.Time, "Test", stage.Deployment, $"{stage.Stage} · test {stage.TestId:N}"));
        }
    }

    private bool Connection(string source, bool connected, DateTimeOffset now)
    {
        var was = _connected.GetValueOrDefault(source);
        _connected[source] = connected;
        if (!connected && !_gaps.Any(g => g.Source == source && g.End is null)) _gaps.Add(new(source, now, null));
        if (connected)
            for (var i = 0; i < _gaps.Count; i++)
                if (_gaps[i].Source == source && _gaps[i].End is null) _gaps[i] = _gaps[i] with { End = now };
        return was;
    }

    public void ObserveConnection(string source, bool connected, DateTimeOffset now)
    {
        lock (_sync) Connection(source, connected, now);
    }

    public void ObservePods(ResourceSnapshot<V1Pod> snapshot, IReadOnlyDictionary<string, V1ReplicaSet> sets, DateTimeOffset now)
    {
        lock (_sync)
        {
            var was = Connection("Pody", snapshot.Connected, now);
            if (!snapshot.Connected)
            {
                for (var i = 0; i < _pods.Count; i++) if (_pods[i].End is null) _pods[i] = _pods[i] with { End = now };
                return;
            }
            var ids = snapshot.Items.Select(p => p.Metadata.Uid).ToHashSet();
            for (var i = 0; i < _pods.Count; i++)
                if (_pods[i].End is null && !ids.Contains(_pods[i].Uid))
                {
                    if (was) _events.Add(new(now, "Usunięcie", _pods[i].Name, _pods[i].Uid));
                    _pods[i] = _pods[i] with { End = now };
                }
            foreach (var pod in snapshot.Items)
            {
                var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Kind == "ReplicaSet" && o.Controller == true);
                var deployment = PodDeploymentOwnership.Resolve(pod, sets)?.Name ?? "Bez Deploymentu";
                var state = pod.Metadata.DeletionTimestamp is not null ? "Terminating" : pod.Status?.Phase is "Succeeded" or "Failed" ? "Zakończony" : pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true ? "Ready" : pod.Status?.Phase == "Running" ? "NotReady" : "Pending";
                var details = string.Join("; ", (pod.Status?.InitContainerStatuses ?? []).Concat(pod.Status?.ContainerStatuses ?? []).Select(c => $"{c.Name}: ready={c.Ready}, restarts={c.RestartCount}, {c.State?.Waiting?.Reason ?? c.State?.Terminated?.Reason ?? (c.State?.Running is not null ? "Running" : "Unknown")}"));
                var index = _pods.FindLastIndex(p => p.Uid == pod.Metadata.Uid && p.End is null);
                if (index >= 0 && _pods[index].State == state && _pods[index].Details == details)
                {
                    _pods[index] = _pods[index] with { Deployment = deployment, ReplicaSet = owner?.Name ?? "—" };
                    continue;
                }
                if (index >= 0) _pods[index] = _pods[index] with { End = now };
                _pods.Add(new(pod.Metadata.Uid, pod.Metadata.Name, owner?.Name ?? "—", deployment, now, null, state, details));
            }
        }
    }

    public void ObserveDeployments(ResourceSnapshot<V1Deployment> snapshot, DateTimeOffset now)
    {
        lock (_sync)
        {
            var was = Connection("Deploymenty", snapshot.Connected, now);
            if (!snapshot.Connected)
            {
                if (was)
                {
                    foreach (var d in _deployments.Values) _replicas.Add(new(d.Metadata.Uid, d.Metadata.Name, now, 0, 0, false));
                    for (var i = 0; i < _rollouts.Count; i++) if (_rollouts[i].End is null) _rollouts[i] = _rollouts[i] with { End = now, Result = "Utrata obserwacji" };
                }
                return;
            }
            var ids = snapshot.Items.Select(d => d.Metadata.Uid).ToHashSet();
            foreach (var uid in _deployments.Keys.Where(uid => !ids.Contains(uid)).ToArray())
            {
                var old = _deployments[uid];
                _replicas.Add(new(uid, old.Metadata.Name, now, 0, 0, false));
                CloseRollout(uid, now, "Usunięty");
                _deployments.Remove(uid);
            }
            foreach (var d in snapshot.Items)
            {
                var uid = d.Metadata.Uid;
                var desired = d.Spec.Replicas ?? 1;
                var ready = d.Status?.ReadyReplicas ?? 0;
                var previous = _replicas.LastOrDefault(p => p.Uid == uid);
                if (previous is null || !previous.Known || previous.Ready != ready || previous.Desired != desired)
                    _replicas.Add(new(uid, d.Metadata.Name, now, ready, desired, true));
                _deployments.TryGetValue(uid, out var old);
                var changed = old is not null && !JsonNode.DeepEquals(JsonNode.Parse(KubernetesJson.Serialize(old.Spec.Template)), JsonNode.Parse(KubernetesJson.Serialize(d.Spec.Template)));
                var image = string.Join(", ", d.Spec.Template.Spec.Containers.Select(c => c.Image));
                var hasPending = _pending.TryGetValue(d.Metadata.Name, out var pending) && now >= pending.Time && image == pending.Image;
                if ((was && (old is null || changed)) || (hasPending && !was))
                {
                    CloseRollout(uid, now, "Zastąpiony");
                    Guid? testId = null;
                    if (hasPending)
                    { testId = pending!.TestId; _pending.Remove(d.Metadata.Name); }
                    _rollouts.Add(new(uid, d.Metadata.Name, image, hasPending ? pending!.Time : now, TestId: testId));
                }
                else if (was && old is not null && old.Spec.Replicas != d.Spec.Replicas)
                    _events.Add(new(now, "Skalowanie", d.Metadata.Name, $"{old.Spec.Replicas ?? 1} → {desired}"));
                if (d.Status?.ObservedGeneration >= d.Metadata.Generation)
                {
                    if (d.Status.Conditions?.Any(c => c.Type == "Progressing" && c.Status == "False" && c.Reason == "ProgressDeadlineExceeded") == true)
                        CloseRollout(uid, now, "Nieudany");
                    else if ((d.Status.UpdatedReplicas ?? 0) == desired && (d.Status.Replicas ?? 0) == desired && (d.Status.AvailableReplicas ?? 0) == desired)
                        CloseRollout(uid, now, "Gotowy");
                }
                _deployments[uid] = d;
            }
        }
    }

    private void CloseRollout(string uid, DateTimeOffset now, string result)
    {
        for (var i = 0; i < _rollouts.Count; i++)
            if (_rollouts[i].Uid == uid && _rollouts[i].End is null) _rollouts[i] = _rollouts[i] with { End = now, Result = result };
    }

    public TimelineSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            return new(_traffic.ToArray(), _pods.ToArray(), _rollouts.ToArray(), _replicas.ToArray(), _events.ToArray(), _gaps.ToArray());
        }
    }
}
