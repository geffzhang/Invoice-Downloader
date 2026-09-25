using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class CwtArchiveInventory : ICwtArchiveInventory
{
    private readonly IArchiveArtifactStore _artifactStore;
    private readonly ILegacyArchiveInventoryStore _legacyStore;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;

    public CwtArchiveInventory(
        IArchiveArtifactStore artifactStore,
        ILegacyArchiveInventoryStore legacyStore,
        IArchiveFileSystem fileSystem,
        IUnitOfWorkFactory unitOfWorkFactory)
    {
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _legacyStore = legacyStore ?? throw new ArgumentNullException(nameof(legacyStore));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    }

    public async Task<IReadOnlyList<CwtArchiveInventoryItem>> ListAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var artifactRows = await _artifactStore.ListCommittedForInventoryAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<CwtArchiveInventoryItem>();
        var artifactPaths = new HashSet<string>(PathComparer);

        foreach (var artifact in artifactRows.OrderBy(item => item.ArtifactId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, artifact.FinalFilePath ?? artifact.FinalRelativePath, out var path)) continue;
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, path));
            if (IsReviewPath(relativePath)) continue;
            var sourceFileName = artifact.SourceFileName;
            if (string.IsNullOrWhiteSpace(sourceFileName)
                && relativePath.StartsWith("住宿发票/", StringComparison.Ordinal))
            {
                sourceFileName = Path.GetFileName(path);
            }
            if (!IsAccommodationArtifact(artifact, relativePath) || string.IsNullOrWhiteSpace(sourceFileName)) continue;
            if (!await _fileSystem.FileExistsAsync(path, cancellationToken).ConfigureAwait(false)) continue;
            if (!artifactPaths.Add(path)) continue;

            items.Add(new CwtArchiveInventoryItem(
                artifact.ArtifactId,
                artifact.ArtifactId,
                artifact.Key.DocumentId,
                artifact.Key.ProcessingRevision,
                sourceFileName,
                relativePath,
                path,
                artifact.ExpectedContentHash,
                CwtArchiveInventoryItemKind.ArchivedArtifact,
                artifact.State,
                null,
                artifact.Key.RunId));
        }

        var rootKey = ArchiveInventoryPath.CreateRootKey(fullRoot);
        var accommodationDirectory = Path.Combine(fullRoot, "住宿发票");
        var directFileHashes = new Dictionary<string, string>(PathComparer);
        var directFiles = await _fileSystem
            .EnumerateDirectChildFilesAsync(accommodationDirectory, cancellationToken).ConfigureAwait(false);
        if (directFiles.Count > 0)
        {
            await using var transaction = await _unitOfWorkFactory
                .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            foreach (var filePath in directFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, filePath, out var path) || artifactPaths.Contains(path)) continue;
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, path));
                var sourceFileName = Path.GetFileName(path);
                var hash = await _fileSystem.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                directFileHashes[path] = hash;
                await _legacyStore.DiscoverAsync(
                    rootKey, relativePath, sourceFileName, hash, transaction, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var legacyRows = await _legacyStore.ListByRootAsync(rootKey, cancellationToken).ConfigureAwait(false);
        foreach (var legacy in legacyRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (legacy.State == LegacyArchiveInventoryState.Review) continue;
            if (!ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, legacy.CurrentRelativePath, out var path) || artifactPaths.Contains(path)) continue;
            if (directFileHashes.TryGetValue(path, out var currentHash)
                && !string.Equals(currentHash, legacy.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
            if (!await _fileSystem.FileExistsAsync(path, cancellationToken).ConfigureAwait(false)) continue;

            items.Add(new CwtArchiveInventoryItem(
                legacy.InventoryId,
                null,
                null,
                null,
                legacy.SourceFileName,
                legacy.CurrentRelativePath,
                path,
                legacy.ContentHash,
                CwtArchiveInventoryItemKind.LegacyFile,
                null,
                legacy.State));
        }

        return items
            .GroupBy(item => item.AbsolutePath, PathComparer)
            .Select(group => group.OrderBy(item => item.Kind).ThenBy(item => item.InventoryId, StringComparer.Ordinal).First())
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.InventoryId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsAccommodationArtifact(ArchiveArtifactSnapshot artifact, string relativePath)
    {
        if (relativePath.StartsWith("住宿发票/", StringComparison.Ordinal)) return true;
        if (artifact.Key.Role is "hotel_invoice" or "hotel_folio") return true;
        return artifact.DocumentType is nameof(InvoiceDocumentType.HotelInvoice)
            or nameof(InvoiceDocumentType.HotelFolio)
            or nameof(InvoiceDocumentType.AccommodationInvoice)
            or nameof(InvoiceDocumentType.AccommodationStatement)
            or nameof(InvoiceDocumentType.AccommodationConfirmation)
            || artifact.SourceFileName?.Contains("酒店", StringComparison.Ordinal) == true
            || artifact.SourceFileName?.Contains("住宿", StringComparison.Ordinal) == true;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static bool IsReviewPath(string relativePath)
        => relativePath.Split('/').Any(segment => string.Equals(segment, "review", StringComparison.OrdinalIgnoreCase));

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

}