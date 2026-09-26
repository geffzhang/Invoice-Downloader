namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Stable, safe-to-surface failure descriptor. Never carries raw exception
/// text, full URLs, mail bodies, OCR text, or secret values. The fingerprint
/// here is a deterministic short id derived from the reason code and
/// redaction-safe fields — used for log correlation only.
/// </summary>
public sealed record CandidateFailure(
    string ReasonCode,
    FailureScope Scope,
    FailureCategory Category,
    bool Retryable,
    string SafeMessage,
    int Attempt = 0,
    int MaxAttempts = 0,
    string ExceptionType = "",
    string Fingerprint = "");