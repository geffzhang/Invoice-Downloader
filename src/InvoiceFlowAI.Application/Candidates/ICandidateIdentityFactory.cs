using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Candidates;

public interface ICandidateIdentityFactory
{
    Task<DocumentIdentity> CreateAttachmentAsync(
        string accountId,
        string mailbox,
        string uidValidity,
        MailboxAttachmentCandidate attachment,
        CancellationToken cancellationToken);

    Task<DocumentIdentity> CreateUrlAsync(
        MailboxUrlCandidate candidate,
        CancellationToken cancellationToken);

    Task<DocumentIdentity> CreateUrlGroupAsync(
        string providerFamily,
        IReadOnlyList<MailboxUrlCandidate> candidates,
        CancellationToken cancellationToken);

    Task<DocumentIdentity> CreateRecoveredArtifactAsync(
        DocumentIdentity sourceGroupIdentity,
        RecoveredArtifactKind kind,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}