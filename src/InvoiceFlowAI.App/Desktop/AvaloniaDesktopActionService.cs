using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Desktop;

public sealed class AvaloniaDesktopActionService(
    IDesktopRunService runs,
    IReportRunDataSource reportData,
    IArchiveArtifactStore artifacts,
    IReportApplicationService reports,
    IDesktopPathLauncher launcher,
    IWindowCommandDispatcher windowCommands) : IDesktopActionService
{
    private const string ActionFailed = "DESKTOP_ACTION_FAILED";
    private const string PathInvalid = "DESKTOP_PATH_INVALID";
    private const string PathNotFound = "DESKTOP_PATH_NOT_FOUND";
    private const string FileNotFound = "DESKTOP_FILE_NOT_FOUND";

    public async Task<DesktopActionResult> OpenRunFolderAsync(string? runId, CancellationToken cancellationToken)
    {
        string? root;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            var data = await reportData.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
            if (data is null) return Failure(RpcErrorCodes.RunNotFound, "The run output folder is unavailable.");
            root = data.OutputRoot;
        }
        else
        {
            var context = await runs.GetContextAsync(cancellationToken).ConfigureAwait(false);
            root = context.LockedOutputPath;
        }

        if (!TryNormalizeRoot(root, out var outputRoot))
        {
            return Failure(PathNotFound, "The output folder is unavailable.");
        }
        if (!Directory.Exists(outputRoot)) return Failure(PathNotFound, "The output folder is unavailable.");
        return await LaunchAsync(outputRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesktopActionResult> OpenManualReviewFolderAsync(string? runId, CancellationToken cancellationToken)
    {
        if (!IsSafePathSegment(runId)) return Failure(RpcErrorCodes.RunNotFound, "The review folder is unavailable.");
        var data = await reportData.LoadAsync(runId!, cancellationToken).ConfigureAwait(false);
        if (data is null || !TryNormalizeRoot(data.OutputRoot, out var outputRoot))
        {
            return Failure(RpcErrorCodes.RunNotFound, "The review folder is unavailable.");
        }

        var relativePath = Path.Combine("archive", runId!, "review");
        if (!TryResolveWithinRoot(outputRoot, relativePath, out var reviewPath))
        {
            return Failure(PathInvalid, "The review folder is unavailable.");
        }
        if (!Directory.Exists(reviewPath)) return Failure(PathNotFound, "The review folder is unavailable.");
        return await LaunchAsync(reviewPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesktopActionResult> OpenFileAsync(RunFileOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsSafePathSegment(request.RunId)) return Failure(RpcErrorCodes.RunNotFound, "The requested file is unavailable.");
        var isDocument = !string.IsNullOrWhiteSpace(request.DocumentId);
        var hasReportFields = !string.IsNullOrWhiteSpace(request.ReportPath) || !string.IsNullOrWhiteSpace(request.ContentHash);
        if (isDocument == hasReportFields)
        {
            return Failure(RpcErrorCodes.RpcInvalidParams, "The requested file is unavailable.");
        }

        var data = await reportData.LoadAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (data is null || !TryNormalizeRoot(data.OutputRoot, out var outputRoot))
        {
            return Failure(RpcErrorCodes.RunNotFound, "The requested file is unavailable.");
        }

        string relativePath;
        if (isDocument)
        {
            var matches = await artifacts.ListByRunAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            var artifact = matches
                .Where(item => item.State == ArchiveArtifactState.Committed
                    && string.Equals(item.Key.DocumentId, request.DocumentId, StringComparison.Ordinal))
                .OrderByDescending(item => string.Equals(item.Key.Role, "invoice", StringComparison.Ordinal))
                .ThenByDescending(item => item.Key.ProcessingRevision)
                .FirstOrDefault();
            if (artifact is null) return Failure(FileNotFound, "The requested file is unavailable.");
            relativePath = artifact.FinalRelativePath;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ReportPath) || string.IsNullOrWhiteSpace(request.ContentHash))
            {
                return Failure(RpcErrorCodes.RpcInvalidParams, "The requested file is unavailable.");
            }
            if (!TryResolveWithinRoot(outputRoot, request.ReportPath, out _))
            {
                return Failure(PathInvalid, "The requested file is unavailable.");
            }

            try
            {
                var token = await reports.OpenAsync(
                    new ReportOpenRequest(request.RunId, request.ReportPath, request.ContentHash),
                    cancellationToken).ConfigureAwait(false);
                var resolution = await reports.ResolveTokenAsync(token.TokenId, cancellationToken).ConfigureAwait(false);
                if (!resolution.Valid
                    || !string.Equals(resolution.RunId, request.RunId, StringComparison.Ordinal)
                    || !string.Equals(resolution.RelativePath, request.ReportPath, StringComparison.Ordinal)
                    || !string.Equals(resolution.ContentHash, request.ContentHash, StringComparison.Ordinal))
                {
                    return Failure(RpcErrorCodes.WebAssetInvalid, "The requested report is unavailable.");
                }
                relativePath = resolution.RelativePath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Failure(RpcErrorCodes.WebAssetInvalid, "The requested report is unavailable.");
            }
        }

        if (!TryResolveWithinRoot(outputRoot, relativePath, out var filePath))
        {
            return Failure(PathInvalid, "The requested file is unavailable.");
        }
        if (!File.Exists(filePath)) return Failure(FileNotFound, "The requested file is unavailable.");
        return await LaunchAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesktopActionResult> ExecuteWindowCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (command is not ("minimize" or "maximize" or "close"))
        {
            return Failure(RpcErrorCodes.RpcInvalidParams, "The window action is unavailable.");
        }

        try
        {
            return await windowCommands.ExecuteAsync(command, cancellationToken).ConfigureAwait(false)
                ? Success()
                : Failure(ActionFailed, "The window action could not be completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(ActionFailed, "The window action could not be completed.");
        }
    }

    private async Task<DesktopActionResult> LaunchAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await launcher.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            return Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(ActionFailed, "The requested location could not be opened.");
        }
    }

    private static bool TryNormalizeRoot(string? root, out string outputRoot)
    {
        outputRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        outputRoot = Path.GetFullPath(root);
        return true;
    }

    private static bool TryResolveWithinRoot(string root, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) return false;

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }
        absolutePath = candidate;
        return true;
    }

    private static bool IsSafePathSegment(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value is not ("." or "..")
            && !Path.IsPathRooted(value)
            && !value.Contains('/')
            && !value.Contains('\\');

    private static DesktopActionResult Success() => new(true, null, null);
    private static DesktopActionResult Failure(string code, string message) => new(false, code, message);
}

public sealed class ShellDesktopPathLauncher : IDesktopPathLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class AvaloniaUiDispatcher : IAvaloniaUiDispatcher
{
    public Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken)
    => Dispatcher.UIThread.InvokeAsync(async () => action()).WaitAsync(cancellationToken);
}

public sealed class AvaloniaWindowCommandDispatcher(
    IMainWindowAccessor windowAccessor,
    IAvaloniaUiDispatcher dispatcher) : IWindowCommandDispatcher
{
    public Task<bool> ExecuteAsync(string command, CancellationToken cancellationToken)
        => dispatcher.InvokeAsync(() =>
        {
            var window = windowAccessor.MainWindow;
            if (window is null) return false;
            switch (command)
            {
                case "minimize":
                    window.WindowState = WindowState.Minimized;
                    return true;
                case "maximize":
                    window.WindowState = window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    return true;
                case "close":
                    Dispatcher.UIThread.Post(window.Close, DispatcherPriority.Background);
                    return true;
                default:
                    return false;
            }
        }, cancellationToken);
}
