namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunContextSnapshot(
    bool ExplicitRunContext,
    bool ControlledRun,
    bool AutostartEnabled,
    int AutostartDelayMs,
    string? RunId,
    string? LockedOutputPath,
    DateOnly? LockedDateFrom,
    DateOnly? LockedDateTo,
    string? LockedEmail,
    string? AccountId);