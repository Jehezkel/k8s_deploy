using KDS.Dashboard.Services;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

static class DeadlineChecks
{
    public static async Task RunAsync(KubernetesClientConfiguration kube, IConfiguration config)
    {
        foreach (var blocked in new[] { "create", "install", "upgrade", "status" })
        {
            var clock = new DeadlineClock();
            var runner = new DeadlineRunner(clock, blocked);
            var helm = new HelmService(runner, kube, Options.Create(new KubernetesOptions { Namespace = "kds-test" }), config, timeProvider: clock);
            var stages = new List<DeploymentStageEvent>();
            try
            {
                await helm.RunTestAsync(await helm.ReadValuesAsync(), false, _ => Task.CompletedTask, stage: stages.Add);
                throw new Exception("Deadline did not cancel " + blocked);
            }
            catch (TimeoutException) { }
            if (clock.Due != TimeSpan.FromSeconds(120) || !stages.Any(s => s.Stage == DeploymentStage.NamespaceCreating) || stages.Last().Stage != DeploymentStage.Failed)
                throw new Exception("Wrong deadline boundary");
        }
        Console.WriteLine("PASS: shared 120-second deadline cancels namespace creation, install, upgrade and readiness wait.");
    }
    private sealed class DeadlineRunner(DeadlineClock clock, string blocked) : IDeploymentCommandRunner
    {
        public async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (args.Contains("delete") && clock.Due != Timeout.InfiniteTimeSpan) throw new Exception("Deadline started before create");
            if (args.Contains(blocked))
            {
                clock.Fire();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        public TimeSpan Due = Timeout.InfiniteTimeSpan;
        private TimerCallback? _callback;
        private object? _state;
        public void Fire() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; Due = dueTime; return new Timer(this); }
        private sealed class Timer(DeadlineClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) { clock.Due = dueTime; return true; }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
