using System.Text.Json;
using KDS.Dashboard.Services;
using Microsoft.Extensions.Configuration;

static class LoadProcessChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kds-process-check-" + Guid.NewGuid());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["LoadTest:ArtifactsPath"] = root }).Build();
        var values = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Helm/target-app/values.yaml"));
        try
        {
            foreach (var mode in new[] { "missing-k6", "missing-traefik", "ambiguous", "tunnel-fails", "k6-exits", "tunnel-exits", "cancel" })
            {
                var processes = new Processes(mode);
                var factory = new LoadTestFactory(config, processes);
                var rejected = false;
                try
                {
                    await using var session = await factory.PrepareAsync(values, new(), "context", "kds-test", _ => { }, default);
                    if (mode == "k6-exits") await session.StartAsync(default);
                    if (mode == "tunnel-exits")
                    {
                        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var guard = session.GuardAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, default);
                        await entered.Task;
                        processes.Tunnel!.Done.SetResult();
                        await guard;
                    }
                    if (mode == "cancel")
                    {
                        using var stop = new CancellationTokenSource();
                        var guard = session.GuardAsync(ct => Task.Delay(Timeout.Infinite, ct), stop.Token);
                        stop.Cancel();
                        await guard;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { rejected = true; }
                if (!rejected || processes.Created.Any(p => !p.Disposed)) throw new Exception("Process cleanup check failed: " + mode);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: missing k6/controller, ambiguous controller, tunnel failure, premature process exit and cancellation cleanup.");
    }
    sealed class Process(bool complete) : ILoadTestProcess
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Task Completion => complete ? Task.CompletedTask : Done.Task;
        public int ExitCode => 0;
        public ValueTask DisposeAsync() { Disposed = true; Done.TrySetResult(); return ValueTask.CompletedTask; }
    }
    sealed class Processes(string mode) : ILoadTestProcessRunner
    {
        public List<Process> Created = [];
        public Process? Tunnel;
        public ILoadTestProcess Start(string executable, IEnumerable<string> arguments, Action<string> output, IDictionary<string, string>? environment = null)
        {
            var args = arguments.ToArray();
            if (executable == "k6" && mode == "missing-k6") throw new InvalidOperationException("k6 missing");
            var tunnel = args.Contains("port-forward");
            var process = new Process(!tunnel || mode == "tunnel-fails");
            Created.Add(process);
            if (args.Contains("get"))
            {
                var service = new { metadata = new { name = "traefik", @namespace = "kube-system" }, spec = new { ports = new[] { new { port = 80 } } } };
                output(JsonSerializer.Serialize(new { items = mode == "missing-traefik" ? Array.Empty<object>() : mode == "ambiguous" ? new object[] { service, service } : new object[] { service } }));
            }
            if (tunnel)
            {
                Tunnel = process;
                if (mode != "tunnel-fails") output("Forwarding from 127.0.0.1:43125 -> 8000");
            }
            return process;
        }
    }
}
