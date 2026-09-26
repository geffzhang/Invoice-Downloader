namespace InvoiceFlowAI.Contracts.Accounts;

public sealed record AccountDeleteRequest(
    string AccountId,
    int ExpectedRevision);