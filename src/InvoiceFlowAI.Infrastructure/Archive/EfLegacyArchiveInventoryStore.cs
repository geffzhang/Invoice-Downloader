using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class EfLegacyArchiveInventoryStore : ILegacyArchiveInventoryStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfLegacyArchiveInventoryStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<LegacyArchiveInventorySnapshot> DiscoverAsync(
        string rootKey,
        string originalRelativePath,
        string sourceFileName,
        string contentHash,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        RequireEfTransaction(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        if (rootKey.Length > 64) throw new ArgumentException("Inventory root key is too long.", nameof(rootKey));
        var normalizedPath = NormalizeRelativePath(originalRelativePath);
        ValidateFileName(sourceFileName);
        var normalizedHash = NormalizeHash(contentHash);
        var row = await _context.LegacyArchiveInventory.FirstOrDefaultAsync(
            item => item.RootKey == rootKey
                && item.OriginalRelativePath == normalizedPath
                && item.ContentHash == normalizedHash,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            var now = DateTimeOffset.UtcNow;
            row = new LegacyArchiveInventoryRow
            {
                InventoryId = CreateInventoryId(rootKey, normalizedPath, normalizedHash),
                RootKey = rootKey,
                OriginalRelativePath = normalizedPath,
                CurrentRelativePath = normalizedPath,
                SourceFileName = sourceFileName,
                ContentHash = normalizedHash,
                State = LegacyArchiveInventoryState.Discovered.ToString(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _context.LegacyArchiveInventory.Add(row);
        }

        return ToSnapshot(row);
    }

    public async Task<IReadOnlyList<LegacyArchiveInventorySnapshot>> ListByRootAsync(
        string rootKey,
        CancellationToken cancellationToken)
    {
        var rows = await _context.LegacyArchiveInventory.AsNoTracking()
            .Where(item => item.RootKey == rootKey)
            .OrderBy(item => item.OriginalRelativePath)
            .ThenBy(item => item.ContentHash)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToSnapshot).ToArray();
    }

    public async Task UpdateLocationAsync(
        string inventoryId,
        string currentRelativePath,
        LegacyArchiveInventoryState state,
        string? reviewRunId,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        RequireEfTransaction(transaction);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        var row = await _context.LegacyArchiveInventory.FirstOrDefaultAsync(
            item => item.InventoryId == inventoryId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Legacy archive inventory item was not found.");
        row.CurrentRelativePath = NormalizeRelativePath(currentRelativePath);
        row.State = state.ToString();
        row.ReviewRunId = reviewRunId;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string CreateInventoryId(string rootKey, string relativePath, string contentHash)
    {
        var identity = Encoding.UTF8.GetBytes($"{rootKey}\0{relativePath}\0{contentHash}");
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(value))
        {
            throw new ArgumentException("Inventory path must be relative.", nameof(value));
        }
        var segments = normalized.Split('/');
        if (normalized.Length > 512 || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("Inventory path must be normalized and remain within its root.", nameof(value));
        }
        return string.Join('/', segments);
    }

    private static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || Path.GetFileName(value) != value || value is "." or "..")
        {
            throw new ArgumentException("Inventory source name must be a file name.", nameof(value));
        }
    }

    private static string NormalizeHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Inventory content hash must be a SHA-256 hex value.", nameof(value));
        }
        return value.ToLowerInvariant();
    }

    private static void RequireEfTransaction(IUnitOfWork transaction)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("Legacy archive inventory writes must use EfUnitOfWork.");
        }
    }

    private static LegacyArchiveInventorySnapshot ToSnapshot(LegacyArchiveInventoryRow row)
        => new(
            row.InventoryId,
            row.RootKey,
            row.OriginalRelativePath,
            row.CurrentRelativePath,
            row.SourceFileName,
            row.ContentHash,
            Enum.Parse<LegacyArchiveInventoryState>(row.State, ignoreCase: false),
            row.ReviewRunId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);
}