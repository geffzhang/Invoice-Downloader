using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class SecretSetRpcHandler(ISecretRetentionStore store)
    : TypedRpcHandler<SecretSetRequest, SecretMutationResult>("secret.set")
{
    protected override Task<SecretMutationResult> ExecuteAsync(SecretSetRequest parameters, CancellationToken cancellationToken)
        => store.SetAsync(parameters, cancellationToken);
}