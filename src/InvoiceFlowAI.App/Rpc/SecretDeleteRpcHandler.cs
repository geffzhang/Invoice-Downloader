using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class SecretDeleteRpcHandler(ISecretStore store)
    : TypedRpcHandler<SecretDeleteRequest, SecretMutationResult>("secret.delete")
{
    protected override async Task<SecretMutationResult> ExecuteAsync(
        SecretDeleteRequest parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.Name is not ("mail.imap.auth-code" or "deepseek.api-key"))
        {
            throw new ArgumentException("Secret name is not allowed.", nameof(parameters));
        }

        await store.DeleteAsync(parameters.Name, cancellationToken).ConfigureAwait(false);
        return new SecretMutationResult(parameters.Name, Configured: false, Persistent: false);
    }
}