using System.Threading.Channels;

namespace KDS.Dashboard.Services;

public sealed record DeploymentTestSnapshot(bool Running, string Stage, IReadOnlyList<string> Messages, string? Error, LoadTestProgress? LoadTest = null, bool DeploymentCompleted = false);

/// <summary>Owns the test for the lifetime of the server, never for the lifetime of a dialog/circuit.</summary>
public sealed class DeploymentTestService(HelmService helm, ILogger<DeploymentTestService> logger, RunArchive? archive = null, ClusterMonitor? monitor = null) : BackgroundService
{
    private readonly object _sync = new();
    private readonly Channel<(string Values, bool Rebuild, LoadTestOptions? LoadTest)> _requests = Channel.CreateUnbounded<(string, bool, LoadTestOptions?)>();
    private DeploymentTestSnapshot _state = new(false, "", [], null);
    public event Action? Changed;
    public DeploymentTestSnapshot Snapshot { get { lock (_sync) return _state; } }

    public async Task StartTestAsync(string values, bool rebuild, LoadTestOptions? loadTest = null)
    {
        loadTest?.Validate();
        if (loadTest is not null) LoadTestOptions.PrepareValues(values);
        DeployValues.ValidateShape(await helm.ReadValuesAsync(), values);
        HelmService.ValidateValues(values);
        lock (_sync)
        {
            if (_state.Running) throw new InvalidOperationException("Inny test już trwa. Poczekaj na jego zakończenie.");
            _state = new(true, "Przyjęto test…", ["Przyjęto test…"], null);
            if (!_requests.Writer.TryWrite((values, rebuild, loadTest)))
            {
                _state = new(false, "Nie przyjęto testu", [], "Serwis jest zatrzymywany.");
                throw new InvalidOperationException(_state.Error);
            }
        }
        Notify();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken))
            {
                string? error = null;
                var result = "Zakończony";
                DateTimeOffset? terminalTime = null;
                var testId = Guid.NewGuid();
                try
                {
                    await helm.RunTestAsync(request.Values, request.Rebuild, message =>
                    {
                        lock (_sync) _state = _state with { Stage = message, Messages = _state.Messages.Append(message).ToArray() };
                        Notify();
                        return Task.CompletedTask;
                    }, stoppingToken, request.LoadTest, progress =>
                    {
                        archive?.Progress(progress);
                        lock (_sync) _state = _state with { LoadTest = progress };
                        Notify();
                    }, () =>
                    {
                        lock (_sync) _state = _state with { DeploymentCompleted = true };
                        Notify();
                    }, stage: e =>
                    {
                        if (e.Stage == DeploymentStage.NamespaceCreating)
                        {
                            archive?.Begin(testId, e.Time, helm.Context, helm.Namespace, request.Values, request.Rebuild, request.LoadTest, monitor?.Capture(e.Time));
                        }
                        archive?.Stage(e);
                        if (e.Stage is DeploymentStage.Completed or DeploymentStage.Failed)
                        {
                            terminalTime = e.Time;
                            monitor?.Record(e.Time);
                            archive?.Complete(testId, e.Stage == DeploymentStage.Completed ? "Zakończony" : "Błąd", e.Time);
                        }
                    }, testId: testId);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    result = ex is TimeoutException ? "Przekroczono czas" : ex is OperationCanceledException ? "Przerwany" : "Błąd";
                    logger.LogError(ex, "Deployment test failed");
                }
                archive?.Complete(testId, result, terminalTime ?? DateTimeOffset.UtcNow);
                lock (_sync) _state = _state with { Running = false, Error = error,
                    LoadTest = _state.LoadTest is { } load ? load with { Users = 0, Rps = 0, RemainingSeconds = null,
                        Phase = error is null ? "Zakończony" : "Przerwany", Result = error is null ? load.Result : "Błąd techniczny" } : null,
                    Stage = error is null ? "Test zakończony" : "Test zakończony błędem" };
                Notify();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _requests.Writer.TryComplete(); }
    }

    private void Notify()
    {
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); }
            catch (Exception ex) { logger.LogWarning(ex, "Test status subscriber failed"); }
    }
}
