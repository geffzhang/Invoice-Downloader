namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Terminal status of a single document candidate after extraction has ended.
/// Each value is the *only* result that may be written for the candidate's
/// (DocumentId + processing revision) idempotency key, per design §5.
/// </summary>
public enum CandidateStatus
{
    Resolved = 0,
    Duplicate = 1,
    Retained = 2,
    ManualReview = 3,
    Unresolved = 4,
    Cancelled = 5,
    QuotaExhausted = 6,
    AuthFailed = 7,
    Timeout = 8,
}