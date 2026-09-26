using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Domain.Runs;

/// <summary>Run-scoped failure surfaced through the DAG's control port.</summary>
public sealed record RunFailure(
    string RunId,
    string Stage,
    string ReasonCode,
    FailureCategory Category,
    bool Retryable,
    string SafeMessage,
    string ExceptionType = "",
    string Fingerprint = "");