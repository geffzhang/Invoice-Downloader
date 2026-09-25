namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record SecretMutationResult(
    string Name,
    bool Configured,
    bool Persistent);