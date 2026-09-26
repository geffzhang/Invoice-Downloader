using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Persistence;

public interface ICandidateSourceWriter
{
    Task UpsertSelectedArtifactAsync(
        DocumentCandidate candidate,
        string contentSha256,
        DocumentIdentity sourceGroupIdentity,
        CancellationToken cancellationToken);
}