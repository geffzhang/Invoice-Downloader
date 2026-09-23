namespace InvoiceFlowAI.Contracts.EmailBody;

public sealed record EmailBodyReceiptRequest(
    string Subject,
    string Sender,
    string EmailDate);