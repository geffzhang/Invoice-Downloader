using Avalonia.Threading;
using AtomUI.Desktop.Controls;
using InvoiceFlowAI.App.Settings;

namespace InvoiceFlowAI.App.Services;

public sealed class AtomUiNotificationService(IMainWindowAccessor windowAccessor) : INotificationService
{
    private WindowMessageManager? _manager;

    public void Show(string message, NotificationKind kind)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, kind));
            return;
        }

        _manager ??= new WindowMessageManager(windowAccessor.MainWindow);
        _manager.Show(new Message(message, ToMessageType(kind)));
    }

    private static MessageType ToMessageType(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => MessageType.Success,
        NotificationKind.Warning => MessageType.Warning,
        NotificationKind.Error => MessageType.Error,
        _ => MessageType.Information,
    };
}
