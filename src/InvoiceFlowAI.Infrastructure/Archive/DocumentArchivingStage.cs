using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class DocumentArchivingStage : IDocumentArchivingStage
{
    private readonly IArchiveNamingPolicy _namingPolicy;
    private readonly IArchiveCommitCoordinator _coordinator;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IPairingStore _pairingStore;
    private readonly IManualReviewItemStore _reviewStore;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICwtCancellationFinalizer? _cwtCancellationFinalizer;

    public DocumentArchivingStage(
        IArchiveNamingPolicy namingPolicy,
        IArchiveCommitCoordinator coordinator,
        IArchiveFileSystem fileSystem,
        IPairingStore pairingStore,
        IManualReviewItemStore reviewStore,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICwtCancellationFinalizer? cwtCancellationFinalizer = null)
    {
        _namingPolicy = namingPolicy ?? throw new ArgumentNullException(nameof(namingPolicy));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _reviewStore = reviewStore ?? throw new ArgumentNullException(nameof(reviewStore));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _cwtCancellationFinalizer = cwtCancellationFinalizer;
    }

    public async Task<ArchiveBatch> ExecuteAsync(ArchiveStageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Batch);
        cancellationToken.ThrowIfCancellationRequested();

        string outputRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.OutputRoot))
            {
                throw new ArgumentException("Run ID and output root are required.");
            }
            outputRoot = Path.GetFullPath(request.OutputRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ArchiveBatch(request.Batch.CandidateResults, [new RunFailure(
                request.RunId ?? string.Empty,
                "archive-documents",
                "ARCHIVE_OUTPUT_ROOT_INVALID",
                FailureCategory.Validation,
                Retryable: false,
                SafeMessage: "The archive output location is invalid.")]);
        }

        var results = request.Batch.CandidateResults.ToArray();
        var candidateIndexes = results
            .Select((result, index) => (Id: result.Candidate.DocumentId.Value, Index: index))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        var cwtCancellationIds = results
            .Where(IsCwtCancellation)
            .Select(static result => result.Candidate.DocumentId.Value)
            .ToHashSet(StringComparer.Ordinal);
        var pairByDocument = BuildPairMap(request.Batch, cwtCancellationIds);
        var processedPairs = new HashSet<PairWork>();
        var outcomes = new List<ArchiveArtifactOutcome>();
        var failures = new List<RunFailure>();

        for (var index = 0; index < results.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = results[index];
            var documentId = result.Candidate.DocumentId.Value;

            if (cwtCancellationIds.Contains(documentId))
            {
                var reviewResult = result with
                {
                    Status = CandidateStatus.ManualReview,
                    Failure = new CandidateFailure(
                        "CWT_HOTEL_CANCELLATION",
                        FailureScope.Candidate,
                        FailureCategory.Validation,
                        Retryable: false,
                        SafeMessage: "A CWT hotel cancellation notice requires manual review."),
                };
                results[index] = reviewResult;
                await ProcessReviewAsync(index, reviewResult, request, outputRoot, results, outcomes, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (pairByDocument.TryGetValue(documentId, out var pair))
            {
                if (processedPairs.Add(pair))
                {
                    await ProcessPairAsync(pair, candidateIndexes, results, outcomes, request, outputRoot, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            switch (result.Status)
            {
                case CandidateStatus.Resolved:
                    await ProcessSingleAsync(index, result, ArchiveArtifactRole.Standalone, request, outputRoot, results, outcomes, cancellationToken).ConfigureAwait(false);
                    break;
                case CandidateStatus.ManualReview:
                case CandidateStatus.Unresolved:
                    await ProcessReviewAsync(index, result, request, outputRoot, results, outcomes, cancellationToken).ConfigureAwait(false);
                    break;
                case CandidateStatus.Retained:
                    await ProcessSingleAsync(index, result, ArchiveArtifactRole.Retained, request, outputRoot, results, outcomes, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (cwtCancellationIds.Count > 0 && _cwtCancellationFinalizer is not null)
        {
            var finalization = await _cwtCancellationFinalizer
                .FinalizeAsync(request.RunId, outputRoot, results, cancellationToken).ConfigureAwait(false);
            foreach (var entry in finalization.UpdatedRelativePaths)
            {
                var index = Array.FindIndex(results, result => result.Candidate.DocumentId.Value == entry.Key);
                if (index >= 0)
                {
                    results[index] = results[index] with
                    {
                        Status = CandidateStatus.ManualReview,
                        ArtifactPath = Path.Combine(outputRoot, entry.Value.Replace('/', Path.DirectorySeparatorChar)),
                        Failure = new CandidateFailure("CWT_CANCELLATION_MATCH", FailureScope.Candidate,
                            FailureCategory.Validation, Retryable: false,
                            SafeMessage: "A related hotel confirmation was moved to manual review."),
                    };
                }
                var artifactIndex = outcomes.FindIndex(outcome => outcome.DocumentId == entry.Key);
                if (artifactIndex >= 0)
                {
                    outcomes[artifactIndex] = outcomes[artifactIndex] with { RelativePath = entry.Value };
                }
            }

            failures.AddRange(finalization.Failures.Select(failure => new RunFailure(
                request.RunId,
                "archive-documents",
                failure.ReasonCode,
                FailureCategory.Persistence,
                Retryable: false,
                SafeMessage: failure.SafeMessage)));
        }

        return new ArchiveBatch(results, failures) { Artifacts = outcomes };
    }

    private async Task ProcessPairAsync(
        PairWork pair,
        IReadOnlyDictionary<string, int> candidateIndexes,
        CandidateProcessResult[] results,
        List<ArchiveArtifactOutcome> outcomes,
        ArchiveStageRequest request,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var assignment = pair.Assignment;
        if (!candidateIndexes.TryGetValue(assignment.Invoice.Id, out var invoiceIndex)
            || !candidateIndexes.TryGetValue(assignment.Companion.Id, out var companionIndex))
        {
            SetFailure(results, candidateIndexes, assignment.Invoice.Id, "PAIRING_DOCUMENT_MISSING", "A paired document is unavailable for archiving.");
            SetFailure(results, candidateIndexes, assignment.Companion.Id, "PAIRING_DOCUMENT_MISSING", "A paired document is unavailable for archiving.");
            return;
        }

        var invoiceResult = results[invoiceIndex];
        var companionResult = results[companionIndex];
        var invoiceName = SourceName(invoiceResult);
        var companionName = SourceName(companionResult);
        ArchivePairPaths paths;
        string invoiceHash;
        string companionHash;
        try
        {
            paths = _namingPolicy.BuildPairRelativePaths(
                invoiceResult.Invoice!, invoiceName, companionResult.Invoice!, companionName,
                request.RunId, pair.PairIndex, pair.Family);
            invoiceHash = await ValidateAndHashSourceAsync(invoiceResult, cancellationToken).ConfigureAwait(false);
            companionHash = await ValidateAndHashSourceAsync(companionResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArchiveStageException exception)
        {
            SetFailure(results, invoiceIndex, exception.ReasonCode, exception.SafeMessage);
            SetFailure(results, companionIndex, exception.ReasonCode, exception.SafeMessage);
            return;
        }
        catch (Exception)
        {
            SetFailure(results, invoiceIndex, "ARCHIVE_SOURCE_READ_FAILED", "A paired source document could not be verified.");
            SetFailure(results, companionIndex, "ARCHIVE_SOURCE_READ_FAILED", "A paired source document could not be verified.");
            return;
        }

        var pairingRecord = new PairingRecord(
            request.RunId,
            assignment.Invoice.Id,
            invoiceResult.Candidate.ProcessingRevision,
            [new PairingCompanionRecord(assignment.Companion.Id, companionResult.Candidate.ProcessingRevision)],
            assignment.Score,
            "Prepared",
            string.Empty);

        try
        {
            await UpsertPairAsync(pairingRecord, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            SetFailure(results, invoiceIndex, "PAIRING_PREPARE_FAILED", "The paired archive operation could not be prepared.");
            SetFailure(results, companionIndex, "PAIRING_PREPARE_FAILED", "The paired archive operation could not be prepared.");
            return;
        }

        var invoiceOutcome = await CommitArtifactSafelyAsync(
            invoiceResult, paths.InvoiceRelativePath, ArchiveRole(pair.Family, companion: false), invoiceHash,
            request, outputRoot, cancellationToken).ConfigureAwait(false);
        var companionOutcome = await CommitArtifactSafelyAsync(
            companionResult, paths.CompanionRelativePath, ArchiveRole(pair.Family, companion: true), companionHash,
            request, outputRoot, cancellationToken).ConfigureAwait(false);
        outcomes.Add(invoiceOutcome.Outcome);
        outcomes.Add(companionOutcome.Outcome);

        var bothCommitted = invoiceOutcome.Outcome.State == ArchiveArtifactState.Committed
            && companionOutcome.Outcome.State == ArchiveArtifactState.Committed;
        var finalRecord = pairingRecord with
        {
            State = bothCommitted ? "Committed" : "RecoveryRequired",
            ReasonCode = bothCommitted ? string.Empty : "PAIR_ARCHIVE_PARTIAL_COMMIT",
        };
        try
        {
            await UpsertPairAsync(finalRecord, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            bothCommitted = false;
        }

        if (!bothCommitted)
        {
            SetFailure(results, invoiceIndex,
                invoiceOutcome.Failure?.ReasonCode ?? "PAIR_ARCHIVE_RECOVERY_REQUIRED",
                invoiceOutcome.Failure?.SafeMessage ?? "The paired archive operation requires recovery.");
            SetFailure(results, companionIndex,
                companionOutcome.Failure?.ReasonCode ?? "PAIR_ARCHIVE_RECOVERY_REQUIRED",
                companionOutcome.Failure?.SafeMessage ?? "The paired archive operation requires recovery.");
        }
    }

    private async Task ProcessSingleAsync(
        int index,
        CandidateProcessResult result,
        ArchiveArtifactRole role,
        ArchiveStageRequest request,
        string outputRoot,
        CandidateProcessResult[] results,
        List<ArchiveArtifactOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (result.Invoice is null)
        {
            SetFailure(results, index, "ARCHIVE_INVOICE_MISSING", "The extracted document data is unavailable for archiving.");
            return;
        }

        try
        {
            var hash = await ValidateAndHashSourceAsync(result, cancellationToken).ConfigureAwait(false);
            var relativePath = role == ArchiveArtifactRole.Retained
                ? _namingPolicy.BuildRetainedRelativePath(request.RunId, result.Candidate.DocumentId.Value, SourceName(result))
                : _namingPolicy.BuildRelativePath(result.Invoice, request.RunId);
            var committed = await CommitArtifactSafelyAsync(result, relativePath, RoleName(role), hash, request, outputRoot, cancellationToken).ConfigureAwait(false);
            outcomes.Add(committed.Outcome);
            if (committed.Failure is not null)
            {
                SetFailure(results, index, committed.Failure.ReasonCode, committed.Failure.SafeMessage);
            }
        }
        catch (ArchiveStageException exception)
        {
            SetFailure(results, index, exception.ReasonCode, exception.SafeMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SetFailure(results, index, "ARCHIVE_COMMIT_FAILED", "The document could not be archived.");
        }
    }

    private async Task ProcessReviewAsync(
        int index,
        CandidateProcessResult result,
        ArchiveStageRequest request,
        string outputRoot,
        CandidateProcessResult[] results,
        List<ArchiveArtifactOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var reasonCode = result.Failure?.ReasonCode;
        if (string.IsNullOrWhiteSpace(reasonCode)) reasonCode = "MANUAL_REVIEW_REQUIRED";

        try
        {
            await using (var transaction = await _unitOfWorkFactory.BeginAsync(TransactionPurpose.PairingCommit, cancellationToken).ConfigureAwait(false))
            {
                await _reviewStore.UpsertOpenAsync(
                    request.RunId,
                    result.Candidate.DocumentId.Value,
                    result.Candidate.ProcessingRevision,
                    reasonCode,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = await ValidateAndHashSourceAsync(result, cancellationToken).ConfigureAwait(false);
            var relativePath = _namingPolicy.BuildReviewRelativePath(
                request.RunId,
                result.Candidate.DocumentId.Value,
                SourceName(result),
                reasonCode);
            var committed = await CommitArtifactSafelyAsync(result, relativePath, RoleName(ArchiveArtifactRole.Review), hash, request, outputRoot, cancellationToken).ConfigureAwait(false);
            outcomes.Add(committed.Outcome);
            if (committed.Failure is not null)
            {
                SetFailure(results, index, committed.Failure.ReasonCode, committed.Failure.SafeMessage);
            }
        }
        catch (ArchiveStageException exception)
        {
            SetFailure(results, index, exception.ReasonCode, exception.SafeMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SetFailure(results, index, "ARCHIVE_REVIEW_FAILED", "The document could not be prepared for review.");
        }
    }

    private async Task<CommitOutcome> CommitArtifactAsync(
        CandidateProcessResult result,
        string relativePath,
        string role,
        string contentHash,
        ArchiveStageRequest request,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var finalPath = ResolveUnderRoot(outputRoot, relativePath);
        var commit = await _coordinator.CommitAsync(new ArchiveCommitRequest(
            new ArchiveArtifactKey(
                request.RunId,
                result.Candidate.DocumentId.Value,
                result.Candidate.ProcessingRevision,
                role,
                contentHash),
            result.ArtifactPath,
            finalPath,
            relativePath,
            Path.GetFileName(relativePath)), cancellationToken).ConfigureAwait(false);
        var outcome = new ArchiveArtifactOutcome(
            result.Candidate.DocumentId.Value,
            commit.State,
            commit.FinalRelativePath,
            commit.ReasonCode);
        var failure = commit.State == ArchiveArtifactState.Committed
            ? null
            : new CandidateFailure(
                commit.ReasonCode ?? "ARCHIVE_RECOVERY_REQUIRED",
                FailureScope.Candidate,
                FailureCategory.Persistence,
                Retryable: false,
                SafeMessage: "The archived copy requires recovery.");
        return new CommitOutcome(outcome, failure);
    }

    private async Task<CommitOutcome> CommitArtifactSafelyAsync(
        CandidateProcessResult result,
        string relativePath,
        string role,
        string contentHash,
        ArchiveStageRequest request,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommitArtifactAsync(result, relativePath, role, contentHash, request, outputRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArchivePathCollisionException)
        {
            var reasonCode = ArchivePathCollisionException.StableReasonCode;
            var failure = CandidateFailureForArchive(reasonCode, "The archive destination is already occupied.");
            return new CommitOutcome(new ArchiveArtifactOutcome(result.Candidate.DocumentId.Value, ArchiveArtifactState.Absent, null, reasonCode), failure);
        }
        catch (ArchiveStageException exception)
        {
            var failure = CandidateFailureForArchive(exception.ReasonCode, exception.SafeMessage);
            return new CommitOutcome(new ArchiveArtifactOutcome(result.Candidate.DocumentId.Value, ArchiveArtifactState.Absent, null, exception.ReasonCode), failure);
        }
        catch (Exception)
        {
            const string reasonCode = "ARCHIVE_COMMIT_FAILED";
            var failure = CandidateFailureForArchive(reasonCode, "The archive operation requires recovery.");
            return new CommitOutcome(new ArchiveArtifactOutcome(result.Candidate.DocumentId.Value, ArchiveArtifactState.RecoveryRequired, relativePath, reasonCode), failure);
        }
    }

    private static CandidateFailure CandidateFailureForArchive(string reasonCode, string safeMessage) =>
        new(reasonCode, FailureScope.Candidate, FailureCategory.Persistence, false, safeMessage);

    private async Task<string> ValidateAndHashSourceAsync(CandidateProcessResult result, CancellationToken cancellationToken)
    {
        var sourcePath = result.ArtifactPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !await _fileSystem.FileExistsAsync(sourcePath, cancellationToken).ConfigureAwait(false))
        {
            throw new ArchiveStageException("ARCHIVE_SOURCE_MISSING", "The source document is unavailable for archiving.");
        }

        var actualHash = await _fileSystem.ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        var expectedHash = result.Invoice?.ContentHash;
        if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveStageException("ARCHIVE_SOURCE_HASH_MISMATCH", "The source document changed before it could be archived.");
        }
        return actualHash;
    }

    private async Task UpsertPairAsync(PairingRecord record, CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWorkFactory.BeginAsync(TransactionPurpose.PairingCommit, cancellationToken).ConfigureAwait(false);
        await _pairingStore.UpsertAsync(record, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, PairWork> BuildPairMap(PairingBatch batch, IReadOnlySet<string> excludedDocumentIds)
    {
        var pairWorks = batch.Results
            .SelectMany(result => result.Pairs)
            .Where(assignment => !excludedDocumentIds.Contains(assignment.Invoice.Id)
                && !excludedDocumentIds.Contains(assignment.Companion.Id))
            .Select(assignment => new PairWork(
                assignment,
                PairFamily(assignment.Invoice.Role)))
            .OrderBy(pair => pair.Family, StringComparer.Ordinal)
            .ThenBy(pair => pair.Assignment.Invoice.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.Assignment.Companion.Id, StringComparer.Ordinal)
            .ToArray();
        var indexesByFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        var map = new Dictionary<string, PairWork>(StringComparer.Ordinal);
        foreach (var pair in pairWorks)
        {
            var index = indexesByFamily.GetValueOrDefault(pair.Family) + 1;
            indexesByFamily[pair.Family] = index;
            var indexed = pair with { PairIndex = index };
            map[indexed.Assignment.Invoice.Id] = indexed;
            map[indexed.Assignment.Companion.Id] = indexed;
        }
        return map;
    }

    private static bool IsCwtCancellation(CandidateProcessResult result)
        => result.Candidate.Metadata is { } metadata
            && metadata.TryGetValue("source_is_cwt", out var sourceIsCwt)
            && sourceIsCwt.Equals("true", StringComparison.OrdinalIgnoreCase)
            && SourceName(result).Contains("取消", StringComparison.Ordinal);

    private static string PairFamily(PairingRole role) => role switch
    {
        PairingRole.RideInvoice or PairingRole.RideItinerary => "ride",
        PairingRole.HotelInvoice or PairingRole.HotelFolio => "hotel",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string ArchiveRole(string family, bool companion) => family switch
    {
        "ride" => companion ? "ride_itinerary" : "ride_invoice",
        "hotel" => companion ? "hotel_folio" : "hotel_invoice",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    private static string RoleName(ArchiveArtifactRole role) => role switch
    {
        ArchiveArtifactRole.Standalone => "standalone",
        ArchiveArtifactRole.Review => "manual_review",
        ArchiveArtifactRole.Retained => "retained",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string SourceName(CandidateProcessResult result)
        => string.IsNullOrWhiteSpace(result.Candidate.OriginalFileName)
            ? Path.GetFileName(result.ArtifactPath)
            : result.Candidate.OriginalFileName;

    private static string ResolveUnderRoot(string outputRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArchiveStageException("ARCHIVE_PATH_INVALID", "The archive destination is invalid.");
        }
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(prefix, comparison))
        {
            throw new ArchiveStageException("ARCHIVE_PATH_INVALID", "The archive destination is invalid.");
        }
        return fullPath;
    }

    private static void SetFailure(CandidateProcessResult[] results, int index, string reasonCode, string safeMessage)
    {
        results[index] = results[index] with
        {
            Status = CandidateStatus.ManualReview,
            Failure = new CandidateFailure(reasonCode, FailureScope.Candidate, FailureCategory.Persistence, false, safeMessage),
        };
    }

    private static void SetFailure(
        CandidateProcessResult[] results,
        IReadOnlyDictionary<string, int> indexes,
        string documentId,
        string reasonCode,
        string safeMessage)
    {
        if (indexes.TryGetValue(documentId, out var index)) SetFailure(results, index, reasonCode, safeMessage);
    }

    private sealed record PairWork(PairingAssignment Assignment, string Family, int PairIndex = 0);
    private sealed record CommitOutcome(ArchiveArtifactOutcome Outcome, CandidateFailure? Failure);

    private enum ArchiveArtifactRole { Standalone, Review, Retained }

    private sealed class ArchiveStageException(string reasonCode, string safeMessage) : Exception(safeMessage)
    {
        public string ReasonCode { get; } = reasonCode;
        public string SafeMessage { get; } = safeMessage;
    }
}
