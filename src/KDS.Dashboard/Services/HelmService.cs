using System.Text.RegularExpressions;
using k8s;
using Microsoft.Extensions.Options;
using YamlDotNet.RepresentationModel;

namespace KDS.Dashboard.Services;

public sealed class HelmService(
    IDeploymentCommandRunner commands,
    KubernetesClientConfiguration kubeConfig,
    IOptions<KubernetesOptions> options,
    IConfiguration configuration,
    ILoadTestFactory? loadTests = null, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Namespace => options.Value.Namespace;
    public string Context => kubeConfig.CurrentContext;
    private string ChartPath => configuration["Helm:ChartPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "Helm", "target-app");
    public const string Release = "demo";

    public async Task<string> ReadValuesAsync()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(await File.ReadAllTextAsync(Path.Combine(ChartPath, "values.yaml"))));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (root.Children.TryGetValue(new YamlScalarNode("image"), out var node) && node is YamlMappingNode image)
            image.Children.Remove(new YamlScalarNode("tag"));
        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    public async Task RunTestAsync(string values, bool forceRebuild, Func<string, Task> report,
        CancellationToken cancellationToken = default, LoadTestOptions? loadTest = null,
        Action<LoadTestProgress>? loadProgress = null, Action? deploymentCompleted = null, Action<DeploymentStageEvent>? stage = null, Guid? testId = null)
    {
        var runId = testId ?? Guid.NewGuid();
        void Emit(DeploymentStage value, string? image = null) => stage?.Invoke(new(runId, DateTimeOffset.UtcNow, $"{Release}-target", value, image));
        loadTest?.Validate();
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Inny test już trwa. Poczekaj na jego zakończenie.");

        var temp = Path.Combine(Path.GetTempPath(), $"kds-deploy-{Guid.NewGuid():N}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds((loadTest?.BeforeSeconds ?? 0) + (loadTest?.AfterSeconds ?? 0)));
        using var recordingDeadline = (timeProvider ?? TimeProvider.System).CreateTimer(_ => timeout.Cancel(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ILoadTestSession? loadSession = null;
        var ct = timeout.Token;
        try
        {
            if (loadTest is not null) values = LoadTestOptions.PrepareValues(values).Values;
            const string initialTag = "v1";
            const string targetTag = "v2";
            var repository = ValidateValues(values);
            if (string.IsNullOrWhiteSpace(Context))
                throw new InvalidOperationException("Brak kontekstu Kubernetes w konfiguracji.");
            if (!Regex.IsMatch(Namespace, @"\A[a-z0-9]([-a-z0-9]*[a-z0-9])?\z") ||
                Namespace.Length > 63 || Namespace == "default" || Namespace.StartsWith("kube-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test wymaga dedykowanego namespace, innego niż default i kube-*.");

            Directory.CreateDirectory(temp);
            var chart = Path.Combine(temp, "chart");
            // Both releases use exactly the same chart and submitted values.
            foreach (var file in Directory.EnumerateFiles(ChartPath, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(chart, Path.GetRelativePath(ChartPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var valuesFile = Path.Combine(temp, "values.yaml");
            await File.WriteAllTextAsync(valuesFile, values, ct);
            await report("Walidacja values i obu wersji chartu…");
            await commands.RunAsync("kubectl", ["version", "--client"], ct);
            foreach (var tag in new[] { initialTag, targetTag })
            {
                await commands.RunAsync("helm", ["lint", chart, "--strict", "-f", valuesFile, "--set-string", $"image.tag={tag}"], ct);
                await commands.RunAsync("helm", ["template", Release, chart, "-n", Namespace, "-f", valuesFile, "--set-string", $"image.tag={tag}"], ct);
            }

            if (loadTest is not null)
            {
                await report("Przygotowanie k6 i tunelu Traefik…");
                loadSession = await (loadTests ?? throw new InvalidOperationException("Brak serwisu k6."))
                    .PrepareAsync(values, loadTest, Context, Namespace, loadProgress ?? (_ => { }), ct);
            }
            async Task InstallInitial(CancellationToken token)
            {
                await PrepareImagesAsync(repository, forceRebuild, report, token);

                Emit(DeploymentStage.Reset);
                await report($"Czyszczenie namespace {Namespace}…");
                await commands.RunAsync("kubectl", ["--context", Context, "delete", "namespace", Namespace, "--ignore-not-found=true", "--wait=true", "--timeout=5m"], token);
                if (loadTest is null) recordingDeadline.Change(TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
                Emit(DeploymentStage.NamespaceCreating);
                await commands.RunAsync("kubectl", ["--context", Context, "create", "namespace", Namespace], token);

                Emit(DeploymentStage.InstallStarted, $"{repository}:{initialTag}");
                await report($"Instalacja obrazu :{initialTag}; oczekiwanie na Ready…");
                await commands.RunAsync("helm", ["install", Release, chart, "--kube-context", Context, "-n", Namespace,
                    "-f", valuesFile, "--set-string", $"image.tag={initialTag}", "--wait", "--timeout", "5m"], token);
                await WaitForRolloutAsync(token);
                Emit(DeploymentStage.InstallReady);
            }
            if (loadSession is null) await InstallInitial(ct);
            else await loadSession.GuardAsync(InstallInitial, ct);

            if (loadSession is not null)
            {
                await report("Start k6 po Ready v1…");
                await loadSession.StartAsync(ct);
                await report("Ruch przed deployem…");
                await loadSession.WaitAsync("Przed deployem", loadTest!.BeforeSeconds, ct);
            }
            await report($"Wersja startowa gotowa. Aktualizacja image.tag: {initialTag} → {targetTag}…");
            async Task Upgrade(CancellationToken token)
            {
                Emit(DeploymentStage.UpgradeStarted, $"{repository}:{targetTag}");
                await commands.RunAsync("helm", ["upgrade", Release, chart, "--kube-context", Context, "-n", Namespace,
                    "--reuse-values", "--set-string", $"image.tag={targetTag}", "--wait", "--timeout", "5m"], token);
                await WaitForRolloutAsync(token);
                Emit(DeploymentStage.UpgradeReady);
                deploymentCompleted?.Invoke();
            }
            if (loadSession is null) await Upgrade(ct);
            else
            {
                await loadSession.GuardAsync(Upgrade, ct);
                await report("v2 Ready — ruch po deployu…");
                await loadSession.WaitAsync("Po deployu", loadTest!.AfterSeconds, ct);
                await loadSession.StopAsync(ct);
            }
            Emit(DeploymentStage.Completed);
            await report($"Gotowe — rollout obrazu :{targetTag} zakończony.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Emit(DeploymentStage.Failed);
            throw new TimeoutException("Przekroczono czas testu. Sprawdź stan namespace przed kolejną próbą.");
        }
        catch
        {
            Emit(DeploymentStage.Failed);
            throw;
        }
        finally
        {
            try
            {
                try { if (loadSession is not null) await loadSession.DisposeAsync(); }
                finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            }
            finally { _gate.Release(); }
        }
    }

    private async Task PrepareImagesAsync(string repository, bool forceRebuild,
        Func<string, Task> report, CancellationToken ct)
    {
        var engine = configuration["ImageBuild:Engine"] ?? "moby";
        var executable = engine switch
        {
            "moby" => "docker",
            "containerd" => "nerdctl",
            _ => throw new InvalidOperationException("ImageBuild:Engine musi mieć wartość moby lub containerd.")
        };
        string[] prefix = engine == "moby"
            ? ["--context", configuration["ImageBuild:DockerContext"] ?? "rancher-desktop"]
            : ["--namespace", "k8s.io"];
        var source = $"{repository}:v1";
        var target = $"{repository}:v2";
        if (forceRebuild)
        {
            var root = FindSourceRoot();
            await report("Force rebuild — budowanie aktualnego kodu jako v1…");
            await commands.RunAsync(executable, [.. prefix, "build", "--no-cache", "-t", source,
                "-f", Path.Combine(root, "src", "KDS.TargetApp", "Dockerfile"), Path.Combine(root, "src")], ct);
        }
        else
        {
            await report("Używanie istniejącego obrazu v1 (bez przebudowy)…");
            try { await commands.RunAsync(executable, [.. prefix, "image", "inspect", source], ct); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Nie można odczytać {source}. Przy pierwszym teście zaznacz Force rebuild. {ex.Message}", ex);
            }
        }
        // Tag the same image rather than running a second build: both tags must be identical.
        await report("Przypisywanie tagu v2 do tego samego obrazu…");
        await commands.RunAsync(executable, [.. prefix, "tag", source, target], ct);
    }

    private string FindSourceRoot()
    {
        var configured = configuration["ImageBuild:SourceRoot"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var root = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(root, "src", "KDS.TargetApp", "Dockerfile"))) return root;
            throw new InvalidOperationException("ImageBuild:SourceRoot nie zawiera src/KDS.TargetApp/Dockerfile.");
        }
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "KDS.TargetApp", "Dockerfile")))
                    return directory.FullName;
        throw new InvalidOperationException("Nie znaleziono kodu TargetApp. Ustaw ImageBuild:SourceRoot na katalog repozytorium.");
    }

    private Task WaitForRolloutAsync(CancellationToken ct) => commands.RunAsync("kubectl",
        ["--context", Context, "-n", Namespace, "rollout", "status", $"deployment/{Release}-target", "--timeout=5m"], ct);

    public static string ValidateValues(string values)
    {
        if (string.IsNullOrWhiteSpace(values) || values.Length > 100_000)
            throw new ArgumentException("Values musi zawierać YAML (maksymalnie 100 KB).");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(values));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            !root.Children.TryGetValue(new YamlScalarNode("image"), out var imageNode) || imageNode is not YamlMappingNode image)
            throw new ArgumentException("Values musi zawierać mapę image z repository.");
        static string Scalar(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
                ? scalar.Value ?? "" : "";
        var repository = Scalar(image, "repository");
        if (!Regex.IsMatch(repository, @"\A[a-z0-9][a-z0-9._:/-]*\z"))
            throw new ArgumentException("Podaj poprawne image.repository bez tagu i digestu.");
        if (Scalar(image, "pullPolicy") != "Never")
            throw new ArgumentException("Lokalny test v1 → v2 wymaga image.pullPolicy: Never.");
        if (!int.TryParse(Scalar(root, "replicaCount"), out var replicas) || replicas < 1)
            throw new ArgumentException("Test wymaga replicaCount większego od zera.");
        return repository;
    }
}
