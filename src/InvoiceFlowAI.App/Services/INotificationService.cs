namespace InvoiceFlowAI.App.Services;

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error,
}

public interface INotificationService
{
    void Show(string message, NotificationKind kind);
}
