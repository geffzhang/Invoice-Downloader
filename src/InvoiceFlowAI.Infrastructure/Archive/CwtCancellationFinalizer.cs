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
        IAuditEventStore auditStore)
    {
        _namingPolicy = namingPolicy ?? throw new ArgumentNullException(nameof(namingPolicy));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
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
        var snapshots = await _artifactStore.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var reconciliationSnapshots = snapshots.ToList();
        var cancellationCandidates = candidates
            .Where(IsCwtCancellation)
            .OrderBy(static candidate => candidate.Candidate.Sequence)
            .ToArray();
        var confirmationCandidates = candidates
            .OrderBy(static candidate => candidate.Candidate.Sequence)
            .ToArray();
        var matchedDocuments = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        var updatedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<CwtCancellationFinalizationFailure>();

        foreach (var cancellation in cancellationCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryExtractPersonName(SourceName(cancellation), out var personName)) continue;

            foreach (var confirmation in confirmationCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var documentId = confirmation.Candidate.DocumentId.Value;
                var snapshot = reconciliationSnapshots
                    .Where(snapshot => snapshot.State == ArchiveArtifactState.Committed
                        && snapshot.Key.DocumentId == documentId
                        && snapshot.Key.ProcessingRevision == confirmation.Candidate.ProcessingRevision
                        && !snapshot.FinalRelativePath.Replace('\\', '/').Contains("/review/", StringComparison.Ordinal))
                    .OrderBy(snapshot => snapshot.Key.Role, StringComparer.Ordinal)
                    .ThenBy(snapshot => snapshot.ArtifactId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (matchedDocuments.Contains(documentId)
                    || documentId == cancellation.Candidate.DocumentId.Value
                    || !IsNameMatch(personName, SourceName(confirmation))
                    || snapshot is null)
                {
                    continue;
                }

                var failure = await MoveToReviewAsync(
                    fullRoot, runId, cancellation, confirmation, snapshot, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    failures.Add(failure);
                    continue;
                }

                var relativePath = _namingPolicy.BuildReviewRelativePath(
                    runId, documentId, SourceName(confirmation), "CWT_CANCELLATION_MATCH");
                var finalPath = ResolveUnderRoot(fullRoot, relativePath);
                var updatedSnapshot = snapshot with
                {
                    FinalRelativePath = relativePath,
                    FinalFilePath = finalPath,
                    FileName = Path.GetFileName(finalPath),
                };
                var snapshotIndex = reconciliationSnapshots.FindIndex(item => item.ArtifactId == snapshot.ArtifactId);
                if (snapshotIndex >= 0) reconciliationSnapshots[snapshotIndex] = updatedSnapshot;
                matchedDocuments.Add(documentId);
                matches.Add(documentId);
                updatedPaths[documentId] = relativePath;
            }
        }

        if (matches.Count > 0)
        {
            try
            {
                await using var transaction = await _unitOfWorkFactory
                    .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
                await _pairingStore.ReconcileArchiveStateAsync(
                    runId, reconciliationSnapshots, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failures.Add(new CwtCancellationFinalizationFailure(
                    matches[^1], "CWT_PAIRING_RECONCILIATION_FAILED", "The matched archive requires pairing reconciliation."));
            }
        }

        return new CwtCancellationFinalizationResult(matches, updatedPaths, failures);
    }

    private async Task<CwtCancellationFinalizationFailure?> MoveToReviewAsync(
        string outputRoot,
        string runId,
        CandidateProcessResult cancellation,
        CandidateProcessResult confirmation,
        ArchiveArtifactSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var documentId = confirmation.Candidate.DocumentId.Value;
        var sourcePath = ResolveUnderRoot(outputRoot, snapshot.FinalRelativePath);
        var relativePath = _namingPolicy.BuildReviewRelativePath(
            runId, documentId, SourceName(confirmation), "CWT_CANCELLATION_MATCH");
        var targetPath = ResolveUnderRoot(outputRoot, relativePath);
        var sidecarPath = $"{targetPath}.json";
        var moved = false;
        var sidecarWritten = false;
        try
        {
            if (await _fileSystem.FileExistsAsync(targetPath, cancellationToken).ConfigureAwait(false))
            {
                return new CwtCancellationFinalizationFailure(
                    documentId, "CWT_MATCH_DESTINATION_EXISTS", "The matched hotel confirmation destination already exists.");
            }

            await _fileSystem.AtomicMoveAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
            moved = true;
            await _fileSystem.FlushToDiskAsync(targetPath, cancellationToken).ConfigureAwait(false);
            var actualHash = await _fileSystem.ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, snapshot.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new CwtFinalizationException("CWT_MATCH_HASH_MISMATCH", "The matched hotel confirmation failed its content hash check.");
            }

            var capturedAtUtc = DateTimeOffset.UtcNow;
            var sidecar = JsonSerializer.Serialize(new CwtRelationshipSidecar(
                "CWT_CANCELLATION_MATCH",
                cancellation.Candidate.DocumentId.Value,
                documentId,
                snapshot.ArtifactId,
                snapshot.ExpectedContentHash,
                capturedAtUtc));
            await _fileSystem.WriteTextAtomicAsync(sidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
            sidecarWritten = true;

            await using var transaction = await _unitOfWorkFactory
                .BeginAsync(TransactionPurpose.ArchiveCommit, cancellationToken).ConfigureAwait(false);
            await _artifactStore.UpdateCommittedLocationAsync(
                snapshot.ArtifactId, relativePath, targetPath, Path.GetFileName(targetPath), transaction, cancellationToken).ConfigureAwait(false);
            await _reviewStore.UpsertOpenAsync(
                runId, documentId, confirmation.Candidate.ProcessingRevision,
                "CWT_CANCELLATION_MATCH", transaction, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new
            {
                cancellationDocumentId = cancellation.Candidate.DocumentId.Value,
                confirmationDocumentId = documentId,
                artifactId = snapshot.ArtifactId,
            });
            await _auditStore.AppendAsync(new AuditEventRecord(
                $"audit-{runId}-cwt-match-{documentId}",
                runId,
                0,
                "archive.cwt_match",
                "archive",
                "archive",
                documentId,
                confirmation.Candidate.ProcessingRevision,
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
                await RecordRelocationRecoveryAsync(runId, confirmation, snapshot, relativePath, targetPath).ConfigureAwait(false);
            throw;
        }
        catch (CwtFinalizationException exception)
        {
            var recoveryRequired = await RollbackFileMoveAsync(sourcePath, targetPath, sidecarPath, moved, sidecarWritten).ConfigureAwait(false);
            if (recoveryRequired)
            {
                await RecordRelocationRecoveryAsync(runId, confirmation, snapshot, relativePath, targetPath).ConfigureAwait(false);
                return new CwtCancellationFinalizationFailure(documentId, "CWT_MATCH_RECOVERY_REQUIRED", "The matched archive requires recovery.");
            }
            return new CwtCancellationFinalizationFailure(documentId, exception.ReasonCode, exception.Message);
        }
        catch
        {
            var recoveryRequired = await RollbackFileMoveAsync(sourcePath, targetPath, sidecarPath, moved, sidecarWritten).ConfigureAwait(false);
            if (recoveryRequired)
            {
                await RecordRelocationRecoveryAsync(runId, confirmation, snapshot, relativePath, targetPath).ConfigureAwait(false);
                return new CwtCancellationFinalizationFailure(documentId, "CWT_MATCH_RECOVERY_REQUIRED", "The matched archive requires recovery.");
            }
            return new CwtCancellationFinalizationFailure(
                documentId, "CWT_MATCH_RELOCATION_FAILED", "The matched hotel confirmation could not be moved to manual review.");
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
        CandidateProcessResult confirmation,
        ArchiveArtifactSnapshot snapshot,
        string relativePath,
        string targetPath)
    {
        var safePayload = JsonSerializer.Serialize(new { artifactId = snapshot.ArtifactId, reason = "CWT_MATCH_RECOVERY_REQUIRED" });
        await using var transaction = await _unitOfWorkFactory
            .BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None).ConfigureAwait(false);
        await _artifactStore.UpdateCommittedLocationAsync(
            snapshot.ArtifactId, relativePath, targetPath, Path.GetFileName(targetPath), transaction, CancellationToken.None).ConfigureAwait(false);
        await _artifactStore.MarkRecoveryRequiredAsync(
            snapshot.ArtifactId, "CWT_MATCH_RECOVERY_REQUIRED", transaction, CancellationToken.None).ConfigureAwait(false);
        await _reviewStore.UpsertOpenAsync(
            runId, confirmation.Candidate.DocumentId.Value, confirmation.Candidate.ProcessingRevision,
            "CWT_CANCELLATION_MATCH", transaction, CancellationToken.None).ConfigureAwait(false);
        await _auditStore.AppendAsync(new AuditEventRecord(
            $"audit-{runId}-cwt-recovery-{snapshot.ArtifactId}", runId, 0,
            "archive.cwt_recovery_required", "archive", "archive", confirmation.Candidate.DocumentId.Value,
            confirmation.Candidate.ProcessingRevision, "CWT_MATCH_RECOVERY_REQUIRED", safePayload,
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
        string ConfirmationDocumentId,
        string ArtifactId,
        string ContentHash,
        DateTimeOffset CapturedAtUtc);

    private sealed class CwtFinalizationException(string reasonCode, string safeMessage) : Exception(safeMessage)
    {
        public string ReasonCode { get; } = reasonCode;
    }
}