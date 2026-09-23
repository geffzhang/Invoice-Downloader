namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Scope that owns a failure. Candidate failures stay attached to a single
/// <see cref="CandidateProcessResult"/>; node / run failures belong to the
/// ZeroPipeline DAG and are surfaced separately.
/// </summary>
public enum FailureScope
{
    Candidate = 0,
    Node = 1,
    Run = 2,
}