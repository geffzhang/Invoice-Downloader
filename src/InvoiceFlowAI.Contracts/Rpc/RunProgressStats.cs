namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunProgressStats(
    int Emails,
    int Invoices,
    int Errors);