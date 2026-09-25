namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record DesktopActionResult(
    bool Succeeded,
    string? ErrorCode,
    string? Message);