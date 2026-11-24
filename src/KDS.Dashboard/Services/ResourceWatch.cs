using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace KDS.Dashboard.Services;

public sealed record ResourceList<T>(IReadOnlyList<T> Items, string ResourceVersion);
public sealed record ResourceSnapshot<T>(IReadOnlyList<T> Items, bool Connected, string? Error);

/// <summary>List/watch cache keyed by UID. Each resource kind owns its own resourceVersion.</summary>
public sealed class ResourceWatch<T>(
    Func<CancellationToken, Task<ResourceList<T>>> list,
    Func<string, Action, CancellationToken, IAsyncEnumerable<(WatchEventType, T)>> watch,
    Func<ResourceSnapshot<T>, Task> publish,
    TimeSpan? retryDelay = null) where T : IKubernetesObject<V1ObjectMeta>
{
    private readonly Dictionary<string, T> _items = [];
    private string? _version;
    private Task Publish(bool connected, string? error = null) => publish(new(_items.Values.ToArray(), connected, error));

    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_version is null)
                {
                    var initial = await list(ct);
                    _items.Clear();
                    foreach (var item in initial.Items) _items[item.Metadata.Uid] = item;
                    _version = initial.ResourceVersion;
                    await Publish(false, "Łączenie watch…");
                }
                // Opening the HTTP stream confirms connectivity even when it has no events yet.
                Task connected = Task.CompletedTask;
                await foreach (var (type, item) in watch(_version, () => connected = Publish(true), ct).WithCancellation(ct))
                {
                    await connected;
                    if (type == WatchEventType.Error) throw new KubernetesException(new V1Status { Code = 410 });
                    _version = item.Metadata.ResourceVersion ?? _version;
                    if (type == WatchEventType.Bookmark) continue;
                    if (type == WatchEventType.Deleted) _items.Remove(item.Metadata.Uid);
                    else if (type is WatchEventType.Added or WatchEventType.Modified) _items[item.Metadata.Uid] = item;
                    failures = 0;
                    await Publish(true);
                }
                await connected;
                failures = 0;
                await Publish(false, "Wznawianie watch…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (RequiresRelist(ex)) _version = null;
                failures = Math.Min(failures + 1, 5);
                await Publish(false, $"Dane mogą być nieaktualne. Ponawianie połączenia: {ex.Message}");
            }
            try { await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, failures))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static bool RequiresRelist(Exception ex) => ex switch
    {
        KubernetesException k => k.Status?.Code is 410 or 404,
        HttpOperationException h => h.Response?.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound,
        _ => false
    };
}
