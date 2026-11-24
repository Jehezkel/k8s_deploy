using YamlDotNet.RepresentationModel;

namespace KDS.Dashboard.Services;

public sealed record LoadTestOptions(int BeforeSeconds = 30, int AfterSeconds = 30, int Users = 10)
{
    public void Validate()
    {
        if (BeforeSeconds is < 0 or > 3600 || AfterSeconds is < 0 or > 3600 || Users is < 1 or > 1000)
            throw new ArgumentException("Czas musi wynosić 0–3600 s, a liczba użytkowników 1–1000.");
    }

    public static (string Values, string Host, bool Tls) PrepareValues(string values)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(values));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (!root.Children.TryGetValue(new YamlScalarNode("ingress"), out var node) || node is not YamlMappingNode ingress)
            throw new ArgumentException("Load test wymaga konfiguracji ingress w values.");
        var host = ingress.Children.TryGetValue(new YamlScalarNode("host"), out var h) ? ((YamlScalarNode)h).Value : null;
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Load test wymaga poprawnego ingress.host.");
        var tls = ingress.Children.TryGetValue(new YamlScalarNode("tls"), out var t) && t is YamlMappingNode map &&
            map.Children.TryGetValue(new YamlScalarNode("enabled"), out var enabled) && ((YamlScalarNode)enabled).Value == "true";
        ingress.Children[new YamlScalarNode("enabled")] = new YamlScalarNode("true");
        ingress.Children[new YamlScalarNode("className")] = new YamlScalarNode("traefik");
        using var writer = new StringWriter();
        yaml.Save(writer, false);
        return (writer.ToString(), host, tls);
    }
}

public sealed record LoadTestProgress(string Phase, int? RemainingSeconds = null, int Users = 0,
    double Requests = 0, double HttpErrors = 0, double FailedChecks = 0, double Rps = 0,
    double P95 = 0, string? Artifacts = null, string? Result = null, DateTimeOffset? SampleTime = null);

public interface ILoadTestSession : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken ct);
    Task WaitAsync(string phase, int seconds, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public interface ILoadTestFactory
{
    Task<ILoadTestSession> PrepareAsync(string values, LoadTestOptions options, string context,
        string testNamespace, Action<LoadTestProgress> progress, CancellationToken ct);
}
