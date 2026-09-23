// Application-level transaction abstraction per design §5 / §8. Business
// services receive an IUnitOfWork created by the DI container; the same
// UoW is shared across all repository writes so a single commit / rollback
// covers business state + audit + event replay atomically.

namespace InvoiceFlowAI.Application.Persistence;

/// <summary>
/// Why a particular unit-of-work was opened. Persisted for diagnostics
/// so the EF transaction trail can be matched to the application intent
/// in crash reports.
/// </summary>
public enum TransactionPurpose
{
    RunCreate,
    RunTransition,
    CandidateCommit,
    CheckpointCommit,
    ArchivePrepare,
    ArchiveCommit,
    ReviewSubmit,
    EventAppend,
    Migration,
    RulesetBootstrap,
    MailboxUpsert,
    SettingsUpdate,
}

/// <summary>
/// Single explicit transaction. Created by <see cref="IUnitOfWorkFactory"/>
/// and shared by every repository write inside a top-level application
/// coordinator. UoWs are not nested, not thread-safe, and not reusable
/// after commit/rollback.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    string TransactionId { get; }
    TransactionPurpose Purpose { get; }
    bool IsCompleted { get; }

    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken);
}