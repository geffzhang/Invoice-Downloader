using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Archive;

public interface ICwtCancellationFinalizer
{
    Task<CwtCancellationFinalizationResult> FinalizeAsync(
        string runId,
        string outputRoot,
        IReadOnlyList<CandidateProcessResult> candidates,
        CancellationToken cancellationToken);
}

public sealed record CwtCancellationFinalizationResult(
    IReadOnlyList<string> Matches,
    IReadOnlyDictionary<string, string> UpdatedRelativePaths,
    IReadOnlyList<CwtCancellationFinalizationFailure> Failures);

public sealed record CwtCancellationFinalizationFailure(string DocumentId, string ReasonCode, string SafeMessage);