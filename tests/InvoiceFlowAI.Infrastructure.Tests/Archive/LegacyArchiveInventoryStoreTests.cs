using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class LegacyArchiveInventoryStoreTests : IClassFixture<InvoiceFlowAI.Infrastructure.Tests.Persistence.SqliteTestFixture>
{
    private readonly InvoiceFlowAI.Infrastructure.Tests.Persistence.SqliteTestFixture _fixture;

    public LegacyArchiveInventoryStoreTests(InvoiceFlowAI.Infrastructure.Tests.Persistence.SqliteTestFixture fixture)
        => _fixture = fixture;

    [Fact]
    public void Legacy_inventory_has_a_durable_record_without_a_document_or_artifact_identity()
    {
        using var context = _fixture.CreateContext();
        var entity = context.Model.FindEntityType("InvoiceFlowAI.Infrastructure.Persistence.Entities.LegacyArchiveInventoryRow");

        entity.Should().NotBeNull();
        var mappedEntity = entity!;
        mappedEntity.GetTableName().Should().Be("LegacyArchiveInventory");
        mappedEntity.FindProperty("InventoryId").Should().NotBeNull();
        mappedEntity.FindProperty("RootKey").Should().NotBeNull();
        mappedEntity.FindProperty("OriginalRelativePath").Should().NotBeNull();
        mappedEntity.FindProperty("CurrentRelativePath").Should().NotBeNull();
        mappedEntity.FindProperty("SourceFileName").Should().NotBeNull();
        mappedEntity.FindProperty("ContentHash").Should().NotBeNull();
        mappedEntity.FindProperty("State").Should().NotBeNull();
        context.Model.FindEntityType(typeof(InvoiceFlowAI.Infrastructure.Persistence.Entities.DocumentSourceRow))
            .Should().NotBeNull();
        context.Model.FindEntityType(typeof(InvoiceFlowAI.Infrastructure.Persistence.Entities.ArchivedArtifactRow))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Discover_is_idempotent_across_relocation_and_keeps_same_name_paths_distinct()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfLegacyArchiveInventoryStore(context);
        var uowFactory = new EfUnitOfWorkFactory(context);

        var contentHash = new string('a', 64);
        var first = await DiscoverAsync(store, uowFactory, "住宿发票/first/hotel.pdf", "hotel.pdf", contentHash);
        var repeated = await DiscoverAsync(store, uowFactory, "住宿发票/first/hotel.pdf", "hotel.pdf", contentHash);
        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None))
        {
            await store.UpdateLocationAsync(
                first.InventoryId,
                "人工复核/hotel.pdf",
                LegacyArchiveInventoryState.Review,
                "review-run",
                transaction,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var sameNameOtherPath = await DiscoverAsync(store, uowFactory, "住宿发票/second/hotel.pdf", "hotel.pdf", contentHash);
        var inventory = await store.ListByRootAsync("root-key", CancellationToken.None);

        repeated.InventoryId.Should().Be(first.InventoryId);
        sameNameOtherPath.InventoryId.Should().NotBe(first.InventoryId);
        inventory.Should().HaveCount(2);
        inventory.Single(item => item.InventoryId == first.InventoryId).CurrentRelativePath.Should().Be("人工复核/hotel.pdf");
        inventory.Single(item => item.InventoryId == first.InventoryId).State.Should().Be(LegacyArchiveInventoryState.Review);
        (await context.Documents.CountAsync()).Should().Be(0);
        (await context.ArchivedArtifacts.CountAsync()).Should().Be(0);
    }

    private static async Task<LegacyArchiveInventorySnapshot> DiscoverAsync(
        ILegacyArchiveInventoryStore store,
        EfUnitOfWorkFactory uowFactory,
        string relativePath,
        string fileName,
        string hash)
    {
        await using var transaction = await uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None);
        var snapshot = await store.DiscoverAsync(
            "root-key", relativePath, fileName, hash, transaction, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return snapshot;
    }
}