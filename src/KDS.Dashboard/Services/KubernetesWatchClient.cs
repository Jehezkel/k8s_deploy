using System.Runtime.CompilerServices;
using System.Text.Json;
using k8s;

namespace KDS.Dashboard.Services;

/// <summary>
/// Opens watch streams at response headers. The SDK's list-response adapter reads the first
/// event before returning, which leaves an idle but healthy watch looking disconnected.
/// Reuse the SDK transport for kubeconfig credentials, TLS and credential renewal.
/// </summary>
public sealed class KubernetesWatchClient(KubernetesClientConfiguration configuration, params DelegatingHandler[] handlers)
    : Kubernetes(configuration, handlers)
{
    public async IAsyncEnumerable<(WatchEventType, T)> WatchResourcesAsync<T>(string path, string resourceVersion,
        Action connected, [EnumeratorCancellation] CancellationToken ct)
    {
        // Bound even a half-open TCP connection; the server normally rotates the stream after 60s.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var watchToken = deadline.Token;
        var query = $"?watch=true&allowWatchBookmarks=true&timeoutSeconds=60&resourceVersion={Uri.EscapeDataString(resourceVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path + query));
        using var response = await SendRequestRaw("", request, watchToken);
        connected();
        using var stream = await response.Content.ReadAsStreamAsync(watchToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(watchToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("type").GetString() == "ERROR")
                throw new KubernetesException(KubernetesJson.Deserialize<k8s.Models.V1Status>(json.RootElement.GetProperty("object").GetRawText()));
            var item = KubernetesJson.Deserialize<Watcher<T>.WatchEvent>(line);
            yield return (item.Type, item.Object);
        }
    }
}
