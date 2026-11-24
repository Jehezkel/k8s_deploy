using System.ComponentModel.DataAnnotations;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace KDS.Dashboard.Services;

public class KubernetesService
{
    private readonly ILogger<KubernetesService> _logger;
    private readonly KubernetesWatchClient _kubernetes;
    private readonly KubernetesOptions _kubernetesOptions;

    public KubernetesService(ILogger<KubernetesService> logger, KubernetesWatchClient kubernetes,
        IOptions<KubernetesOptions> options)
    {
        _logger = logger;
        _kubernetes = kubernetes;
        _kubernetesOptions = options.Value;
    }

    public async Task<V1PodList> GetInitialPodsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pods = await _kubernetes.CoreV1
                .ListNamespacedPodAsync(_kubernetesOptions.Namespace, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return pods;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            throw;
        }
    }

    public Task<V1ReplicaSetList> GetInitialReplicaSetsAsync(CancellationToken cancellationToken = default)
        => _kubernetes.AppsV1.ListNamespacedReplicaSetAsync(
            _kubernetesOptions.Namespace, cancellationToken: cancellationToken);

    public Task<V1DeploymentList> GetInitialDeploymentsAsync(CancellationToken cancellationToken = default)
        => _kubernetes.AppsV1.ListNamespacedDeploymentAsync(_kubernetesOptions.Namespace, cancellationToken: cancellationToken);

    public IAsyncEnumerable<(WatchEventType, V1Deployment)> WatchDeploymentsAsync(string resourceVersion,
        Action connected, CancellationToken ct = default)
        => _kubernetes.WatchResourcesAsync<V1Deployment>(
            $"apis/apps/v1/namespaces/{Uri.EscapeDataString(_kubernetesOptions.Namespace)}/deployments", resourceVersion, connected, ct);

    public async Task PrepareNamespace()
    {
        var namespaceFound = await _kubernetes.CoreV1.ReadNamespaceAsync(_kubernetesOptions.Namespace);
        if (namespaceFound is null)
        {
            _logger.LogInformation("Namespace not found");
            await CreateNamespace(_kubernetesOptions.Namespace);
            _logger.LogInformation("Created namespace");
        }
    }

    public async Task DeleteNamespace(string ns)
    {
        await _kubernetes.DeleteNamespaceAsync(ns);
    }

    private async Task CreateNamespace(string name)
    {
        var namespacev1 = new V1Namespace()
            { ApiVersion = "v1", Kind = "Namespace", Metadata = new V1ObjectMeta { Name = name } };
        await _kubernetes.CoreV1.CreateNamespaceAsync(namespacev1);
    }

    public IAsyncEnumerable<(WatchEventType, V1Pod)> WatchPodsAsync(string resourceVersion,
        Action connected, CancellationToken ct = default)
        => _kubernetes.WatchResourcesAsync<V1Pod>(
            $"api/v1/namespaces/{Uri.EscapeDataString(_kubernetesOptions.Namespace)}/pods", resourceVersion, connected, ct);

    public IAsyncEnumerable<(WatchEventType, V1ReplicaSet)> WatchReplicaSetsAsync(string resourceVersion,
        Action connected, CancellationToken ct = default)
        => _kubernetes.WatchResourcesAsync<V1ReplicaSet>(
            $"apis/apps/v1/namespaces/{Uri.EscapeDataString(_kubernetesOptions.Namespace)}/replicasets", resourceVersion, connected, ct);

}

public class KubernetesOptions
{
    [Required]
    [ConfigurationKeyName("Namespace")]
    public string Namespace { get; set; } = null!;
}

public static class KubernetesInstaller
{
    public static IServiceCollection AddKubernetes(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KubernetesOptions>().Bind(configuration.GetSection("Kubernetes")).ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(_ => KubernetesClientConfiguration.BuildConfigFromConfigFile());
        services.AddSingleton(sp => new KubernetesWatchClient(sp.GetRequiredService<KubernetesClientConfiguration>()));
        services.AddSingleton<KubernetesService>();
        return services;
    }
}