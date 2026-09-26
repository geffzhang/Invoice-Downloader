namespace InvoiceFlowAI.Application.Archive;

public interface ICwtArchiveInventory
{
    Task<IReadOnlyList<CwtArchiveInventoryItem>> ListAsync(
        string outputRoot,
        CancellationToken cancellationToken);
}