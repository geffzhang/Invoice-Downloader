using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class PairingStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public PairingStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_is_idempotent_and_serializes_companions_in_document_order()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        await SeedRunAndDocumentsAsync(context, "run-1", "invoice-1", "companion-b", "companion-a");
        var store = new EfPairingStore(context);

        await using (var transaction = await BeginAsync(context))
        {
            await store.UpsertAsync(NewPairing("run-1", "invoice-1", "Prepared", [new("companion-b", 2), new("companion-a", 1)]), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        await using (var transaction = await BeginAsync(context))
        {
            await store.UpsertAsync(NewPairing("run-1", "invoice-1", "Prepared", [new("companion-a", 1), new("companion-b", 2)]), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var rows = await context.Pairings.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].CompanionDocumentIdsJson.Should().Be("[\"companion-a\",\"companion-b\"]");
        rows[0].CompanionProcessingRevisionsJson.Should().Be("[1,2]");
        rows[0].Score.Should().Be("130");
    }

    [Fact]
    public async Task Upsert_updates_pair_state_through_commit_and_recovery_required()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        await SeedRunAndDocumentsAsync(context, "run-2", "invoice-2", "companion-2");
        var store = new EfPairingStore(context);

        foreach (var state in new[] { "Prepared", "Committed", "RecoveryRequired" })
        {
            await using var transaction = await BeginAsync(context);
            await store.UpsertAsync(NewPairing("run-2", "invoice-2", state, [new("companion-2", 0)]), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            (await context.Pairings.AsNoTracking().SingleAsync()).State.Should().Be(state);
        }

        (await context.Pairings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_keeps_partial_pair_recoverable_until_every_member_is_committed()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        await SeedRunAndDocumentsAsync(context, "run-reconcile", "invoice-r", "companion-r");
        var store = new EfPairingStore(context);
        await using (var transaction = await BeginAsync(context))
        {
            await store.UpsertAsync(new PairingRecord(
                "run-reconcile", "invoice-r", 0, [new("companion-r", 0)], 110, "Prepared", ""), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var invoiceArtifact = NewSnapshot("artifact-invoice", "invoice-r", ArchiveArtifactState.Committed);
        var companionPrepared = NewSnapshot("artifact-companion", "companion-r", ArchiveArtifactState.Prepared);
        await using (var transaction = await BeginAsync(context))
        {
            await store.ReconcileArchiveStateAsync("run-reconcile", [invoiceArtifact, companionPrepared], transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        var partial = await context.Pairings.AsNoTracking().SingleAsync();
        partial.State.Should().Be("RecoveryRequired");
        partial.ReasonCode.Should().Be("PAIR_ARCHIVE_MEMBER_PREPARED");

        await using (var transaction = await BeginAsync(context))
        {
            await store.ReconcileArchiveStateAsync("run-reconcile", [invoiceArtifact, companionPrepared with { State = ArchiveArtifactState.Committed }], transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        var completed = await context.Pairings.AsNoTracking().SingleAsync();
        completed.State.Should().Be("Committed");
        completed.ReasonCode.Should().BeEmpty();
    }

    private static PairingRecord NewPairing(string runId, string invoiceDocumentId, string state, IReadOnlyList<PairingCompanionRecord> companions) => new(
        RunId: runId,
        InvoiceDocumentId: invoiceDocumentId,
        InvoiceProcessingRevision: 0,
        Companions: companions,
        TotalScore: 130,
        State: state,
        ReasonCode: "");

    private static async Task SeedRunAndDocumentsAsync(InvoiceFlowDbContext context, string runId, params string[] documentIds)
    {
        context.Runs.Add(new RunRow
        {
            RunId = runId,
            State = "Running",
            Stage = "pairing",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
        foreach (var documentId in documentIds)
        {
            context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = documentId,
                SourceKind = "attachment",
                SourceFileName = $"{documentId}.pdf",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
        await context.SaveChangesAsync();
        var sequence = 0L;
        foreach (var documentId in documentIds)
        {
            context.DocumentProcessing.Add(new DocumentProcessingRow
            {
                DocumentId = documentId,
                ProcessingRevision = documentId.StartsWith("companion-b", StringComparison.Ordinal) ? 2 : documentId == "companion-a" ? 1 : 0,
                RunId = runId,
                Sequence = sequence++,
                Stage = "pairing",
                Status = "Resolved",
                UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
        await context.SaveChangesAsync();
    }

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context) =>
        await new EfUnitOfWorkFactory(context).BeginAsync(TransactionPurpose.PairingCommit, CancellationToken.None);

    private static ArchiveArtifactSnapshot NewSnapshot(string artifactId, string documentId, ArchiveArtifactState state) => new(
        artifactId,
        new ArchiveArtifactKey("run-reconcile", documentId, 0, "paired", new string('a', 64)),
        "temp.bin",
        $"archive/{documentId}.bin",
        $"{documentId}.bin",
        new string('a', 64),
        state,
        DateTimeOffset.UnixEpoch,
        state == ArchiveArtifactState.Committed ? DateTimeOffset.UnixEpoch : null,
        $"output/archive/{documentId}.bin");
}
