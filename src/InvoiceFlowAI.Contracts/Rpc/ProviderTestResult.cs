namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record ProviderTestResult(
    string ProviderId,
    bool Succeeded,
    string? FailureCode = null,
    string SafeMessage = "");