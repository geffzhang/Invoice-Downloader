using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Application.Archive;

public interface ILegacyArchiveInventoryStore
{
    Task<LegacyArchiveInventorySnapshot> DiscoverAsync(
        string rootKey,
        string originalRelativePath,
        string sourceFileName,
        string contentHash,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LegacyArchiveInventorySnapshot>> ListByRootAsync(
        string rootKey,
        CancellationToken cancellationToken);

    Task UpdateLocationAsync(
        string inventoryId,
        string currentRelativePath,
        LegacyArchiveInventoryState state,
        string? reviewRunId,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}