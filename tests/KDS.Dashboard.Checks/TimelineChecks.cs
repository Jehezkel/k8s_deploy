using KDS.Dashboard.Services;
using Microsoft.Extensions.Configuration;
using KDS.Dashboard.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using k8s;
using k8s.Models;

static class TimelineChecks
{
    private static void Check(bool value, string name) { if (!value) throw new Exception("Timeline: " + name); }
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    public static async Task RunAsync(Func<FakeRunner, string, HelmService> service)
    {
        var store = new TimelineStore();
        var id = Guid.NewGuid();
        store.Traffic(id, T, 10, 1);
        store.Traffic(id, T.AddSeconds(2), 30, 3);
        var point = store.Snapshot(T.AddSeconds(2)).Traffic.Single();
        Check(point.RequestsPerSecond == 10 && point.ErrorsPerSecond == 1 && point.ErrorPercent == 10, "irregular sample deltas");
        store.Traffic(id, T.AddSeconds(2), 999, 999);
        Check(store.Snapshot(T).Traffic.Length == 1, "phase notifications are not samples");
        store.Traffic(id, T.AddSeconds(3), 30, 3);
        Check(store.Snapshot(T).Traffic.Last().ErrorPercent is null, "no requests has undefined percent");
        store.Traffic(id, T.AddSeconds(4), 1, 0);
        store.Traffic(Guid.NewGuid(), T.AddSeconds(5), 100, 0);
        store.Traffic(id, T.AddSeconds(20), 200, 1);
        Check(store.Snapshot(T).Traffic.Length == 2, "counter resets, sessions and gaps do not bridge");
        Check(store.Snapshot(T.AddHours(2)).Traffic.Length == 2, "long recordings retain traffic");

        store = new();
        void Pods(double seconds, bool connected, params V1Pod[] pods) => store.ObservePods(new(pods, connected, null), new Dictionary<string, V1ReplicaSet>(), T.AddSeconds(seconds));
        Pods(0, true, Pod("one", false));
        Pods(1, true, Pod("one", true));
        Pods(2, false, Pod("one", true));
        Pods(4, true, Pod("one", false));
        var history = store.Snapshot(T.AddSeconds(4));
        Check(history.Pods.Length == 3 && history.Pods[1].End == T.AddSeconds(2) && history.Pods[2].Start == T.AddSeconds(4), "pod gaps stay empty");
        Pods(5, true);
        Pods(6, true, Pod("two", true));
        history = store.Snapshot(T.AddSeconds(6));
        Check(history.Events.Count(e => e.Kind == "Usunięcie") == 1 && history.Pods.Select(p => p.Uid).Distinct().Count() == 2, "deletion and reused names use UID");
        Pods(7, false);
        Pods(8, true);
        Check(store.Snapshot(T.AddSeconds(8)).Events.Count(e => e.Kind == "Usunięcie") == 1, "relist does not invent deletion time");
        Pods(9, true, Pod("three", true));
        history = store.Snapshot(T.AddHours(2));
        Check(history.Pods.Any(p => p.Uid == "three"), "retain open state");

        store = new();
        void Deploy(double seconds, bool connected, params V1Deployment[] deployments) => store.ObserveDeployments(new(deployments, connected, null), T.AddSeconds(seconds));
        Deploy(0, true, Deployment("d", "v1", 1, true));
        Check(store.Snapshot(T).Rollouts.Length == 0, "initial deployment is baseline");
        store.Stage(new(id, T.AddSeconds(.5), "demo-target", DeploymentStage.UpgradeStarted, "app:v2"));
        Deploy(1, true, Deployment("d", "v2", 2));
        Deploy(2, true, Deployment("d", "v2", 2, true));
        history = store.Snapshot(T.AddSeconds(2));
        Check(history.Rollouts.Length == 1 && history.Rollouts[0].Result == "Gotowy" && history.Rollouts[0].TestId == id, "v1 to v2 correlated once");
        Deploy(3, true, Deployment("d", "v2", 3, true, 5));
        Check(store.Snapshot(T).Rollouts.Length == 1 && store.Snapshot(T).Events.Any(e => e.Kind == "Skalowanie"), "scale does not deploy");
        Deploy(4, true, Deployment("d", "v3", 4));
        Deploy(5, true, Deployment("d", "v4", 5));
        history = store.Snapshot(T);
        Check(history.Rollouts[1].Result == "Zastąpiony" && history.Rollouts[2].TestId is null, "external superseded rollout");
        Deploy(6, false);
        Deploy(8, true, Deployment("d", "v5", 6, true));
        history = store.Snapshot(T);
        Check(history.Rollouts.Length == 3 && history.Rollouts[2].Result == "Utrata obserwacji", "relist does not invent rollout");
        Deploy(9, true);
        Deploy(10, true, Deployment("new-d", "v1", 1));
        Check(store.Snapshot(T).Rollouts.Last().Uid == "new-d", "namespace recreation uses new identity");
        history = store.Snapshot(T.AddHours(2));
        Check(history.Replicas.Any(p => p.Uid == "new-d"), "retain recreated deployment");
        var copy = store.Snapshot(T.AddHours(2));
        copy.Replicas[0] = copy.Replicas[0] with { Ready = 999 };
        Check(store.Snapshot(T.AddHours(2)).Replicas[0].Ready != 999, "snapshots isolated for browser subscribers");

        store = new();
        store.Stage(new(id, T, "demo-target", DeploymentStage.InstallStarted, "app:v1"));
        store.ObserveDeployments(new([Deployment("initial", "v1", 1, true)], true, null), T.AddSeconds(1));
        Check(store.Snapshot(T).Rollouts.Single().TestId == id, "known install correlates even after namespace gap");

        var stages = new List<DeploymentStageEvent>();
        var helm = service(new(), "kds-test");
        await helm.RunTestAsync(await helm.ReadValuesAsync(), false, _ => Task.CompletedTask, stage: stages.Add, testId: id);
        Check(stages.Select(s => s.Stage).SequenceEqual(new[] { DeploymentStage.Reset, DeploymentStage.NamespaceCreating, DeploymentStage.InstallStarted, DeploymentStage.InstallReady, DeploymentStage.UpgradeStarted, DeploymentStage.UpgradeReady, DeploymentStage.Completed }), "typed execution stage order");
        Check(stages.All(s => s.TestId == id), "typed stages retain test identity");
        store = new();
        store.ObserveDeployments(new([Deployment("d", "v1", 1, true)], true, null), T);
        var failed = Deployment("d", "v2", 2);
        failed.Status.ObservedGeneration = 2;
        failed.Status.Conditions = [new() { Type = "Progressing", Status = "False", Reason = "ProgressDeadlineExceeded" }];
        store.ObserveDeployments(new([failed], true, null), T.AddSeconds(1));
        Check(store.Snapshot(T).Rollouts.Single().Result == "Nieudany", "deadline is not success");
        var terminating = Pod("p", true);
        terminating.Metadata.DeletionTimestamp = T.UtcDateTime;
        store.ObservePods(new([terminating], true, null), new Dictionary<string, V1ReplicaSet>(), T);
        Check(store.Snapshot(T).Pods.Single().State == "Terminating", "deletion timestamp overrides Ready");
        await RenderChecks();
        Console.WriteLine("PASS: timeline rates, resets, gaps, pod identity, retention, rollout correlation, external deploys, scaling, supersession, typed stages.");
    }
    private static async Task RenderChecks()
    {
        var store = new TimelineStore();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        store.Traffic(id, now.AddSeconds(-3), 10, 0);
        store.Traffic(id, now.AddSeconds(-1), 30, 2);
        store.ObserveDeployments(new([Deployment("d", "v1", 1, true)], true, null), now.AddMinutes(-2));
        store.ObserveDeployments(new([Deployment("d", "v2", 2)], true, null), now.AddMinutes(-1));
        store.ObservePods(new([Pod("p", true)], true, null), new Dictionary<string, V1ReplicaSet>(), now.AddMinutes(-1));
        using var client = new KubernetesWatchClient(new KubernetesClientConfiguration { Host = "https://localhost:6443" });
        var kubernetes = new KubernetesService(NullLogger<KubernetesService>.Instance, client, Options.Create(new KubernetesOptions { Namespace = "test" }));
        using var monitor = new ClusterMonitor(kubernetes, NullLogger<ClusterMonitor>.Instance);
        var archivePath = Path.Combine(Path.GetTempPath(), "kds-render-" + Guid.NewGuid());
        var archive = new RunArchive(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Runs:Path"] = archivePath }).Build());
        archive.Begin(id, now.AddMinutes(-2), "test", "test", "", false, null);
        archive.Observe(new(now.AddMinutes(-1), new([Pod("p", true)], true, null), new([], true, null), new([Deployment("d", "v2", 2)], true, null)));
        archive.Progress(new("test", Requests: 10, SampleTime: now.AddSeconds(-3)));
        archive.Progress(new("test", Requests: 30, SampleTime: now.AddSeconds(-1)));
        using var services = new ServiceCollection().AddSingleton(archive).AddLogging().AddSingleton(store).AddSingleton(monitor).AddSingleton<IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var first = await renderer.RenderComponentAsync<DeploymentTimeline>(ParameterView.Empty);
            var second = await renderer.RenderComponentAsync<DeploymentTimeline>(ParameterView.Empty);
            var html = first.ToHtmlString();
            Check(html.Contains("1×") && html.Contains("2×") && html.Contains("4×"), "playback controls rendered");
            Check(html.Contains("pod-state ready") && !html.Contains("NaN"), "populated SVG renders");
            Check(!html.Contains("Brak pomiaru k6 w tym zakresie"), "samples visible in chart");
            Check(second.ToHtmlString().Contains("pod-state ready") && store.Snapshot(now).Rollouts.Length == 1, "two views share history without duplicate events");
        });
    }

    private static V1Pod Pod(string uid, bool ready) => new()
    {
        Metadata = new() { Uid = uid, Name = "same-name", NamespaceProperty = "test" },
        Status = new() { Phase = "Running", Conditions = [new() { Type = "Ready", Status = ready ? "True" : "False" }] }
    };
    private static V1Deployment Deployment(string uid, string version, long generation, bool complete = false, int replicas = 3) => new()
    {
        Metadata = new() { Uid = uid, Name = "demo-target", Generation = generation },
        Spec = new() { Replicas = replicas, Template = new() { Spec = new() { Containers = [new() { Name = "app", Image = "app:" + version }] } } },
        Status = new() { ObservedGeneration = complete ? generation : generation - 1, Replicas = replicas, UpdatedReplicas = complete ? replicas : 0, AvailableReplicas = complete ? replicas : 0, ReadyReplicas = complete ? replicas : 0 }
    };
}
