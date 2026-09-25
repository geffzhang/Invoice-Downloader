namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record DesktopFileActionRequest(
    string? RunId,
    string Path);