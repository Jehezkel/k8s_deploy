using System.Diagnostics;

namespace KDS.Dashboard.Services;

/// <summary>A server-owned process with continuously drained output and bounded shutdown.</summary>
public interface ILoadTestProcess : IAsyncDisposable
{
    Task Completion { get; }
    int ExitCode { get; }
}

public interface ILoadTestProcessRunner
{
    ILoadTestProcess Start(string executable, IEnumerable<string> arguments, Action<string> output,
        IDictionary<string, string>? environment = null);
}

public sealed class LoadTestProcessRunner : ILoadTestProcessRunner
{
    public ILoadTestProcess Start(string executable, IEnumerable<string> arguments, Action<string> output,
        IDictionary<string, string>? environment = null) => new ManagedProcess(executable, arguments, output, environment);
}

internal sealed class ManagedProcess : ILoadTestProcess
{
    private readonly Process _process;
    private readonly Task _stdout, _stderr;
    public Task Completion { get; }
    public int ExitCode => _process.ExitCode;

    public ManagedProcess(string executable, IEnumerable<string> arguments, Action<string> output,
        IDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        _process = Process.Start(start) ?? throw new InvalidOperationException($"Nie udało się uruchomić {executable}.");
        _stdout = DrainAsync(_process.StandardOutput, output);
        _stderr = DrainAsync(_process.StandardError, output);
        Completion = CompleteAsync();
    }

    private async Task CompleteAsync()
    {
        await _process.WaitForExitAsync();
        await Task.WhenAll(_stdout, _stderr);
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> output)
    {
        while (await reader.ReadLineAsync() is { } line) output(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await Completion;
        _process.Dispose();
    }
}
