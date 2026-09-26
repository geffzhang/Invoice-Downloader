// Application-level abstraction for run lifecycle state — the
// RunCoordinator needs to read and update the active run row, including the
// LastEventSequence cursor that event-replay depends on. The application
// layer never sees the EF row types.

using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Persistence;

public interface IRunLifecycleStore
{
    Task<IReadOnlyList<string>> ListOutputRootsAsync(CancellationToken cancellationToken);

    Task<RunStateSnapshot?> FindAsync(string runId, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(
        RunCreationRequest request,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task<bool> TryMarkRunningAsync(
        string runId,
        string stage,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task UpdateTerminalStateAsync(
        RunStateSnapshot snapshot,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task UpdateLastEventSequenceAsync(
        string runId,
        long lastEventSequence,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task<bool> TryRequestCancellationAsync(
        string runId,
        DateTimeOffset requestedAtUtc,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken);
}

public sealed record RunCreationRequest(
    string RunId,
    DateOnly DateFrom,
    DateOnly DateToExclusive,
    string? AccountId,
    int? AccountRevision,
    string? Mailbox,
    string? OutputRoot,
    int? SettingsRevision,
    string? RuleSetId,
    int? RuleSetVersion,
    string? ConfigurationFingerprint,
    string? RecipeVersion,
    DateTimeOffset StartedAtUtc);

public enum RunLifecycleState
{
    Created = 0,
    Running = 1,
    Recovering = 2,
    Completed = 3,
    PartialSuccess = 4,
    NeedsManualReview = 5,
    Cancelled = 6,
    Failed = 7,
}

public sealed record RunStateSnapshot(
    string RunId,
    RunLifecycleState State,
    string Stage,
    string? TerminalReasonCode,
    long LastEventSequence,
    DateTimeOffset? EndedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    RunSummary? Summary = null);