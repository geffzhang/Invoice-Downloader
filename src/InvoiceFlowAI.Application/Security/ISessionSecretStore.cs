namespace InvoiceFlowAI.Application.Security;

public interface ISessionSecretStore
{
    Task SaveAsync(string name, string value, CancellationToken cancellationToken);

    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}