namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountTestResult(
    string AccountId,
    bool Succeeded,
    string Mailbox,
    string SafeMessage,
    string? FailureCode = null);