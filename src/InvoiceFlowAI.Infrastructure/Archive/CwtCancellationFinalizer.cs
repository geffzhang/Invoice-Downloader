using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class CwtCancellationFinalizer : ICwtCancellationFinalizer
{
    private static readonly Regex CancellationNamePattern = new(
        @"取消知会[_\-]?(\S+?)[_\-]\d",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly IArchiveNamingPolicy _namingPolicy;
    private readonly IArchiveArtifactStore _artifactStore;
    private readonly ILegacyArchiveInventoryStore _legacyStore;
    private readonly ICwtArchiveInventory _inventory;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IManualReviewItemStore _reviewStore;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IPairingStore _pairingStore;
    private readonly IAuditEventStore _auditStore;

    public CwtCancellationFinalizer(
        IArchiveNamingPolicy namingPolicy,
        IArchiveArtifactStore artifactStore,
        IArchiveFileSystem fileSystem,
        IManualReviewItemStore reviewStore,
        IUnitOfWorkFactory unitOfWorkFactory,
        IPairingStore pairingStore,
        IAuditEventStore auditStore,
        ILegacyArchiveInventoryStore legacyStore,
        ICwtArchiveInventory inventory)
    {
        _namingPolicy = namingPolicy ?? throw new ArgumentNullException(nameof(namingPolicy));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _legacyStore = legacyStore ?? throw new ArgumentNullException(nameof(legacyStore));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _reviewStore = reviewStore ?? throw new ArgumentNullException(nameof(reviewStore));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
    }

    public async Task<CwtCancellationFinalizationResult> FinalizeAsync(
        string runId,
        string outputRoot,
        IReadOnlyList<CandidateProcessResult> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var inventoryItems = await _inventory.ListAsync(fullRoot, cancellationToken).ConfigureAwait(false);
        var cancellationCandidates = candidates
            .Where(IsCwtCancellation)
            .OrderBy(static candidate => candidate.Candidate.Sequence)
            .ToArray();
        var matchedInventoryIds = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        var updatedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<CwtCancellationFinalizationFailure>();
        var affectedArtifactRuns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cancellation in cancellationCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryExtractPersonName(SourceName(cancellation), out var personName)) continue;

            foreach (var item in inventoryItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchId = item.DocumentId ?? item.InventoryId;
                if (matchedInventoryIds.Contains(item.InventoryId)
                    || item.DocumentId == cancellation.Candidate.DocumentId.Value
                    || !IsNameMatch(personName, item.SourceFileName)
                    || item.ArtifactState is not null && item.ArtifactState != ArchiveArtifactState.Committed
                    || item.LegacyState is not null && item.LegacyState != LegacyArchiveInventoryState.Discovered)
                {
                    continue;
                }

                var failure = await MoveToReviewAsync(
                    fullRoot, runId, cancellation, item, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    failures.Add(failure);
                    continue;
                }

                var relativePath = _namingPolicy.BuildReviewRelativePath(
                    runId, matchId, item.SourceFileName, "CWT_CANCELLATION_MATCH");
                matchedInventoryIds.Add(item.InventoryId);
                matches.Add(matchId);
                updatedPaths[matchId] = relativePath;
                if (item.ArtifactRunId is not null) affectedArtifactRuns.Add(item.ArtifactRunId);
            }
        }

        foreach (var artifactRunId in affectedArtifactRuns.OrderBy(value => value, StringComparer.Ordinal))
        {
            try
            {
                var runSnapshots = await _artifactStore.ListByRunAsync(artifactRunId, cancellationToken).ConfigureAwait(false);
                await using var transaction = await _unitOfWorkFactory
                    .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
                await _pairingStore.ReconcileArchiveStateAsync(
                    artifactRunId, runSnapshots, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failures.Add(new CwtCancellationFinalizationFailure(
                    artifactRunId, "CWT_PAIRING_RECONCILIATION_FAILED", "The matched archive requires pairing reconciliation."));
            }
        }

        return new CwtCancellationFinalizationResult(matches, updatedPaths, failures);
    }

    private async Task<CwtCancellationFinalizationFailure?> MoveToReviewAsync(
        string outputRoot,
        string runId,
        CandidateProcessResult cancellation,
        CwtArchiveInventoryItem item,
        CancellationToken cancellationToken)
    {
        var subjectId = item.DocumentId ?? item.InventoryId;
        var sourcePath = ResolveUnderRoot(outputRoot, item.RelativePath);
        var relativePath = _namingPolicy.BuildReviewRelativePath(
            runId, subjectId, item.SourceFileName, "CWT_CANCELLATION_MATCH");
        var targetPath = ResolveUnderRoot(outputRoot, relativePath);
        var sidecarPath = $"{targetPath}.json";
        var moved = false;
        var sidecarWritten = false;
        try
        {
            if (await _fileSystem.FileExistsAsync(targetPath, cancellationToken).ConfigureAwait(false))
            {
                return new CwtCancellationFinalizationFailure(
                    subjectId, "CWT_MATCH_DESTINATION_EXISTS", "The matched hotel confirmation destination already exists.");
            }

            await _fileSystem.AtomicMoveAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
            moved = true;
            await _fileSystem.FlushToDiskAsync(targetPath, cancellationToken).ConfigureAwait(false);
            var actualHash = await _fileSystem.ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, item.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new CwtFinalizationException("CWT_MATCH_HASH_MISMATCH", "The matched hotel confirmation failed its content hash check.");
            }

            var capturedAtUtc = DateTimeOffset.UtcNow;
            var sidecar = JsonSerializer.Serialize(new CwtRelationshipSidecar(
                "CWT_CANCELLATION_MATCH",
                cancellation.Candidate.DocumentId.Value,
                item.InventoryId,
                item.DocumentId,
                item.ArtifactId,
                item.ContentHash,
                capturedAtUtc));
            await _fileSystem.WriteTextAtomicAsync(sidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
            sidecarWritten = true;

            await using var transaction = await _unitOfWorkFactory
                .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            if (item.Kind == CwtArchiveInventoryItemKind.ArchivedArtifact)
            {
                await _artifactStore.UpdateCommittedLocationAsync(
                    item.ArtifactId!, relativePath, targetPath, Path.GetFileName(targetPath), transaction, cancellationToken).ConfigureAwait(false);
                await _reviewStore.UpsertOpenAsync(
                    item.ArtifactRunId ?? runId, item.DocumentId!, item.ProcessingRevision!.Value,
                    "CWT_CANCELLATION_MATCH", transaction, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _legacyStore.UpdateLocationAsync(
                    item.InventoryId, relativePath, LegacyArchiveInventoryState.Review, runId,
                    transaction, cancellationToken).ConfigureAwait(false);
            }
            var payload = JsonSerializer.Serialize(new
            {
                cancellationDocumentId = cancellation.Candidate.DocumentId.Value,
                inventoryId = item.InventoryId,
                documentId = item.DocumentId,
                artifactId = item.ArtifactId,
                contentHash = item.ContentHash,
                reason = "CWT_CANCELLATION_MATCH",
                capturedAtUtc,
            });
            await _auditStore.AppendAsync(new AuditEventRecord(
                $"audit-{runId}-cwt-match-{item.InventoryId}",
                runId,
                0,
                "archive.cwt_match",
                "archive",
                "archive",
                item.DocumentId,
                item.ProcessingRevision,
                "CWT_CANCELLATION_MATCH",
                payload,
                Sha256Hex(payload),
                capturedAtUtc), transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var recoveryRequired = await RollbackFileMoveAsync(sourcePath, targetPath, sidecarPath, moved, sidecarWritten).ConfigureAwait(false);
            if (recoveryRequired)
                await RecordRelocationRecoveryAsync(runId, item, relativePath, targetPath).ConfigureAwait(false);
            throw;
        }
        catch (CwtFinalizationException exception)
        {
            var recoveryRequired = await RollbackFileMoveAsync(sourcePath, targetPath, sidecarPath, moved, sidecarWritten).ConfigureAwait(false);
            if (recoveryRequired)
            {
                await RecordRelocationRecoveryAsync(runId, item, relativePath, targetPath).ConfigureAwait(false);
                return new CwtCancellationFinalizationFailure(subjectId, "CWT_MATCH_RECOVERY_REQUIRED", "The matched archive requires recovery.");
            }
            return new CwtCancellationFinalizationFailure(subjectId, exception.ReasonCode, exception.Message);
        }
        catch
        {
            var recoveryRequired = await RollbackFileMoveAsync(sourcePath, targetPath, sidecarPath, moved, sidecarWritten).ConfigureAwait(false);
            if (recoveryRequired)
            {
                await RecordRelocationRecoveryAsync(runId, item, relativePath, targetPath).ConfigureAwait(false);
                return new CwtCancellationFinalizationFailure(subjectId, "CWT_MATCH_RECOVERY_REQUIRED", "The matched archive requires recovery.");
            }
            return new CwtCancellationFinalizationFailure(
                subjectId, "CWT_MATCH_RELOCATION_FAILED", "The matched hotel confirmation could not be moved to manual review.");
        }
    }

    private async Task<bool> RollbackFileMoveAsync(string sourcePath, string targetPath, string sidecarPath, bool moved, bool sidecarWritten)
    {
        var rollbackSucceeded = true;
        try
        {
            if (moved && await _fileSystem.FileExistsAsync(targetPath, CancellationToken.None).ConfigureAwait(false))
            {
                await _fileSystem.AtomicMoveAsync(targetPath, sourcePath, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            rollbackSucceeded = false;
        }

        if (rollbackSucceeded && sidecarWritten)
        {
            try
            {
                await _fileSystem.DeleteAsync(sidecarPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                rollbackSucceeded = false;
            }
        }

        return !rollbackSucceeded
            && await _fileSystem.FileExistsAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RecordRelocationRecoveryAsync(
        string runId,
        CwtArchiveInventoryItem item,
        string relativePath,
        string targetPath)
    {
        var safePayload = JsonSerializer.Serialize(new
        {
            inventoryId = item.InventoryId,
            artifactId = item.ArtifactId,
            contentHash = item.ContentHash,
            reason = "CWT_MATCH_RECOVERY_REQUIRED",
            capturedAtUtc = DateTimeOffset.UtcNow,
        });
        await using var transaction = await _unitOfWorkFactory
            .BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None).ConfigureAwait(false);
        if (item.Kind == CwtArchiveInventoryItemKind.ArchivedArtifact)
        {
            await _artifactStore.UpdateCommittedLocationAsync(
                item.ArtifactId!, relativePath, targetPath, Path.GetFileName(targetPath), transaction, CancellationToken.None).ConfigureAwait(false);
            await _artifactStore.MarkRecoveryRequiredAsync(
                item.ArtifactId!, "CWT_MATCH_RECOVERY_REQUIRED", transaction, CancellationToken.None).ConfigureAwait(false);
            await _reviewStore.UpsertOpenAsync(
                item.ArtifactRunId ?? runId, item.DocumentId!, item.ProcessingRevision!.Value,
                "CWT_CANCELLATION_MATCH", transaction, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _legacyStore.UpdateLocationAsync(
                item.InventoryId, relativePath, LegacyArchiveInventoryState.RecoveryRequired, runId,
                transaction, CancellationToken.None).ConfigureAwait(false);
        }
        await _auditStore.AppendAsync(new AuditEventRecord(
            $"audit-{runId}-cwt-recovery-{item.InventoryId}", runId, 0,
            "archive.cwt_recovery_required", "archive", "archive", item.DocumentId,
            item.ProcessingRevision, "CWT_MATCH_RECOVERY_REQUIRED", safePayload,
            Sha256Hex(safePayload), DateTimeOffset.UtcNow), transaction, CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsCwtCancellation(CandidateProcessResult result)
        => result.Candidate.Metadata is { } metadata
            && metadata.TryGetValue("source_is_cwt", out var sourceIsCwt)
            && sourceIsCwt.Equals("true", StringComparison.OrdinalIgnoreCase)
            && SourceName(result).Contains("取消", StringComparison.Ordinal);

    private static bool IsNameMatch(string personName, string fileName)
        => fileName.Contains(personName, StringComparison.Ordinal)
            && fileName.Contains("酒店", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractPersonName(string fileName, out string personName)
    {
        personName = string.Empty;
        if (fileName.Length > 512) return false;
        try
        {
            var match = CancellationNamePattern.Match(fileName);
            if (!match.Success || match.Groups[1].Value.Length < 2) return false;
            personName = match.Groups[1].Value;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string SourceName(CandidateProcessResult result)
        => string.IsNullOrWhiteSpace(result.Candidate.OriginalFileName)
            ? Path.GetFileName(result.ArtifactPath)
            : result.Candidate.OriginalFileName;

    private static string ResolveUnderRoot(string outputRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Archive path must be relative.");
        var fullPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = outputRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(prefix, comparison)) throw new InvalidOperationException("Archive path must remain under the output root.");
        return fullPath;
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CwtRelationshipSidecar(
        string Reason,
        string CancellationDocumentId,
        string InventoryId,
        string? DocumentId,
        string? ArtifactId,
        string ContentHash,
        DateTimeOffset CapturedAtUtc);

    private sealed class CwtFinalizationException(string reasonCode, string safeMessage) : Exception(safeMessage)
    {
        public string ReasonCode { get; } = reasonCode;
    }
}