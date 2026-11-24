using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KDS.Dashboard.Services;

public sealed class LoadTestFactory(IConfiguration configuration, ILoadTestProcessRunner? processes = null) : ILoadTestFactory
{
    private readonly ILoadTestProcessRunner _processes = processes ?? new LoadTestProcessRunner();
    public async Task<ILoadTestSession> PrepareAsync(string values, LoadTestOptions options, string context,
        string testNamespace, Action<LoadTestProgress> progress, CancellationToken ct)
    {
        options.Validate();
        var (_, host, tls) = LoadTestOptions.PrepareValues(values);
        var script = Path.Combine(AppContext.BaseDirectory, "LoadTests", "deployment.js");
        if (!File.Exists(script)) throw new InvalidOperationException("Brak skryptu LoadTests/deployment.js.");
        await CaptureAsync("k6", ["version"], ct);
        var ns = configuration["LoadTest:TraefikNamespace"];
        var name = configuration["LoadTest:TraefikService"];
        if (string.IsNullOrEmpty(ns) != string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Ustaw razem LoadTest:TraefikNamespace i LoadTest:TraefikService.");
        var args = new List<string> { "--context", context, "get", "services" };
        if (name is not null) args.AddRange([name, "-n", ns!]);
        else args.AddRange(["-A", "-l", "app.kubernetes.io/name=traefik"]);
        args.AddRange(["-o", "json"]);
        using var json = JsonDocument.Parse(await CaptureAsync("kubectl", args, ct));
        var candidates = (name is null ? json.RootElement.GetProperty("items").EnumerateArray().ToArray() : [json.RootElement])
            .Where(s => s.GetProperty("metadata").GetProperty("namespace").GetString() != testNamespace).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("Wymagany jeden Service Traefik poza namespace testu. Ustaw LoadTest:TraefikNamespace i LoadTest:TraefikService.");
        var service = candidates[0];
        ns = service.GetProperty("metadata").GetProperty("namespace").GetString()!;
        name = service.GetProperty("metadata").GetProperty("name").GetString()!;
        var ports = service.GetProperty("spec").GetProperty("ports").EnumerateArray()
            .Where(p => p.GetProperty("port").GetInt32() == (tls ? 443 : 80)).ToArray();
        if (ports.Length != 1) throw new InvalidOperationException("Service Traefik nie udostępnia wymaganego portu 80/443.");
        var directory = Path.GetFullPath(Path.Combine(configuration["LoadTest:ArtifactsPath"] ?? "artifacts",
            $"kds-load-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        var session = new Session(options, host, tls, script, directory, progress, _processes);
        try
        {
            await session.OpenTunnelAsync(context, ns, name, ports[0].GetProperty("port").GetInt32(), ct);
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private async Task<string> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var process = _processes.Start(exe, args, line => { lock (lines) lines.Add(line); });
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{exe}: {string.Join('\n', lines)}");
        return string.Join('\n', lines);
    }

    private sealed class Session(LoadTestOptions options, string host, bool tls, string script,
        string directory, Action<LoadTestProgress> progress, ILoadTestProcessRunner processes) : ILoadTestSession
    {
        private ILoadTestProcess? _tunnel, _k6;
        private int _port;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly CancellationTokenSource _monitorStop = new();
        private Task? _monitor;
        private readonly object _sync = new();
        private LoadTestProgress _state = new("Przygotowanie", Artifacts: directory);
        private double _lastRequests;
        private long _lastSample = Stopwatch.GetTimestamp();
        private bool _stopped;

        private void Log(string file, string line)
        {
            lock (_sync) File.AppendAllText(Path.Combine(directory, file), line + Environment.NewLine);
        }
        private void Phase(string phase, int? remaining = null)
        {
            lock (_sync)
            {
                if (_state.Phase != phase) Log("phases.jsonl", JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, phase }));
                _state = _state with { Phase = phase, RemainingSeconds = remaining };
                progress(_state);
            }
        }
        public async Task OpenTunnelAsync(string context, string ns, string name, int remote, CancellationToken ct)
        {
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tunnel = processes.Start("kubectl", ["--context", context, "-n", ns, "port-forward",
                $"service/{name}", $":{remote}", "--address", "127.0.0.1"], line =>
                {
                    Log("tunnel.log", line);
                    var match = Regex.Match(line, @"Forwarding from 127\.0\.0\.1:(\d+)");
                    if (match.Success) ready.TrySetResult(int.Parse(match.Groups[1].Value));
                });
            var winner = await Task.WhenAny(ready.Task, _tunnel.Completion).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (winner != ready.Task) throw new InvalidOperationException("Tunel Traefik zakończył się przed uzyskaniem gotowości.");
            _port = await ready.Task;
            Phase("Tunel Traefik gotowy");
        }
        public async Task StartAsync(CancellationToken ct)
        {
            // The API port reservation is released immediately before process creation; collisions fail startup safely.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var apiPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            _http.BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/");
            _k6 = processes.Start("k6", ["run", "--address", $"127.0.0.1:{apiPort}", "--out",
                $"json={Path.Combine(directory, "metrics.json")}", script], line => Log("k6.log", line),
                new Dictionary<string, string>
                {
                    ["SUMMARY_PATH"] = Path.Combine(directory, "summary.json"),
                    ["TARGET_HOST"] = host, ["TARGET_URL"] = $"{(tls ? "https" : "http")}://{host}/",
                    ["TUNNEL_PORT"] = _port.ToString(), ["USERS"] = options.Users.ToString(),
                    ["MAX_SECONDS"] = (options.BeforeSeconds + options.AfterSeconds + 660).ToString(),
                    ["NO_PROXY"] = "*", ["no_proxy"] = "*"
                });
            await GuardAsync(async token =>
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    try
                    {
                        using var status = await ReadAsync("v1/status", token);
                        var a = status.RootElement.GetProperty("data").GetProperty("attributes");
                        if (a.GetProperty("running").GetBoolean() && a.GetProperty("vus").GetInt32() == options.Users)
                        {
                            _monitor = MonitorAsync(_monitorStop.Token);
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                    await Task.Delay(100, token);
                }
                throw new TimeoutException("k6 nie osiągnął gotowości w 30 s.");
            }, ct);
        }
        public async Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_tunnel!.Completion.IsCompleted || (_k6 is not null && !_stopped && _k6.Completion.IsCompleted))
                throw new InvalidOperationException("Proces k6 lub tunel Traefik nie działa. Logi: " + directory);
            var work = action(linked.Token);
            var watched = new List<Task> { work, _tunnel!.Completion };
            if (_k6 is not null && !_stopped) watched.Add(_k6.Completion);
            if (_monitor is not null && !_stopped) watched.Add(_monitor);
            var done = await Task.WhenAny(watched);
            if (done == work) { await work; return; }
            await linked.CancelAsync();
            try { await work; } catch { }
            ct.ThrowIfCancellationRequested();
            if (done == _monitor) await _monitor!;
            throw new InvalidOperationException("Proces k6 lub tunel Traefik zakończył się przed końcem testu. Sprawdź logi: " + directory);
        }
        public async Task WaitAsync(string phase, int seconds, CancellationToken ct)
        {
            Phase(phase, seconds);
            await GuardAsync(async token =>
            {
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed.TotalSeconds < seconds)
                {
                    Phase(phase, Math.Max(0, seconds - (int)elapsed.Elapsed.TotalSeconds));
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, seconds * 1000 - elapsed.Elapsed.TotalMilliseconds))), token);
                }
            }, ct);
            Phase(phase, 0);
            if (phase == "Przed deployem") Phase("Deploy v2");
        }
        private async Task<JsonDocument> ReadAsync(string path, CancellationToken ct)
        {
            using var response = await _http.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        private async Task SampleAsync(CancellationToken ct)
        {
            using var json = await ReadAsync("v1/metrics", ct);
            var samples = json.RootElement.GetProperty("data").EnumerateArray().ToDictionary(
                m => m.GetProperty("id").GetString()!, m => m.GetProperty("attributes").GetProperty("sample"));
            double Get(string metric, string field) => samples.TryGetValue(metric, out var s) &&
                s.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var requests = Get("http_reqs", "count");
            var now = Stopwatch.GetTimestamp();
            lock (_sync)
            {
                _state = _state with { Users = (int)Get("vus", "value"), Requests = requests,
                    HttpErrors = Get("target_http_errors", "count"), FailedChecks = Get("target_failed_checks", "count"),
                    Rps = Math.Max(0, requests - _lastRequests) / Math.Max(.001, Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds),
                    P95 = Get("http_req_duration", "p(95)"), SampleTime = DateTimeOffset.UtcNow };
                _lastRequests = requests;
                _lastSample = now;
                Log("samples.jsonl", JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, metrics = _state }));
                progress(_state);
            }
        }
        private async Task MonitorAsync(CancellationToken ct)
        {
            while (true)
            {
                await SampleAsync(ct);
                await Task.Delay(1000, ct);
            }
        }
        public async Task StopAsync(CancellationToken ct)
        {
            Phase("Kończenie k6");
            await _monitorStop.CancelAsync();
            if (_monitor is not null)
                try { await _monitor; } catch (OperationCanceledException) { }
            await SampleAsync(ct);
            _stopped = true;
            using var response = await _http.PatchAsync("v1/status", new StringContent(
                "{\"data\":{\"type\":\"status\",\"id\":\"default\",\"attributes\":{\"stopped\":true}}}",
                System.Text.Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            await _k6!.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
            // k6 0.55 returns ScriptStoppedFromRESTAPI (103) for our explicit stop request.
            if (_k6.ExitCode is not (0 or 103)) throw new InvalidOperationException($"k6 zakończył się kodem {_k6.ExitCode}. Logi: {directory}");
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "summary.json"), ct));
            var metrics = summary.RootElement.GetProperty("metrics");
            double Final(string metric, string field) => metrics.TryGetProperty(metric, out var m) &&
                m.GetProperty("values").TryGetProperty(field, out var v) ? v.GetDouble() : 0;
            lock (_sync)
            {
                _state = _state with { Requests = Final("http_reqs", "count"), HttpErrors = Final("http_req_failed", "passes"),
                    FailedChecks = Final("checks", "fails"), P95 = Final("http_req_duration", "p(95)") };
                _state = _state with { Users = 0, Rps = 0, Result = _state.HttpErrors > 0 || _state.FailedChecks > 0 ? "Zarejestrowano błędy requestów" : "Bez błędów requestów" };
                Phase("Zakończony");
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _monitorStop.CancelAsync();
            if (_monitor is not null) try { await _monitor; } catch { }
            try { if (_k6 is not null) await _k6.DisposeAsync(); }
            finally
            {
                if (_tunnel is not null) await _tunnel.DisposeAsync();
                _http.Dispose();
                _monitorStop.Dispose();
            }
        }
    }
}
