namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountListResult(
    IReadOnlyList<MailboxAccountSnapshot> Items);