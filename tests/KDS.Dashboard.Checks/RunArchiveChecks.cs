using KDS.Dashboard.Services;
using Microsoft.Extensions.Configuration;
using k8s.Models;

static class RunArchiveChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "kds-archive-" + Guid.NewGuid());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Runs:Path"] = path }).Build();
        var t = DateTimeOffset.UtcNow;
        var archive = new RunArchive(config);
        var pod = new V1Pod { Metadata = new() { Uid = "one", Name = "same" }, Status = new() { Phase = "Running" } };
        RunFrame Frame(DateTimeOffset time) => new(time, new([pod], true, null), new([], true, null), new([], true, null));
        void Check(bool value, string name) { if (!value) throw new Exception("Archive: " + name); }
        archive.Observe(Frame(t));
        Check(archive.List().Length == 0 && Directory.GetFiles(path).Length == 0, "no background recording");
        var id = Guid.NewGuid();
        archive.Begin(id, t, "context", "namespace", "values", false, new());
        archive.Observe(Frame(t));
        pod.Status.Phase = "Failed";
        archive.Observe(Frame(t.AddHours(2)));
        archive.Progress(new("load", Requests: 10, SampleTime: t));
        archive.Progress(new("load", Requests: 20, SampleTime: t.AddSeconds(1)));
        archive.Complete(id, "Zakończony", t.AddHours(2));
        archive.Alias(id, "Alias");
        var restored = new RunArchive(config).Get(id);
        Check(restored.Info.Alias == "Alias" && restored.Info.Values == "values", "metadata persists");
        Check(restored.Frames[0].Pods.Items[0].Status.Phase == "Running", "frames isolated");
        Check(restored.Timeline.Traffic.Length == 1 && restored.Frames.Length == 2, "long run retained");
        var second = Guid.NewGuid();
        archive.Begin(second, t, "c", "n", "", false, null);
        archive.Observe(Frame(t));
        var recovered = new RunArchive(config);
        Check(recovered.Get(second).Info.Result == "Przerwany", "restart recovers incomplete run");
        Check(recovered.Get(second).Timeline.Traffic.Length == 0, "sessions isolated");
        File.AppendAllText(Path.Combine(path, $"{second:N}.jsonl"), "broken\n");
        Check(recovered.Get(second).Info.StorageError is not null, "corruption exposed");
        var third = Guid.NewGuid();
        archive.Begin(third, t, "c", "n", "", false, null, Frame(t));
        archive.Observe(Frame(t.AddSeconds(1)));
        Check(archive.Get(third).Frames.All(f => f.Pods.Items.Count == 0), "old namespace watch cache excluded");
        pod.Metadata.Uid = "new-uid";
        archive.Observe(Frame(t.AddSeconds(2)));
        Check(archive.Get(third).Frames.Last().Pods.Items.Single().Metadata.Uid == "new-uid", "reused name with new UID recorded");
        Directory.Delete(path, true);
        Console.WriteLine("PASS: run archive persistence, recovery, isolation, long runs, corruption.");
    }
}
