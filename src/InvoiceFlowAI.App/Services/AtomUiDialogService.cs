using Avalonia.Media;
using AtomUI.Desktop.Controls;
using InvoiceFlowAI.App.Settings;

namespace InvoiceFlowAI.App.Services;

public sealed class AtomUiDialogService(IMainWindowAccessor windowAccessor) : IDialogService
{
    public async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default)
    {
        var content = new Avalonia.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        var options = new MessageBoxOptions
        {
            Title = title,
            OkButtonText = confirmText,
            CancelButtonText = cancelText,
            IsDragMovable = true,
            IsMaskClosable = false,
        };

        var result = await MessageBox.ShowMessageBoxModalAsync(
            content,
            options: options,
            topLevel: windowAccessor.MainWindow,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        return result is DialogCode.Accepted;
    }
}
