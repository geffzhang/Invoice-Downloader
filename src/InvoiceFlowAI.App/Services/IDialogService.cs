namespace InvoiceFlowAI.App.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default);
}
