// Two-phase commit coordinator per design §7 / §11:
//
//   Phase A (transactional): hash the temp file, compare with the requested
//     ExpectedContentHash, then insert (or no-op if a prior Prepared row
//     matches the key) the ArchivedArtifacts row in State=Prepared and
//     append an audit row. Commit. The temp file is left in place — the
//     move happens after the DB knows the row exists.
//
//   Phase B (filesystem + DB): atomically rename the temp file to the
//     final relative path, fsync the parent directory, recompute the
//     hash of the final file, and — only if it matches the expected hash
//     — open a second transaction that flips the row to State=Committed
//     and appends a matching audit row. Any failure between Phase A and
//     the commit of Phase B leaves the row in State=Prepared, which the
//     recovery service resolves on the next start-up.

using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Application.Archive;

public sealed class ArchiveCommitCoordinator : IArchiveCommitCoordinator
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IArchiveArtifactStore _store;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IAuditEventStore _auditStore;
    private readonly Func<ArchiveArtifactKey, string> _artifactIdFactory;

    public ArchiveCommitCoordinator(
        IUnitOfWorkFactory uowFactory,
        IArchiveArtifactStore store,
        IArchiveFileSystem fileSystem,
        IAuditEventStore auditStore,
        Func<ArchiveArtifactKey, string>? artifactIdFactory = null)
    {
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _artifactIdFactory = artifactIdFactory ?? DefaultArtifactIdFactory;
    }

    public async Task<ArchiveCommitResult> CommitAsync(ArchiveCommitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.SourceFilePath);
        ArgumentException.ThrowIfNullOrEmpty(request.FinalFilePath);
        ArgumentException.ThrowIfNullOrEmpty(request.FinalRelativePath);

        var key = request.Key;
        if (key is null) throw new ArgumentException("Key required.", nameof(request));
        if (string.IsNullOrEmpty(key.ExpectedContentHash))
        {
            throw new ArgumentException("ExpectedContentHash required.", nameof(request));
        }

        var existing = await _store.FindByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameContent(existing, key);
            if (existing.State == ArchiveArtifactState.Committed)
            {
                return BuildResult(existing, alreadyExisted: true);
            }
            if (existing.State == ArchiveArtifactState.RecoveryRequired)
            {
                return BuildResult(existing, alreadyExisted: true, reasonCode: "ARCHIVE_RECOVERY_REQUIRED");
            }
            if (existing.State == ArchiveArtifactState.Prepared)
            {
                return await CompletePreparedAsync(existing, request, alreadyExisted: true, cancellationToken).ConfigureAwait(false);
            }
        }

        if (await _fileSystem.FileExistsAsync(request.FinalFilePath, cancellationToken).ConfigureAwait(false))
        {
            throw new ArchivePathCollisionException(request.FinalFilePath);
        }

        var tempFilePath = await _fileSystem.CopyToSiblingTempAsync(
            request.SourceFilePath,
            request.FinalFilePath,
            cancellationToken).ConfigureAwait(false);
        var phaseAPersisted = false;
        var phaseACommitAttempted = false;
        ArchiveArtifactSnapshot preparedSnapshot;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var tempHash = await _fileSystem.ComputeSha256Async(tempFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(tempHash, key.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Archive copy does not match the expected source hash.");
            }

            var artifactId = _artifactIdFactory(key);
            preparedSnapshot = new ArchiveArtifactSnapshot(
                ArtifactId: artifactId,
                Key: key,
                TempFilePath: tempFilePath,
                FinalRelativePath: request.FinalRelativePath,
                FileName: request.FileName,
                ExpectedContentHash: key.ExpectedContentHash,
                State: ArchiveArtifactState.Prepared,
                CreatedAtUtc: now,
                CommittedAtUtc: null,
                FinalFilePath: request.FinalFilePath,
                SourceFileName: request.SourceFileName);

            await using var txA = await _uowFactory.BeginAsync(TransactionPurpose.ArchivePrepare, cancellationToken).ConfigureAwait(false);
            var current = await _store.FindByKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                EnsureSameContent(current, key);
                if (current.State == ArchiveArtifactState.Committed)
                {
                    await txA.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await _fileSystem.DeleteAsync(tempFilePath, cancellationToken).ConfigureAwait(false);
                    return BuildResult(current, alreadyExisted: true);
                }
                if (current.State != ArchiveArtifactState.Prepared)
                {
                    throw new InvalidOperationException("An archive retry is already marked for recovery.");
                }
                preparedSnapshot = current;
                phaseACommitAttempted = true;
                await txA.CommitAsync(cancellationToken).ConfigureAwait(false);
                phaseAPersisted = true;
                await _fileSystem.DeleteAsync(tempFilePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _store.InsertPreparedAsync(preparedSnapshot, txA, cancellationToken).ConfigureAwait(false);
                await _auditStore.AppendAsync(BuildAudit(
                    key.RunId, "archive.prepare", request, now, key.ExpectedContentHash), txA, cancellationToken).ConfigureAwait(false);
                phaseACommitAttempted = true;
                await txA.CommitAsync(cancellationToken).ConfigureAwait(false);
                phaseAPersisted = true;
            }
        }
        catch
        {
            if (!phaseAPersisted && !phaseACommitAttempted)
            {
                await _fileSystem.DeleteAsync(tempFilePath, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }

        return await CompletePreparedAsync(preparedSnapshot, request, alreadyExisted: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArchiveCommitResult> CompletePreparedAsync(
        ArchiveArtifactSnapshot snapshot,
        ArchiveCommitRequest request,
        bool alreadyExisted,
        CancellationToken cancellationToken)
    {
        var finalFilePath = snapshot.FinalFilePath ?? request.FinalFilePath;
        if (await _fileSystem.FileExistsAsync(finalFilePath, cancellationToken).ConfigureAwait(false))
        {
            var existingFinalHash = await _fileSystem.ComputeSha256Async(finalFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingFinalHash, snapshot.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await MarkRecoveryRequiredAsync(snapshot, "ARCHIVE_HASH_MISMATCH", existingFinalHash, cancellationToken).ConfigureAwait(false);
            }
            return await MarkCommittedAsync(snapshot, request, existingFinalHash, alreadyExisted, cancellationToken).ConfigureAwait(false);
        }

        if (!await _fileSystem.FileExistsAsync(snapshot.TempFilePath, cancellationToken).ConfigureAwait(false))
        {
            return await MarkRecoveryRequiredAsync(snapshot, "ARCHIVE_BOTH_FILES_MISSING", snapshot.ExpectedContentHash, cancellationToken).ConfigureAwait(false);
        }

        var tempHash = await _fileSystem.ComputeSha256Async(snapshot.TempFilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(tempHash, snapshot.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return await MarkRecoveryRequiredAsync(snapshot, "ARCHIVE_TEMP_HASH_MISMATCH", tempHash, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _fileSystem.AtomicMoveAsync(snapshot.TempFilePath, finalFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            if (await _fileSystem.FileExistsAsync(finalFilePath, cancellationToken).ConfigureAwait(false))
            {
                return await MarkRecoveryRequiredAsync(
                    snapshot,
                    ArchivePathCollisionException.StableReasonCode,
                    snapshot.ExpectedContentHash,
                    cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        await _fileSystem.FlushToDiskAsync(finalFilePath, cancellationToken).ConfigureAwait(false);
        var finalHash = await _fileSystem.ComputeSha256Async(finalFilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(finalHash, snapshot.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return await MarkRecoveryRequiredAsync(snapshot, "ARCHIVE_HASH_MISMATCH", finalHash, cancellationToken).ConfigureAwait(false);
        }

        return await MarkCommittedAsync(snapshot, request, finalHash, alreadyExisted, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArchiveCommitResult> MarkCommittedAsync(
        ArchiveArtifactSnapshot snapshot,
        ArchiveCommitRequest request,
        string contentHash,
        bool alreadyExisted,
        CancellationToken cancellationToken)
    {
        var committedAtUtc = DateTimeOffset.UtcNow;
        await using var transaction = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
        await _store.MarkCommittedAsync(snapshot.ArtifactId, committedAtUtc, transaction, cancellationToken).ConfigureAwait(false);
        await _auditStore.AppendAsync(BuildAudit(
            snapshot.Key.RunId, "archive.commit", request, committedAtUtc, contentHash), transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveCommitResult(snapshot.ArtifactId, ArchiveArtifactState.Committed, snapshot.FinalRelativePath, contentHash, alreadyExisted);
    }

    private async Task<ArchiveCommitResult> MarkRecoveryRequiredAsync(
        ArchiveArtifactSnapshot snapshot,
        string reasonCode,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
        await _store.MarkRecoveryRequiredAsync(snapshot.ArtifactId, reasonCode, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveCommitResult(
            snapshot.ArtifactId,
            ArchiveArtifactState.RecoveryRequired,
            snapshot.FinalRelativePath,
            contentHash,
            AlreadyExisted: false,
            ReasonCode: reasonCode);
    }

    private static void EnsureSameContent(ArchiveArtifactSnapshot existing, ArchiveArtifactKey requestedKey)
    {
        if (!string.Equals(existing.ExpectedContentHash, requestedKey.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Hash mismatch on idempotent retry: existing={existing.ExpectedContentHash}, new={requestedKey.ExpectedContentHash}.");
        }
    }

    private static ArchiveCommitResult BuildResult(
        ArchiveArtifactSnapshot snapshot,
        bool alreadyExisted,
        string? reasonCode = null) =>
        new(
            snapshot.ArtifactId,
            snapshot.State,
            snapshot.FinalRelativePath,
            snapshot.ExpectedContentHash,
            alreadyExisted,
            reasonCode);

    private static AuditEventRecord BuildAudit(
        string runId,
        string eventType,
        ArchiveCommitRequest request,
        DateTimeOffset occurredAtUtc,
        string contentHash)
    {
        var payload = $"{{\"documentId\":\"{request.Key.DocumentId}\",\"processingRevision\":{request.Key.ProcessingRevision},\"role\":\"{request.Key.Role}\",\"contentHash\":\"{contentHash}\"}}";
        return new AuditEventRecord(
            AuditEventId: $"audit-{runId}-{eventType}-{request.Key.DocumentId}-{request.Key.ProcessingRevision}",
            RunId: runId,
            EventSequence: 0,
            EventType: eventType,
            Stage: "archive",
            NodeId: "archive",
            DocumentId: request.Key.DocumentId,
            ProcessingRevision: request.Key.ProcessingRevision,
            ReasonCode: eventType,
            PayloadJson: payload,
            PayloadHash: Sha256Hex(payload),
            OccurredAtUtc: occurredAtUtc);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DefaultArtifactIdFactory(ArchiveArtifactKey key) =>
        $"archive-{key.RunId}-{key.DocumentId}-{key.ProcessingRevision}-{key.Role}".ToLowerInvariant();
}