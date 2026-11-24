using k8s.Models;

namespace KDS.Dashboard.Services;

/// <summary>One set of watches per server, independent of browser circuits.</summary>
public sealed class ClusterMonitor(KubernetesService kubernetes, ILogger<ClusterMonitor> logger, RunArchive? archive = null) : BackgroundService
{
    private readonly object _sync = new();
    public ResourceSnapshot<V1Pod> Pods { get; private set; } = new([], false, null);
    public ResourceSnapshot<V1ReplicaSet> ReplicaSets { get; private set; } = new([], false, null);
    public ResourceSnapshot<V1Deployment> Deployments { get; private set; } = new([], false, null);
    public event Action? Changed;
    public (ResourceSnapshot<V1Pod> Pods, ResourceSnapshot<V1ReplicaSet> ReplicaSets) Resources
    { get { lock (_sync) return (Pods, ReplicaSets); } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pods = new ResourceWatch<V1Pod>(async ct =>
        {
            var list = await kubernetes.GetInitialPodsAsync(ct);
            return new(list.Items.ToArray(), list.Metadata.ResourceVersion);
        }, kubernetes.WatchPodsAsync, snapshot =>
        {
            lock (_sync)
            {
                Pods = snapshot;
                Record();
            }
            Notify();
            return Task.CompletedTask;
        });
        var sets = new ResourceWatch<V1ReplicaSet>(async ct =>
        {
            var list = await kubernetes.GetInitialReplicaSetsAsync(ct);
            return new(list.Items.ToArray(), list.Metadata.ResourceVersion);
        }, kubernetes.WatchReplicaSetsAsync, snapshot =>
        {
            lock (_sync)
            {
                ReplicaSets = snapshot;
                Record();
            }
            Notify();
            return Task.CompletedTask;
        });
        var deployments = new ResourceWatch<V1Deployment>(async ct =>
        {
            var list = await kubernetes.GetInitialDeploymentsAsync(ct);
            return new(list.Items.ToArray(), list.Metadata.ResourceVersion);
        }, kubernetes.WatchDeploymentsAsync, snapshot =>
        {
            lock (_sync) { Deployments = snapshot; Record(); }
            Notify();
            return Task.CompletedTask;
        });
        await Task.WhenAll(pods.RunAsync(stoppingToken), sets.RunAsync(stoppingToken), deployments.RunAsync(stoppingToken), TickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(ct)) { Notify(); } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public RunFrame Capture(DateTimeOffset time)
    {
        lock (_sync) return new(time, Pods, ReplicaSets, Deployments);
    }

    public void Record(DateTimeOffset? time = null)
    {
        lock (_sync) archive?.Observe(new(time ?? DateTimeOffset.UtcNow, Pods, ReplicaSets, Deployments));
    }

    private void Notify()
    {
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); }
            catch (Exception ex) { logger.LogWarning(ex, "Monitoring subscriber failed"); }
    }
}
