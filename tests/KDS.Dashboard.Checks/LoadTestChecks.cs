using KDS.Dashboard.Services;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

static class LoadTestChecks
{
    public static async Task RunAsync(KubernetesClientConfiguration kube, IConfiguration config)
    {
        var values = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Helm", "target-app", "values.yaml"));
        var effective = LoadTestOptions.PrepareValues(values.Replace("enabled: true", "enabled: false").Replace("className: traefik", "className: other"));
        Assert(effective.Values.Contains("className: traefik") && effective.Values.Contains("enabled: true"), "force ingress");
        foreach (var invalid in new[] { new LoadTestOptions(-1), new LoadTestOptions(0, 3601), new LoadTestOptions(0, 0, 0), new LoadTestOptions(0, 0, 1001) })
        {
            try { invalid.Validate(); throw new Exception("Invalid options accepted"); } catch (ArgumentException) { }
        }
        foreach (var failure in new[] { "", "preflight", "start", "before", "upgrade", "after", "stop" })
        {
            var events = new List<string>();
            var factory = new FakeLoadFactory(events, failure);
            var runner = new Runner(events, failure);
            var service = new HelmService(runner, kube, Options.Create(new KubernetesOptions { Namespace = "kds-check" }), config, factory);
            var completed = false;
            try
            {
                await service.RunTestAsync(values, false, _ => Task.CompletedTask, default, new(7, 11, 10),
                    _ => { }, () => completed = true);
                Assert(failure == "", "failure must propagate");
            }
            catch (InvalidOperationException) when (failure != "") { }
            if (failure == "preflight") Assert(!events.Contains("delete"), "preflight preserves namespace");
            else Assert(events.Last() == "dispose", "sessions disposed on every outcome");
            if (failure == "")
            {
                Assert(events.IndexOf("start") > events.IndexOf("ready"), "start after initial Ready");
                Assert(events.IndexOf("before:7") < events.IndexOf("upgrade") && events.IndexOf("after:11") > events.LastIndexOf("ready"), "full periods around rollout");
                Assert(events.Count(e => e == "start") == 1 && completed, "one process and deployment result");
                Assert(events.IndexOf("guard") < events.IndexOf("upgrade"), "rollout guarded by load session");
            }
            if (failure is "start" or "before") Assert(!events.Contains("upgrade"), "no upgrade after load failure");
            if (failure is "after" or "stop") Assert(completed, "load failure preserves deployment success");
        }
        Console.WriteLine("PASS: load options, ingress overrides, preflight, continuous session, timing, errors and disposal.");
    }
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    sealed class Runner(List<string> events, string failure) : IDeploymentCommandRunner
    {
        public Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
        {
            var args = arguments.ToArray();
            foreach (var command in new[] { "delete", "install", "upgrade" })
                if (args.Contains(command))
                {
                    events.Add(command);
                    if (failure == command) throw new InvalidOperationException(command);
                }
            if (args.Contains("status")) events.Add("ready");
            return Task.CompletedTask;
        }
    }
    sealed class FakeLoadFactory(List<string> events, string failure) : ILoadTestFactory
    {
        public Task<ILoadTestSession> PrepareAsync(string values, LoadTestOptions options, string context,
            string testNamespace, Action<LoadTestProgress> progress, CancellationToken ct)
        {
            events.Add("preflight");
            if (failure == "preflight") throw new InvalidOperationException("preflight");
            return Task.FromResult<ILoadTestSession>(new Session(events, failure));
        }
    }
    sealed class Session(List<string> events, string failure) : ILoadTestSession
    {
        Task Event(string name, string? entry = null)
        {
            events.Add(entry ?? name);
            if (failure == name) throw new InvalidOperationException(name);
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken ct) => Event("start");
        public Task StopAsync(CancellationToken ct) => Event("stop");
        public Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken ct)
        { events.Add("guard"); return action(ct); }
        public Task WaitAsync(string phase, int seconds, CancellationToken ct) =>
            Event(phase == "Przed deployem" ? "before" : "after", $"{(phase == "Przed deployem" ? "before" : "after")}:{seconds}");
        public ValueTask DisposeAsync() { events.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
