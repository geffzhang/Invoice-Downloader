namespace InvoiceFlowAI.App.Navigation;

public interface IAppNavigator
{
    Task NavigateAsync(string key, CancellationToken cancellationToken = default);
}
