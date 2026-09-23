namespace InvoiceFlowAI.Contracts.Accounts;

/// <summary>
/// Non-secret account descriptor sent by the page on <c>account.save</c>.
/// <c>CredentialName</c> is a *reference* (e.g. <c>mail.imap.auth-code</c>) —
/// raw auth codes never enter this DTO and must come through
/// <c>secret.set</c> so they live only in DPAPI.
/// </summary>
public sealed record MailboxAccountDraft(
    string AccountId,
    string EmailAddress,
    string ImapHost,
    int ImapPort,
    bool UseTls,
    string CredentialName,
    string DisplayName,
    string? DefaultMailbox = null);