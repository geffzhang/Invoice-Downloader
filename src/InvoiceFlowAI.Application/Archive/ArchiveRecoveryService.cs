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

    public ArchiveRecoveryService(
        IUnitOfWorkFactory uowFactory,
        IArchiveArtifactStore store,
        IArchiveFileSystem fileSystem,
        IAuditEventStore auditStore)
    {
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
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
                r.State))
            .ToList();
    }

    public async Task<ArchiveRecoveryDecision> ResolveAsync(ArchiveRecoveryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var tempExists = await _fileSystem.FileExistsAsync(entry.TempFilePath, cancellationToken).ConfigureAwait(false);
        var finalExists = await _fileSystem.FileExistsAsync(entry.FinalRelativePath, cancellationToken).ConfigureAwait(false);

        // Case: both gone. Evidence missing — surface RecoveryRequired.
        if (!tempExists && !finalExists)
        {
            return await MarkRecoveryAsync(entry, "ARCHIVE_BOTH_FILES_MISSING",
                "Temp and final files are both missing — cannot reconcile.", cancellationToken).ConfigureAwait(false);
        }

        // Case: final present. Verify hash.
        if (finalExists)
        {
            var finalHash = await _fileSystem.ComputeSha256Async(entry.FinalRelativePath, cancellationToken).ConfigureAwait(false);
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

            await _fileSystem.AtomicMoveAsync(entry.TempFilePath, entry.FinalRelativePath, cancellationToken).ConfigureAwait(false);
            await _fileSystem.FlushToDiskAsync(entry.FinalRelativePath, cancellationToken).ConfigureAwait(false);
            var finalHash = await _fileSystem.ComputeSha256Async(entry.FinalRelativePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(finalHash, entry.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await MarkRecoveryAsync(entry, "ARCHIVE_HASH_MISMATCH",
                    "Final file after re-move has unexpected hash.", cancellationToken).ConfigureAwait(false);
            }

            await using var tx = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await _store.MarkCommittedAsync(entry.ArtifactId, DateTimeOffset.UtcNow, tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ArchiveRecoveryDecision(entry.ArtifactId, ArchiveArtifactState.Committed, null, null);
        }

        return await MarkRecoveryAsync(entry, "ARCHIVE_RECOVERY_UNKNOWN",
            "Unknown recovery state — leaving row as RecoveryRequired.", cancellationToken).ConfigureAwait(false);
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
        return new ArchiveRecoveryDecision(entry.ArtifactId, ArchiveArtifactState.RecoveryRequired, reasonCode, safeMessage);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}