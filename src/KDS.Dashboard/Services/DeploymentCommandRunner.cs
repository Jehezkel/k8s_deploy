using System.Diagnostics;

namespace KDS.Dashboard.Services;

public interface IDeploymentCommandRunner
{
    Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken);
}

public sealed class DeploymentCommandRunner : IDeploymentCommandRunner
{
    public async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Nie udało się uruchomić {executable}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{executable}: {stderr}\n{stdout}".Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
