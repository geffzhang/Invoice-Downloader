namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record ProviderTestRequest(
    string ProviderId,
    string CredentialName);