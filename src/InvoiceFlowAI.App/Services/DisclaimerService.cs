using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.Views;

namespace InvoiceFlowAI.App.Services;

public sealed class DisclaimerService(IMainWindowAccessor windowAccessor) : IDisclaimerService
{
    public Task ShowAsync(CancellationToken cancellationToken = default)
    {
        var dialog = new DisclaimerWindow();
        var owner = windowAccessor.MainWindow;
        if (owner is null)
        {
            dialog.Show();
            return Task.CompletedTask;
        }

        _ = dialog.ShowDialog(owner);
        return Task.CompletedTask;
    }
}
