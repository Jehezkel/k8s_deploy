using KDS.Dashboard.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using k8s;
using KDS.Dashboard.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

if (args.Contains("--integration-load"))
{
    var integrationConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["LoadTest:ArtifactsPath"] = Path.GetFullPath("artifacts"),
    }).Build();
    var kube = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: "rancher-desktop");
    var real = new HelmService(new DeploymentCommandRunner(), kube,
        Options.Create(new KubernetesOptions { Namespace = "kds-load-check" }), integrationConfig, new LoadTestFactory(integrationConfig));
    var input = (await real.ReadValuesAsync()).Replace("target.test", "load-check.test");
    LoadTestProgress? last = null;
    await real.RunTestAsync(input, false, message => { Console.WriteLine(message); return Task.CompletedTask; },
        default, new(10, 10, 10), p => { last = p; Console.WriteLine($"{p.Phase} {p.RemainingSeconds} req={p.Requests} errors={p.HttpErrors}"); });
    if (last is null || last.Requests <= 0 || last.Result is null) throw new Exception("Missing integration result");
    Console.WriteLine($"INTEGRATION ARTIFACTS: {last.Artifacts}");
    return;
}

const string values = "replicaCount: 3\nimage:\n  repository: kds-target\n  pullPolicy: Never\n";
var chart = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Helm", "target-app"));
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Helm:ChartPath"] = chart }).Build();
var kubeConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(new k8s.KubeConfigModels.K8SConfiguration
{
    CurrentContext = "test-context",
    Contexts = [new() { Name = "test-context", ContextDetails = new() { Cluster = "test-cluster" } }],
    Clusters = [new() { Name = "test-cluster", ClusterEndpoint = new() { Server = "https://localhost:6443" } }]
});
HelmService Service(FakeRunner runner, string ns = "kds-test") => new(runner, kubeConfig,
    Options.Create(new KubernetesOptions { Namespace = ns }), configuration);
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
static async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
    throw new Exception($"Expected rejection: {name}");
}
Task Report(string message) => Task.CompletedTask;

var runner = new FakeRunner();
await Service(runner).RunTestAsync(values, false, Report);
var calls = runner.Calls;
var delete = calls.FindIndex(c => c.Args.Contains("delete"));
var install = calls.FindIndex(c => c.Args.Contains("install"));
var upgrade = calls.FindIndex(c => c.Args.Contains("upgrade"));
Check(delete > 0 && install > delete && upgrade > install, "reset/install/upgrade order");
Check(calls.Take(delete).Count(c => c.Args.Contains("lint")) == 2, "validate both tags before reset");
Check(calls.Take(delete).Count(c => c.Args.Contains("template")) == 2, "render both tags before reset");
Check(calls[upgrade - 1].Args.Contains("status"), "wait for initial rollout before upgrade");
Check(calls[^1].Args.Contains("status"), "wait for target rollout");
Check(calls[upgrade].Args.Contains("--reuse-values") && calls[upgrade].Args.Contains("image.tag=v2") && !calls[upgrade].Args.Contains("-f"), "upgrade only overrides image tag");
Check(calls.Skip(delete).All(c => c.Args.Contains("test-context") && c.Args.Contains("kds-test")), "fixed context and namespace");
Check(calls[install].Args.Contains("image.tag=v1"), "fixed initial tag");
Check(!calls.Any(c => c.Args.Contains("build")), "unchecked checkbox never builds");
var tagging = calls.FindIndex(c => c.Args.Contains("tag"));
Check(tagging < delete && calls[tagging].Args.TakeLast(2).SequenceEqual(new[] { "kds-target:v1", "kds-target:v2" }), "same image receives both tags before reset");
var rebuilt = new FakeRunner();
await Service(rebuilt).RunTestAsync(values, true, Report);
Check(rebuilt.Calls.Count(c => c.Args.Contains("build")) == 1, "force rebuild builds once");
var build = rebuilt.Calls.FindIndex(c => c.Args.Contains("build"));
Check(rebuilt.Calls[build].Args.Contains("--no-cache") && rebuilt.Calls[build].Args.Contains("kds-target:v1"), "build current source as v1 without cache");
Check(build < rebuilt.Calls.FindIndex(c => c.Args.Contains("tag")), "tag after build");
var failedBuild = new FakeRunner { FailOn = "build" };
await Reject(() => Service(failedBuild).RunTestAsync(values, true, Report), "build failure");
Check(!failedBuild.Calls.Any(c => c.Args.Contains("delete")), "build failure preserves namespace");
var missingImage = new FakeRunner { FailOn = "inspect" };
await Reject(() => Service(missingImage).RunTestAsync(values, false, Report), "missing existing v1");
Check(!missingImage.Calls.Any(c => c.Args.Contains("delete")), "missing image preserves namespace");
await Reject(() => Service(new()).RunTestAsync(values.Replace("Never", "Always"), false, Report), "local image pull policy");
await Reject(() => Service(new()).RunTestAsync(values.Replace("replicaCount: 3", "replicaCount: 0"), false, Report), "zero replicas");
await Reject(() => Service(new(), "default").RunTestAsync(values, false, Report), "protected namespace");
var brokenLint = new FakeRunner { FailOn = "lint" };
await Reject(() => Service(brokenLint).RunTestAsync(values, false, Report), "lint failure");
Check(!brokenLint.Calls.Any(c => c.Args.Contains("delete")), "lint failure cannot clean namespace");
var brokenInstall = new FakeRunner { FailOn = "install" };
await Reject(() => Service(brokenInstall).RunTestAsync(values, false, Report), "install failure");
Check(!brokenInstall.Calls.Any(c => c.Args.Contains("upgrade")), "install failure cannot start update");
var blocked = new FakeRunner { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
var shared = Service(blocked);
var first = shared.RunTestAsync(values, false, Report);
await blocked.Entered.Task;
await Reject(() => shared.RunTestAsync(values, false, Report), "concurrent run");
blocked.Hold.SetResult();
await first;
var services = new ServiceCollection().AddLogging()
    .AddSingleton<IJSRuntime, NoJs>()
    .AddSingleton(Service(new FakeRunner()))
    .AddSingleton<DeploymentTestService>().BuildServiceProvider();
await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
{
    await renderer.Dispatcher.InvokeAsync(async () =>
    {
        var dialog = await renderer.RenderComponentAsync<DeployDialog>(ParameterView.Empty);
        var html = dialog.ToHtmlString();
        Check(html.Contains("role=\"dialog\"") && html.Contains("kds-test") && html.Contains("test-context"), "dialog context");
        Check(html.Contains("value-replicaCount") && !html.Contains("<textarea") && html.Contains("force-rebuild") && !html.Contains("deploy-target"), "values editor and rebuild checkbox, no tag input");
    });
}
await DeadlineChecks.RunAsync(kubeConfig, configuration);
RunArchiveChecks.Run();
await TimelineChecks.RunAsync(Service);
await LoadTestChecks.RunAsync(kubeConfig, configuration);
await LoadProcessChecks.RunAsync();
await LiveTopologyChecks.RunAsync(Service);
await DialogInteractionChecks.RunAsync(Service(new()));
Console.WriteLine("PASS: command order, validation before reset, fixed namespace/context, tag-only upgrade, failure handling, concurrent-run lock, rebuild/reuse, dialog.");

sealed class FakeRunner : IDeploymentCommandRunner
{
    public List<(string Exe, string[] Args)> Calls { get; } = [];
    public string? FailOn { get; init; }
    public TaskCompletionSource? Hold { get; init; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var args = arguments.ToArray();
        Calls.Add((executable, args));
        Entered.TrySetResult();
        if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
        if (FailOn is not null && args.Contains(FailOn)) throw new InvalidOperationException("Simulated command failure");
    }
}

sealed class NoJs : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
}
