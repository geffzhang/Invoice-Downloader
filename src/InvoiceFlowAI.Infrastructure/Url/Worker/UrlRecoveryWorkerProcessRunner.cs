using System.Diagnostics;

namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public sealed class UrlRecoveryWorkerTimeoutException : Exception
{
    public UrlRecoveryWorkerTimeoutException()
        : base("URL recovery worker exceeded its time limit.")
    {
    }
}

public sealed class UrlRecoveryWorkerProcessRunner : IUrlRecoveryWorkerProcessRunner
{
    private readonly string _executablePath;

    public UrlRecoveryWorkerProcessRunner(string? executablePath = null)
    {
        _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "InvoiceFlowAI.UrlRecovery.Worker.exe" : "InvoiceFlowAI.UrlRecovery.Worker");
    }

    public async Task<int> RunAsync(string requestManifestPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestManifestPath);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(requestManifestPath))!,
        };
        startInfo.ArgumentList.Add("--job-manifest");
        startInfo.ArgumentList.Add(Path.GetFullPath(requestManifestPath));

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("URL recovery worker could not be started.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAndWaitAsync(process).ConfigureAwait(false);
            throw new UrlRecoveryWorkerTimeoutException();
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }
}