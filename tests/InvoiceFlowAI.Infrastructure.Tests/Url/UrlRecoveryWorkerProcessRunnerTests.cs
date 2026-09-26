using System.Diagnostics;
using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryWorkerProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invoiceflow-worker-runner-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Timeout_terminates_worker_process_tree()
    {
        Directory.CreateDirectory(_root);
        var pidFile = Path.Combine(_root, "child.pid");
        var manifest = await CreateManifestAsync(pidFile);
        var runner = new UrlRecoveryWorkerProcessRunner(FixtureExecutablePath());

        var act = () => runner.RunAsync(manifest, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryWorkerTimeoutException>();
        var childProcessId = await ReadChildProcessIdAsync(pidFile);
        await WaitForExitAsync(childProcessId);
        IsProcessRunning(childProcessId).Should().BeFalse();
    }

    [Fact]
    public async Task Caller_cancellation_terminates_worker_process_tree()
    {
        Directory.CreateDirectory(_root);
        var pidFile = Path.Combine(_root, "child.pid");
        var manifest = await CreateManifestAsync(pidFile);
        var runner = new UrlRecoveryWorkerProcessRunner(FixtureExecutablePath());
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync(manifest, TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            var childProcessId = await ReadChildProcessIdAsync(pidFile);
            cancellation.Cancel();

            var act = async () => await run;

            await act.Should().ThrowAsync<OperationCanceledException>();
            await WaitForExitAsync(childProcessId);
            IsProcessRunning(childProcessId).Should().BeFalse();
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<string> CreateManifestAsync(string pidFile)
    {
        var manifest = Path.Combine(_root, "request.json");
        await File.WriteAllTextAsync(manifest, pidFile);
        return manifest;
    }

    private static string FixtureExecutablePath()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "InvoiceFlowAI.UrlRecovery.WorkerProcessFixture.exe"
            : "InvoiceFlowAI.UrlRecovery.WorkerProcessFixture";
        return Path.Combine(AppContext.BaseDirectory, "worker-process-fixture", executableName);
    }

    private static async Task<int> ReadChildProcessIdAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    var contents = await File.ReadAllTextAsync(pidFile);
                    if (int.TryParse(contents, out var processId)) return processId;
                }
                catch (IOException)
                {
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Worker fixture did not start its child process.");
    }

    private static async Task WaitForExitAsync(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && IsProcessRunning(processId)) await Task.Delay(25);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}