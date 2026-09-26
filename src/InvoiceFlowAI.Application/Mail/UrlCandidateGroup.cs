using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Mail;

public sealed record UrlCandidateGroup(
    string ProviderFamily,
    IReadOnlyList<MailboxUrlCandidate> Candidates,
    IReadOnlyDictionary<string, string> ExpectedFields,
    IReadOnlyDictionary<string, IReadOnlyList<ExpectedFieldEvidence>> ExpectedFieldEvidence,
    DocumentIdentity GroupIdentity);