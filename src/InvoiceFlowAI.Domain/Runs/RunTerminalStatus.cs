namespace InvoiceFlowAI.Domain.Runs;

/// <summary>Terminal run status, decided exactly once at the final barrier.</summary>
public enum RunTerminalStatus
{
    Completed = 0,
    PartialSuccess = 1,
    NeedsManualReview = 2,
    Cancelled = 3,
    Failed = 4,
}