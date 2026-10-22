namespace InvoiceFlowAI.App.Services;

public interface IDisclaimerService
{
    Task ShowAsync(CancellationToken cancellationToken = default);
}
