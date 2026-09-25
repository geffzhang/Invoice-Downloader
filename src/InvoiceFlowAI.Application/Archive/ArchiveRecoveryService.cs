// Recovery service that runs at start-up to reconcile any ArchivedArtifacts
// rows that are still in State=Prepared after a crash. It uses the existing
// IArchiveFileSystem abstraction to inspect the on-disk state of the temp
// and final files; the decision it writes is intentionally explicit
// (RecoveryRequired) whenever the evidence is unclear so the operator is
// not silently lossy.

using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Application.Archive;

public sealed class ArchiveRecoveryService : IArchiveRecoveryService
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IArchiveArtifactStore _store;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IAuditEventStore _auditStore;
    private readonly IPairingStore? _pairingStore;
    private readonly ILegacyArchiveInventoryStore? _legacyInventoryStore;

    public ArchiveRecoveryService(
        IUnitOfWorkFactory uowFactory,
        IArchiveArtifactStore store,
        IArchiveFileSystem fileSystem,
        IAuditEventStore auditStore,
        IPairingStore? pairingStore = null,
        ILegacyArchiveInventoryStore? legacyInventoryStore = null)
    {
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _pairingStore = pairingStore;
        _legacyInventoryStore = legacyInventoryStore;
    }

    public async Task<IReadOnlyList<ArchiveRecoveryEntry>> ScanAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = await _store.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return rows
            .Where(r => r.State == ArchiveArtifactState.Prepared
                     || r.State == ArchiveArtifactState.RecoveryRequired)
            .Select(r => new ArchiveRecoveryEntry(
                r.ArtifactId,
                r.Key,
                r.TempFilePath,
                r.FinalRelativePath,
                r.FileName,
                r.ExpectedContentHash,
                r.State,
                r.FinalFilePath))
            .ToList();
    }

    public async Task<ArchiveRecoveryDecision> ResolveAsync(ArchiveRecoveryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var finalFilePath = entry.FinalFilePath ?? entry.FinalRelativePath;
        var tempExists = await _fileSystem.FileExistsAsync(entry.TempFilePath, cancellationToken).ConfigureAwait(false);
        var finalExists = await _fileSystem.FileExistsAsync(finalFilePath, cancellationToken).ConfigureAwait(false);

        // Case: both gone. Evidence missing — surface RecoveryRequired.
        if (!tempExists && !finalExists)
        {
            return await MarkRecoveryAsync(entry, "ARCHIVE_BOTH_FILES_MISSING",
                "Temp and final files are both missing — cannot reconcile.", cancellationToken).ConfigureAwait(false);
        }

        // Case: final present. Verify hash.
        if (finalExists)
        {
            var finalHash = await _fileSystem.ComputeSha256Async(finalFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(finalHash, entry.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await MarkRecoveryAsync(entry, "ARCHIVE_HASH_MISMATCH",
                    "Final file present but its content hash does not match the DB-asserted hash.", cancellationToken).ConfigureAwait(false);
            }

            // Final hash matches → safe to commit. Use a fresh transaction.
            await using var tx = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await _store.MarkCommittedAsync(entry.ArtifactId, DateTimeOffset.UtcNow, tx, cancellationToken).ConfigureAwait(false);
            await _auditStore.AppendAsync(new AuditEventRecord(
                AuditEventId: $"audit-{entry.Key.RunId}-archive.recover.{entry.ArtifactId}",
                RunId: entry.Key.RunId,
                EventSequence: 0,
                EventType: "archive.recover",
                Stage: "archive",
                NodeId: "archive",
                DocumentId: entry.Key.DocumentId,
                ProcessingRevision: entry.Key.ProcessingRevision,
                ReasonCode: "ARCHIVE_RECOVERED",
                PayloadJson: $"{{\"artifactId\":\"{entry.ArtifactId}\"}}",
                PayloadHash: Sha256Hex($"{{\"artifactId\":\"{entry.ArtifactId}\"}}"),
                OccurredAtUtc: DateTimeOffset.UtcNow), tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            await ReconcilePairingsAsync(entry.Key.RunId, cancellationToken).ConfigureAwait(false);
            return new ArchiveRecoveryDecision(entry.ArtifactId, ArchiveArtifactState.Committed, null, null);
        }

        // Case: only temp present. Re-run the move and the final-hash check.
        if (tempExists)
        {
            var tempHash = await _fileSystem.ComputeSha256Async(entry.TempFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(tempHash, entry.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await MarkRecoveryAsync(entry, "ARCHIVE_TEMP_HASH_MISMATCH",
                    "Temp file present but its content hash does not match the DB-asserted hash.", cancellationToken).ConfigureAwait(false);
            }

            await _fileSystem.AtomicMoveAsync(entry.TempFilePath, finalFilePath, cancellationToken).ConfigureAwait(false);
            await _fileSystem.FlushToDiskAsync(finalFilePath, cancellationToken).ConfigureAwait(false);
            var finalHash = await _fileSystem.ComputeSha256Async(finalFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(finalHash, entry.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await MarkRecoveryAsync(entry, "ARCHIVE_HASH_MISMATCH",
                    "Final file after re-move has unexpected hash.", cancellationToken).ConfigureAwait(false);
            }

            await using var tx = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await _store.MarkCommittedAsync(entry.ArtifactId, DateTimeOffset.UtcNow, tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            await ReconcilePairingsAsync(entry.Key.RunId, cancellationToken).ConfigureAwait(false);
            return new ArchiveRecoveryDecision(entry.ArtifactId, ArchiveArtifactState.Committed, null, null);
        }

        return await MarkRecoveryAsync(entry, "ARCHIVE_RECOVERY_UNKNOWN",
            "Unknown recovery state — leaving row as RecoveryRequired.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LegacyArchiveRecoveryDecision>> ReconcileLegacyAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var store = _legacyInventoryStore
            ?? throw new InvalidOperationException("Legacy archive inventory recovery is not configured.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var rootKey = ArchiveInventoryPath.CreateRootKey(fullRoot);
        var entries = await store.ListByRootAsync(rootKey, cancellationToken).ConfigureAwait(false);
        var decisions = new List<LegacyArchiveRecoveryDecision>();

        foreach (var entry in entries.Where(item => item.State == LegacyArchiveInventoryState.RecoveryRequired))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasCurrent = ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, entry.CurrentRelativePath, out var currentPath)
                && await _fileSystem.FileExistsAsync(currentPath, cancellationToken).ConfigureAwait(false);
            var hasOriginal = ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, entry.OriginalRelativePath, out var originalPath)
                && await _fileSystem.FileExistsAsync(originalPath, cancellationToken).ConfigureAwait(false);

            if (hasCurrent && hasOriginal && !PathsEqual(currentPath, originalPath))
            {
                decisions.Add(new LegacyArchiveRecoveryDecision(
                    entry.InventoryId, LegacyArchiveInventoryState.RecoveryRequired, "LEGACY_ARCHIVE_LOCATION_AMBIGUOUS"));
                continue;
            }

            var evidencePath = hasCurrent ? currentPath : hasOriginal ? originalPath : null;
            if (evidencePath is null)
            {
                decisions.Add(new LegacyArchiveRecoveryDecision(
                    entry.InventoryId, LegacyArchiveInventoryState.RecoveryRequired, "LEGACY_ARCHIVE_FILE_MISSING"));
                continue;
            }

            var actualHash = await _fileSystem.ComputeSha256Async(evidencePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(new LegacyArchiveRecoveryDecision(
                    entry.InventoryId, LegacyArchiveInventoryState.RecoveryRequired, "LEGACY_ARCHIVE_HASH_MISMATCH"));
                continue;
            }

            var resolvedState = hasCurrent && !PathsEqual(currentPath, originalPath)
                ? LegacyArchiveInventoryState.Review
                : LegacyArchiveInventoryState.Discovered;
            var relativePath = resolvedState == LegacyArchiveInventoryState.Review
                ? entry.CurrentRelativePath
                : entry.OriginalRelativePath;
            await using var transaction = await _uowFactory
                .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await store.UpdateLocationAsync(
                entry.InventoryId,
                relativePath,
                resolvedState,
                resolvedState == LegacyArchiveInventoryState.Review ? entry.ReviewRunId : null,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            decisions.Add(new LegacyArchiveRecoveryDecision(entry.InventoryId, resolvedState, null));
        }

        return decisions;
    }

    public async Task<ArchiveStartupRecoveryResult> ReconcileBeforeRunAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var recoverable = await _store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var artifactDecisions = new List<ArchiveRecoveryDecision>();
        foreach (var artifact in recoverable.OrderBy(item => item.Key.RunId, StringComparer.Ordinal)
                     .ThenBy(item => item.ArtifactId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, artifact.FinalFilePath ?? artifact.FinalRelativePath, out var finalPath)
                || !ArchiveInventoryPath.TryResolveUnderRoot(fullRoot, artifact.TempFilePath, out var tempPath))
            {
                continue;
            }

            artifactDecisions.Add(await ResolveAsync(new ArchiveRecoveryEntry(
                artifact.ArtifactId,
                artifact.Key,
                tempPath,
                artifact.FinalRelativePath,
                artifact.FileName,
                artifact.ExpectedContentHash,
                artifact.State,
                finalPath), cancellationToken).ConfigureAwait(false));
        }

        var legacyDecisions = await ReconcileLegacyAsync(fullRoot, cancellationToken).ConfigureAwait(false);
        return new ArchiveStartupRecoveryResult(artifactDecisions, legacyDecisions);
    }

    private async Task<ArchiveRecoveryDecision> MarkRecoveryAsync(
        ArchiveRecoveryEntry entry,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        await using var tx = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
        await _store.MarkRecoveryRequiredAsync(entry.ArtifactId, reasonCode, tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ReconcilePairingsAsync(entry.Key.RunId, cancellationToken).ConfigureAwait(false);
        return new ArchiveRecoveryDecision(entry.ArtifactId, ArchiveArtifactState.RecoveryRequired, reasonCode, safeMessage);
    }

    private async Task ReconcilePairingsAsync(string runId, CancellationToken cancellationToken)
    {
        if (_pairingStore is null) return;

        var artifacts = await _store.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
        await _pairingStore.ReconcileArchiveStateAsync(runId, artifacts, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}