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
        ArgumentException.ThrowIfNullOrEmpty(request.TempFilePath);
        ArgumentException.ThrowIfNullOrEmpty(request.FinalRelativePath);

        var key = request.Key;
        if (key is null) throw new ArgumentException("Key required.", nameof(request));
        if (string.IsNullOrEmpty(key.ExpectedContentHash))
        {
            throw new ArgumentException("ExpectedContentHash required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;

        // Phase A — hash temp, ensure DB row in State=Prepared.
        var tempHash = await _fileSystem.ComputeSha256Async(request.TempFilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(tempHash, key.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Temp file hash {tempHash} does not match expected {key.ExpectedContentHash}.");
        }

        var artifactId = _artifactIdFactory(key);
        var snapshot = new ArchiveArtifactSnapshot(
            ArtifactId: artifactId,
            Key: key,
            TempFilePath: request.TempFilePath,
            FinalRelativePath: request.FinalRelativePath,
            FileName: request.FileName,
            ExpectedContentHash: key.ExpectedContentHash,
            State: ArchiveArtifactState.Prepared,
            CreatedAtUtc: now,
            CommittedAtUtc: null);

        await using (var txA = await _uowFactory.BeginAsync(TransactionPurpose.ArchivePrepare, cancellationToken).ConfigureAwait(false))
        {
            var existing = await _store.FindByKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.ExpectedContentHash, key.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Hash mismatch on idempotent retry: existing={existing.ExpectedContentHash}, new={key.ExpectedContentHash}.");
                }
                if (existing.State == ArchiveArtifactState.Committed)
                {
                    return new ArchiveCommitResult(
                        existing.ArtifactId,
                        ArchiveArtifactState.Committed,
                        existing.FinalRelativePath,
                        existing.ExpectedContentHash,
                        AlreadyExisted: true);
                }
            }
            else
            {
                await _store.InsertPreparedAsync(snapshot, txA, cancellationToken).ConfigureAwait(false);
            }

            await _auditStore.AppendAsync(BuildAudit(
                key.RunId, "archive.prepare", request, now, key.ExpectedContentHash), txA, cancellationToken).ConfigureAwait(false);

            await txA.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Phase B — atomic move, fsync, hash final, persist Committed state.
        await _fileSystem.AtomicMoveAsync(request.TempFilePath, request.FinalRelativePath, cancellationToken).ConfigureAwait(false);
        await _fileSystem.FlushToDiskAsync(request.FinalRelativePath, cancellationToken).ConfigureAwait(false);

        var finalHash = await _fileSystem.ComputeSha256Async(request.FinalRelativePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(finalHash, key.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            // Orphan: the final file does not match the DB-asserted hash. Mark
            // RecoveryRequired so the user is notified and the file is not
            // silently deleted.
            await using var txOrphan = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await _store.MarkRecoveryRequiredAsync(artifactId, "ARCHIVE_HASH_MISMATCH", txOrphan, cancellationToken).ConfigureAwait(false);
            await txOrphan.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ArchiveCommitResult(artifactId, ArchiveArtifactState.RecoveryRequired, request.FinalRelativePath, finalHash, AlreadyExisted: false);
        }

        await using (var txB = await _uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false))
        {
            await _store.MarkCommittedAsync(artifactId, now, txB, cancellationToken).ConfigureAwait(false);
            await _auditStore.AppendAsync(BuildAudit(
                key.RunId, "archive.commit", request, now, finalHash), txB, cancellationToken).ConfigureAwait(false);
            await txB.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ArchiveCommitResult(artifactId, ArchiveArtifactState.Committed, request.FinalRelativePath, finalHash, AlreadyExisted: false);
    }

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