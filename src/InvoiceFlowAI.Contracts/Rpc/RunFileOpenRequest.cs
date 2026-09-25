namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunFileOpenRequest(
    string RunId,
    string? DocumentId = null,
    string? ReportPath = null,
    string? ContentHash = null);
