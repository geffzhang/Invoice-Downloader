namespace InvoiceFlowAI.App.Events;

public interface IAppEventBus
{
    void Publish(AppRunEvent runEvent);
    IDisposable Subscribe(Action<AppRunEvent> handler);
}
