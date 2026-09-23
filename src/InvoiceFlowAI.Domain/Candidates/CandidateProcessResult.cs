using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Single terminal result for a <see cref="DocumentCandidate"/>. Each candidate
/// in a run must produce exactly one of these — duplicates are coalesced by
/// (DocumentId, ProcessingRevision) before they reach the final result port.
/// </summary>
public sealed record CandidateProcessResult(
    DocumentCandidate Candidate,
    CandidateStatus Status,
    InvoiceDocument? Invoice = null,
    string ArtifactPath = "",
    CandidateFailure? Failure = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<string, string>? Trace = null);