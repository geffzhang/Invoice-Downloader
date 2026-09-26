using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Desktop;

public interface IDesktopActionService
{
    Task<DesktopActionResult> OpenRunFolderAsync(string? runId, CancellationToken cancellationToken);
    Task<DesktopActionResult> OpenManualReviewFolderAsync(string? runId, CancellationToken cancellationToken);
    Task<DesktopActionResult> OpenFileAsync(RunFileOpenRequest request, CancellationToken cancellationToken);
    Task<DesktopActionResult> ExecuteWindowCommandAsync(string command, CancellationToken cancellationToken);
}

public interface IDesktopPathLauncher
{
    Task OpenAsync(string path, CancellationToken cancellationToken);
}

public interface IWindowCommandDispatcher
{
    Task<bool> ExecuteAsync(string command, CancellationToken cancellationToken);
}

public interface IAvaloniaUiDispatcher
{
    Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken);
}
