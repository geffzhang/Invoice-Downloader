using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class CwtArchiveInventoryTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public CwtArchiveInventoryTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Lists_prior_run_accommodation_artifact_with_source_identity_and_ignores_other_types()
    {
        await _fixture.ResetAsync();
        var outputRoot = CreateRoot();
        try
        {
            var hotelPath = Path.Combine(outputRoot, "archive", "prior-run", "generated-hotel.pdf");
            var otherPath = Path.Combine(outputRoot, "archive", "prior-run", "other.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(hotelPath)!);
            await File.WriteAllTextAsync(hotelPath, "prior hotel bytes");
            await File.WriteAllTextAsync(otherPath, "other document bytes");
            await SeedArtifactAsync(outputRoot, "prior-hotel-run", "hotel-doc", "invoice-hotel",
                "20260610_住宿确认单_张三酒店.pdf", hotelPath, "generated-hotel.pdf", null,
                InvoiceDocumentType.AccommodationConfirmation);
            await SeedArtifactAsync(outputRoot, "prior-other-run", "other-doc", "invoice-other",
                "other.pdf", otherPath, "other.pdf", "other-source.pdf", InvoiceDocumentType.Other);

            await using var context = _fixture.CreateContext();
            var inventory = CreateInventory(context);
            var items = await inventory.ListAsync(outputRoot, CancellationToken.None);

            items.Should().ContainSingle(item => item.DocumentId == "hotel-doc")
                .Which.Should().BeEquivalentTo(new
                {
                    ArtifactId = "artifact-hotel-doc",
                    DocumentId = "hotel-doc",
                    ProcessingRevision = 0,
                    SourceFileName = "20260610_住宿确认单_张三酒店.pdf",
                    RelativePath = Path.GetRelativePath(outputRoot, hotelPath).Replace('\\', '/'),
                    AbsolutePath = Path.GetFullPath(hotelPath),
                    ContentHash = Hash("prior hotel bytes"),
                    Kind = CwtArchiveInventoryItemKind.ArchivedArtifact,
                });
            items.Should().NotContain(item => item.DocumentId == "other-doc");
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Excludes_committed_accommodation_artifacts_already_in_review()
    {
        await _fixture.ResetAsync();
        var outputRoot = CreateRoot();
        var reviewedPath = Path.Combine(outputRoot, "archive", "prior-run", "review", "hotel.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(reviewedPath)!);
        await File.WriteAllTextAsync(reviewedPath, "already reviewed hotel bytes");
        try
        {
            await SeedArtifactAsync(outputRoot, "prior-run", "reviewed-hotel", "reviewed-invoice",
                "20260610_住宿确认单_张三酒店.pdf", reviewedPath, "hotel.pdf", null,
                InvoiceDocumentType.AccommodationConfirmation);
            await using var context = _fixture.CreateContext();
            var inventory = CreateInventory(context);

            var items = await inventory.ListAsync(outputRoot, CancellationToken.None);

            items.Should().NotContain(item => item.DocumentId == "reviewed-hotel");
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Lists_matching_direct_legacy_file_once_without_fabricating_document_or_artifact_rows()
    {
        await _fixture.ResetAsync();
        var outputRoot = CreateRoot();
        try
        {
            var accommodationDirectory = Path.Combine(outputRoot, "住宿发票");
            var nestedDirectory = Path.Combine(accommodationDirectory, "nested");
            Directory.CreateDirectory(nestedDirectory);
            var legacyPath = Path.Combine(accommodationDirectory, "20260610_住宿确认单_张三酒店.pdf");
            await File.WriteAllTextAsync(legacyPath, "unregistered hotel bytes");
            await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "nested_张三酒店.pdf"), "nested bytes");

            await using var context = _fixture.CreateContext();
            var inventory = CreateInventory(context);
            var items = await inventory.ListAsync(outputRoot, CancellationToken.None);
            var persisted = await context.LegacyArchiveInventory.AsNoTracking().ToListAsync();

            items.Should().ContainSingle(item => item.SourceFileName.Contains("张三酒店", StringComparison.Ordinal))
                .Which.Should().BeEquivalentTo(new
                {
                    ArtifactId = (string?)null,
                    DocumentId = (string?)null,
                    ProcessingRevision = (int?)null,
                    SourceFileName = "20260610_住宿确认单_张三酒店.pdf",
                    RelativePath = "住宿发票/20260610_住宿确认单_张三酒店.pdf",
                    AbsolutePath = Path.GetFullPath(legacyPath),
                    ContentHash = Hash("unregistered hotel bytes"),
                    Kind = CwtArchiveInventoryItemKind.LegacyFile,
                });
            items.Should().NotContain(item => item.SourceFileName.StartsWith("nested_", StringComparison.Ordinal));
            persisted.Should().ContainSingle().Which.SourceFileName.Should().Be("20260610_住宿确认单_张三酒店.pdf");
            (await context.Documents.CountAsync()).Should().Be(0);
            (await context.ArchivedArtifacts.CountAsync()).Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Replaced_direct_file_is_represented_by_its_current_content_identity_only()
    {
        await _fixture.ResetAsync();
        var outputRoot = CreateRoot();
        var filePath = Path.Combine(outputRoot, "住宿发票", "20260610_住宿确认单_张三酒店.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "first version");
        try
        {
            await using var context = _fixture.CreateContext();
            var inventory = CreateInventory(context);
            var first = await inventory.ListAsync(outputRoot, CancellationToken.None);
            await File.WriteAllTextAsync(filePath, "replacement version");

            var current = await inventory.ListAsync(outputRoot, CancellationToken.None);
            var persisted = await context.LegacyArchiveInventory.AsNoTracking().ToListAsync();

            first.Should().ContainSingle();
            current.Should().ContainSingle().Which.ContentHash.Should().Be(Hash("replacement version"));
            current.Single().InventoryId.Should().NotBe(first.Single().InventoryId);
            persisted.Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Finalizer_moves_matching_unregistered_legacy_file_without_a_confirmation_candidate()
    {
        await _fixture.ResetAsync();
        var outputRoot = CreateRoot();
        const string sourceName = "20260610_住宿确认单_张三酒店.pdf";
        var sourcePath = Path.Combine(outputRoot, "住宿发票", sourceName);
        var priorArtifactPath = Path.Combine(outputRoot, "archive", "prior-run", "generated-hotel.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(priorArtifactPath)!);
        await File.WriteAllTextAsync(sourcePath, "unregistered hotel bytes");
        await File.WriteAllTextAsync(priorArtifactPath, "prior run hotel bytes");
        try
        {
            await SeedArtifactAsync(outputRoot, "prior-cwt-run", "prior-hotel-doc", "prior-hotel-invoice",
                sourceName, priorArtifactPath, "generated-hotel.pdf", sourceName,
                InvoiceDocumentType.AccommodationConfirmation);
            await using var context = _fixture.CreateContext();
            context.Runs.Add(new RunRow
            {
                RunId = "current-cwt-run",
                State = "Running",
                Stage = "archive-documents",
                DateFrom = new DateOnly(2026, 6, 1),
                DateToExclusive = new DateOnly(2026, 7, 1),
                OutputRoot = outputRoot,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            var fileSystem = new PhysicalArchiveFileSystem();
            var artifactStore = new EfArchiveArtifactStore(context);
            var legacyStore = new EfLegacyArchiveInventoryStore(context);
            var unitOfWorkFactory = new EfUnitOfWorkFactory(context);
            var finalizer = new CwtCancellationFinalizer(
                new ArchiveNamingPolicy(),
                artifactStore,
                fileSystem,
                new EfManualReviewItemStore(context),
                unitOfWorkFactory,
                new EfPairingStore(context),
                new EfAuditStore(context),
                legacyStore,
                new CwtArchiveInventory(
                    artifactStore,
                    legacyStore,
                    fileSystem,
                    unitOfWorkFactory));

            var result = await finalizer.FinalizeAsync(
                "current-cwt-run",
                outputRoot,
                [CancellationCandidate("cancel-doc", "酒店预定取消知会-张三-20260610入住-上海.pdf")],
                CancellationToken.None);

            result.Matches.Should().HaveCount(2).And.Contain("prior-hotel-doc");
            result.UpdatedRelativePaths.Should().HaveCount(2).And.ContainKey("prior-hotel-doc");
            File.Exists(sourcePath).Should().BeFalse();
            File.Exists(priorArtifactPath).Should().BeFalse();
            var legacyTarget = Path.Combine(outputRoot,
                result.UpdatedRelativePaths.Single(entry => entry.Key != "prior-hotel-doc").Value.Replace('/', Path.DirectorySeparatorChar));
            var artifactTarget = Path.Combine(outputRoot,
                result.UpdatedRelativePaths["prior-hotel-doc"].Replace('/', Path.DirectorySeparatorChar));
            File.Exists(legacyTarget).Should().BeTrue();
            File.Exists(artifactTarget).Should().BeTrue();
            (await File.ReadAllTextAsync(legacyTarget)).Should().Be("unregistered hotel bytes");
            (await File.ReadAllTextAsync(artifactTarget)).Should().Be("prior run hotel bytes");
            var inventoryRow = await context.LegacyArchiveInventory.AsNoTracking().SingleAsync();
            inventoryRow.InventoryId.Should().Be(result.Matches.Single(match => match != "prior-hotel-doc"));
            inventoryRow.State.Should().Be(nameof(LegacyArchiveInventoryState.Review));
            inventoryRow.CurrentRelativePath.Should().Be(result.UpdatedRelativePaths[inventoryRow.InventoryId]);
            var sidecar = await File.ReadAllTextAsync($"{legacyTarget}.json");
            sidecar.Should().Contain(inventoryRow.InventoryId).And.NotContain("张三").And.NotContain(outputRoot);
            var artifactRow = await context.ArchivedArtifacts.AsNoTracking().SingleAsync(row => row.ArtifactId == "artifact-prior-hotel-doc");
            artifactRow.RelativePath.Should().Be(result.UpdatedRelativePaths["prior-hotel-doc"]);
            var reviewRow = await context.ManualReviewItems.AsNoTracking().SingleAsync();
            reviewRow.RunId.Should().Be("prior-cwt-run");
            reviewRow.DocumentId.Should().Be("prior-hotel-doc");
            (await context.Documents.CountAsync()).Should().Be(1);
            (await context.ArchivedArtifacts.CountAsync()).Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static CwtArchiveInventory CreateInventory(InvoiceFlowDbContext context)
        => new(
            new EfArchiveArtifactStore(context),
            new EfLegacyArchiveInventoryStore(context),
            new PhysicalArchiveFileSystem(),
            new EfUnitOfWorkFactory(context));

    private async Task SeedArtifactAsync(
        string outputRoot,
        string runId,
        string documentId,
        string invoiceId,
        string documentSourceName,
        string finalPath,
        string archiveName,
        string? artifactSourceName,
        InvoiceDocumentType documentType)
    {
        await using var context = _fixture.CreateContext();
        if (!await context.Runs.AnyAsync(row => row.RunId == runId))
        {
            context.Runs.Add(new RunRow
            {
                RunId = runId,
                State = "Completed",
                Stage = "complete",
                DateFrom = new DateOnly(2026, 6, 1),
                DateToExclusive = new DateOnly(2026, 7, 1),
                OutputRoot = outputRoot,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = documentId,
                SourceKind = "attachment",
                SourceFileName = documentSourceName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            context.DocumentProcessing.Add(new DocumentProcessingRow
            {
                DocumentId = documentId,
                ProcessingRevision = 0,
                RunId = runId,
                Sequence = 1,
                Stage = "archive-documents",
                Status = "Archived",
                Attempt = 1,
                MaxAttempts = 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            context.Invoices.Add(new InvoiceRow
            {
                InvoiceId = invoiceId,
                DocumentId = documentId,
                ProcessingRevision = 0,
                InvoiceDate = new DateOnly(2026, 6, 10),
                Purchaser = "Example Co",
                Seller = "Hotel Co",
                Amount = "100",
                TaxAmount = "0",
                TotalAmount = "100",
                DocumentType = documentType.ToString(),
                Confidence = "1",
                ArchiveState = "Committed",
            });
            await context.SaveChangesAsync();
            context.ArchivedArtifacts.Add(new ArchivedArtifactRow
            {
                ArtifactId = $"artifact-{documentId}",
                RunId = runId,
                DocumentId = documentId,
                ProcessingRevision = 0,
                Role = "standalone",
                RelativePath = Path.GetRelativePath(outputRoot, finalPath).Replace('\\', '/'),
                FinalFilePath = Path.GetFullPath(finalPath),
                FileName = archiveName,
                SourceFileName = artifactSourceName,
                ContentHash = Hash(await File.ReadAllTextAsync(finalPath)),
                State = "Committed",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CommittedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoice-flow-cwt-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CandidateProcessResult CancellationCandidate(string documentId, string fileName)
    {
        var document = new InvoiceDocument(documentId, new DateOnly(2026, 6, 10), "buyer", "seller", 0m, 0m, 0m,
            null, null, InvoiceDocumentType.AccommodationConfirmation, null, null, [], fileName, string.Empty);
        return new CandidateProcessResult(
            new DocumentCandidate(DocumentIdentity.Create(documentId), 0, "corr", "uid", fileName,
                "application/pdf", 1, 1, "mime_attachment",
                Metadata: new Dictionary<string, string> { ["source_is_cwt"] = "true" }),
            CandidateStatus.Resolved, document, string.Empty);
    }
}