using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Security;

public interface ISecretRetentionStore
{
    Task<SecretMutationResult> SetAsync(SecretSetRequest request, CancellationToken cancellationToken);
}