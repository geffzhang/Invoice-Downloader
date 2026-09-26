namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountMutationResult(
    MailboxAccountSnapshot Account,
    string ConfigurationFingerprint);