namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountSaveRequest(
    MailboxAccountDraft Account,
    int ExpectedRevision);