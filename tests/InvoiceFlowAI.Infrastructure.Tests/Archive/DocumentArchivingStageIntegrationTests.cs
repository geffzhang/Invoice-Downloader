using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class DocumentArchivingStageIntegrationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public DocumentArchivingStageIntegrationTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Real_pair_archive_commits_both_copies_and_audit_events_without_moving_sources()
    {
        await _fixture.ResetAsync();
        const string runId = "run-real-pair";
        var invoiceSource = CreateSource("real-invoice.pdf", "real invoice bytes");
        var companionSource = CreateSource("real-itinerary.ofd", "real itinerary bytes");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-real-output-{Guid.NewGuid():N}");
        try
        {
            await SeedRunAndDocumentsAsync(runId, "real-invoice", "real-itinerary");
            await using var context = _fixture.CreateContext();
            var uowFactory = new EfUnitOfWorkFactory(context);
            var fileSystem = new PhysicalArchiveFileSystem();
            var pairingStore = new EfPairingStore(context);
            var archiveStore = new EfArchiveArtifactStore(context);
            var stage = new DocumentArchivingStage(
                new ArchiveNamingPolicy(),
                new ArchiveCommitCoordinator(uowFactory, archiveStore, fileSystem, new EfAuditStore(context)),
                fileSystem,
                pairingStore,
                new EfManualReviewItemStore(context),
                uowFactory);

            var invoice = NewResult("real-invoice", invoiceSource, InvoiceDocumentType.RideInvoice, "real invoice bytes");
            var companion = NewResult("real-itinerary", companionSource, InvoiceDocumentType.RideItinerary, "real itinerary bytes");
            var assignment = new PairingAssignment(
                NewPairingDocument(invoice, PairingRole.RideInvoice),
                NewPairingDocument(companion, PairingRole.RideItinerary),
                Score: 130);
            var result = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([new PairingResult([assignment], [], [], [])], [invoice, companion])),
                CancellationToken.None);

            result.Artifacts.Should().HaveCount(2).And.OnlyContain(artifact => artifact.State == ArchiveArtifactState.Committed);
            File.ReadAllText(invoiceSource).Should().Be("real invoice bytes");
            File.ReadAllText(companionSource).Should().Be("real itinerary bytes");
            File.Exists(Path.Combine(outputRoot, result.Artifacts[0].RelativePath!.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
            File.Exists(Path.Combine(outputRoot, result.Artifacts[1].RelativePath!.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();

            await using var queryContext = _fixture.CreateContext();
            var pair = await queryContext.Pairings.AsNoTracking().SingleAsync();
            pair.State.Should().Be("Committed");
            var artifacts = await queryContext.ArchivedArtifacts.AsNoTracking().OrderBy(row => row.DocumentId).ToListAsync();
            artifacts.Should().HaveCount(2).And.OnlyContain(artifact => artifact.State == "Committed");
            var auditSequences = await queryContext.AuditEvents.AsNoTracking()
                .Where(row => row.RunId == runId)
                .OrderBy(row => row.EventSequence)
                .Select(row => row.EventSequence)
                .ToListAsync();
            auditSequences.Should().Equal(1, 2, 3, 4);
        }
        finally
        {
            DeleteSource(invoiceSource);
            DeleteSource(companionSource);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cwt_match_moves_hotel_confirmation_and_updates_persisted_review_and_artifact_state()
    {
        await _fixture.ResetAsync();
        const string runId = "run-real-cwt";
        const string cancellationName = "酒店预定取消知会-张三-20260610入住-上海.pdf";
        const string hotelName = "20260610_住宿确认单_张三酒店.pdf";
        const string cancellationContent = "synthetic CWT cancellation";
        const string hotelContent = "synthetic CWT hotel confirmation";
        var cancellationSource = CreateSource(cancellationName, cancellationContent);
        var hotelSource = CreateSource(hotelName, hotelContent);
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-real-cwt-output-{Guid.NewGuid():N}");
        try
        {
            await SeedRunAndDocumentsAsync(runId, "cwt-cancel", "cwt-hotel");
            await using var context = _fixture.CreateContext();
            var uowFactory = new EfUnitOfWorkFactory(context);
            var fileSystem = new PhysicalArchiveFileSystem();
            var archiveStore = new EfArchiveArtifactStore(context);
            var pairingStore = new EfPairingStore(context);
            var auditStore = new EfAuditStore(context);
            var finalizer = new CwtCancellationFinalizer(
                new ArchiveNamingPolicy(), archiveStore, fileSystem,
                new EfManualReviewItemStore(context), uowFactory, pairingStore, auditStore);
            var coordinator = new ArchiveCommitCoordinator(uowFactory, archiveStore, fileSystem, auditStore);
            var stage = new DocumentArchivingStage(
                new ArchiveNamingPolicy(), coordinator, fileSystem, pairingStore,
                new EfManualReviewItemStore(context), uowFactory, finalizer);
            var cancellation = NewResult("cwt-cancel", cancellationSource, InvoiceDocumentType.AccommodationConfirmation, cancellationContent)
                with
                {
                    Candidate = NewResult("cwt-cancel", cancellationSource, InvoiceDocumentType.AccommodationConfirmation, cancellationContent)
                        .Candidate with { Metadata = new Dictionary<string, string> { ["source_is_cwt"] = "true" } },
                };
            var hotel = NewResult("cwt-hotel", hotelSource, InvoiceDocumentType.AccommodationConfirmation, hotelContent);

            var result = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([], [cancellation, hotel])),
                CancellationToken.None);

            result.Results.Single(item => item.Candidate.DocumentId.Value == "cwt-hotel").Status.Should().Be(CandidateStatus.ManualReview);
            result.Results.Single(item => item.Candidate.DocumentId.Value == "cwt-hotel").Failure!.ReasonCode.Should().Be("CWT_CANCELLATION_MATCH");
            result.Artifacts.Single(item => item.DocumentId == "cwt-hotel").RelativePath.Should().Contain("/review/");
            await using var queryContext = _fixture.CreateContext();
            var hotelArtifact = await queryContext.ArchivedArtifacts.AsNoTracking().SingleAsync(row => row.DocumentId == "cwt-hotel");
            hotelArtifact.State.Should().Be("Committed");
            hotelArtifact.RelativePath.Should().Contain("/review/");
            hotelArtifact.ContentHash.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hotelContent))).ToLowerInvariant());
            var reviewReasons = await queryContext.ManualReviewItems.AsNoTracking()
                .Where(row => row.RunId == runId && row.DocumentId == "cwt-hotel")
                .Select(row => row.ReasonCode)
                .ToListAsync();
            reviewReasons.Should().ContainSingle().Which.Should().Be("CWT_CANCELLATION_MATCH");
            var cwtAudit = await queryContext.AuditEvents.AsNoTracking()
                .Where(row => row.RunId == runId && row.ReasonCode == "CWT_CANCELLATION_MATCH")
                .ToListAsync();
            cwtAudit.Should().ContainSingle();
            var movedPath = Path.Combine(outputRoot, hotelArtifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(movedPath).Should().BeTrue();
            var sidecar = await File.ReadAllTextAsync($"{movedPath}.json");
            sidecar.Should().Contain("cwt-cancel").And.Contain("cwt-hotel").And.NotContain("张三");
        }
        finally
        {
            DeleteSource(cancellationSource);
            DeleteSource(hotelSource);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    private async Task SeedRunAndDocumentsAsync(string runId, params string[] documentIds)
    {
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = runId,
            State = "Running",
            Stage = "archive-documents",
            DateFrom = new DateOnly(2026, 9, 1),
            DateToExclusive = new DateOnly(2026, 10, 1),
            StartedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        foreach (var documentId in documentIds)
        {
            context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = documentId,
                SourceKind = "attachment",
                SourceFileName = $"{documentId}.pdf",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        await context.SaveChangesAsync();

        for (var sequence = 0; sequence < documentIds.Length; sequence++)
        {
            context.DocumentProcessing.Add(new DocumentProcessingRow
            {
                DocumentId = documentIds[sequence],
                ProcessingRevision = 0,
                RunId = runId,
                Sequence = sequence,
                Stage = "pair-artifacts",
                Status = "Resolved",
                Attempt = 1,
                MaxAttempts = 2,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        await context.SaveChangesAsync();
    }

    private static CandidateProcessResult NewResult(string id, string source, InvoiceDocumentType type, string content)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new CandidateProcessResult(
            new DocumentCandidate(DocumentIdentity.Create(id), 0, "corr", "uid", Path.GetFileName(source), "application/pdf", content.Length, 0, "attachment"),
            CandidateStatus.Resolved,
            new InvoiceDocument(id, new DateOnly(2026, 9, 24), "Buyer", "Merchant", 100m, 13m, 113m, "code", id, type, null, null,
                Array.Empty<InvoiceItem>(), Path.GetFileName(source), contentHash),
            source);
    }

    private static PairingDocument NewPairingDocument(CandidateProcessResult result, PairingRole role) => new(
        result.Candidate.DocumentId.Value,
        role,
        result.Invoice!.TotalAmount,
        result.Invoice.InvoiceDate,
        "",
        [],
        result.Candidate.SourceMessageUid,
        result.ArtifactPath);

    private static string CreateSource(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoice-flow-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteSource(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

}
