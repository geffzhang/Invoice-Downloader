namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountTestRequest(
    string AccountId,
    string? Mailbox = null);