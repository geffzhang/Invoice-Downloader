namespace InvoiceFlowAI.Application.Mail;

public sealed record MailboxUrlCandidate(
    string AccountId,
    string Mailbox,
    string UidValidity,
    string MessageUid,
    Uri SourceUrl,
    string ProviderFamily,
    string ProviderGroupId,
    IReadOnlyDictionary<string, string> ExpectedFields,
    long Sequence)
{
    public IReadOnlyDictionary<string, IReadOnlyList<ExpectedFieldEvidence>> ExpectedFieldEvidence { get; init; }
        = new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(StringComparer.Ordinal);
}