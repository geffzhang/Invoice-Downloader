namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record DirectoryChooseResult(
    bool Cancelled,
    string? Path);