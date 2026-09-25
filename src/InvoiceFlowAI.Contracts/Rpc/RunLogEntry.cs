namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Message);