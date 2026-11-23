using System.ComponentModel.DataAnnotations;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace KDS.Dashboard.Services;

public class KubernetesService
{
    private readonly ILogger<KubernetesService> _logger;
    private readonly Kubernetes _kubernetes;
    private readonly KubernetesOptions _kubernetesOptions;

    public KubernetesService(ILogger<KubernetesService> logger, Kubernetes kubernetes,
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
        CancellationToken ct = default)
    {
        return _kubernetes.CoreV1.WatchListNamespacedPodAsync(namespaceParameter: _kubernetesOptions.Namespace,
            resourceVersion: resourceVersion, cancellationToken: ct);
    }
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
        services.AddSingleton(_ =>
        {
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            return new Kubernetes(config);
        });
        services.AddSingleton<KubernetesService>();
        return services;
    }
}