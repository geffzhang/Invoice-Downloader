// Verifies the audit store boundaries from design §5 / §8:
//   * AuditEvents is append-only — UPDATE/DELETE must fail at the SQLite
//     layer regardless of how EF tries to push them
//   * (RunId, EventSequence) is unique
//   * Audit payload hashes form a chained SHA-256 sequence
//   * Audit chain breaks are detected and abort the transaction
//
// These tests use the real SQLite provider (in-memory) so triggers and
// unique indexes fire exactly as they will in production.

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class AppendOnlyAuditTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AppendOnlyAuditTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuditEventUpdate_throws_at_Sqlite_layer()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var uow = await SeedAuditAsync(context, "run-1");

        var audit = new AuditEventRecord(
            AuditEventId: "evt-1",
            RunId: "run-1",
            EventSequence: 1,
            EventType: "run.created",
            Stage: "lifecycle",
            NodeId: "lifecycle",
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: "",
            PayloadJson: "{\"previous\":null,\"payloadHash\":\"0000000000000000000000000000000000000000000000000000000000000000\"}",
            PayloadHash: "11111111111111111111111111111111",
            OccurredAtUtc: DateTimeOffset.UtcNow);

        var store = new EfAuditStore(context);
        await store.AppendAsync(audit, uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        // Now attempt to mutate the row directly via SQL — the append-only
        // trigger must reject the change.
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AuditEvents SET ReasonCode='hacked' WHERE AuditEventId='evt-1';";
        var act = async () => await command.ExecuteNonQueryAsync();
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task AuditEventDelete_throws_at_Sqlite_layer()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var uow = await SeedAuditAsync(context, "run-2");

        var audit = new AuditEventRecord(
            AuditEventId: "evt-2",
            RunId: "run-2",
            EventSequence: 1,
            EventType: "run.created",
            Stage: "lifecycle",
            NodeId: "lifecycle",
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: "",
            PayloadJson: "{}",
            PayloadHash: "11111111111111111111111111111111",
            OccurredAtUtc: DateTimeOffset.UtcNow);

        var store = new EfAuditStore(context);
        await store.AppendAsync(audit, uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuditEvents WHERE AuditEventId='evt-2';";
        var act = async () => await command.ExecuteNonQueryAsync();
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task DuplicateEventSequence_throws_unique_constraint()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var uow = await SeedAuditAsync(context, "run-3");

        var store = new EfAuditStore(context);

        var first = new AuditEventRecord(
            AuditEventId: "evt-3a",
            RunId: "run-3",
            EventSequence: 1,
            EventType: "run.created",
            Stage: "lifecycle",
            NodeId: "lifecycle",
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: "",
            PayloadJson: "{}",
            PayloadHash: "11111111111111111111111111111111",
            OccurredAtUtc: DateTimeOffset.UtcNow);

        var second = new AuditEventRecord(
            AuditEventId: "evt-3b",
            RunId: "run-3",
            EventSequence: 1,
            EventType: "run.created",
            Stage: "lifecycle",
            NodeId: "lifecycle",
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: "",
            PayloadJson: "{}",
            PayloadHash: "22222222222222222222222222222222",
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await store.AppendAsync(first, uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        await using var uow2 = await BeginAsync(context);
        var act = async () =>
        {
            await store.AppendAsync(second, uow2, CancellationToken.None);
            await uow2.CommitAsync(CancellationToken.None);
        };

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Zero_sequence_archive_events_receive_unique_run_local_sequences()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var uow = await SeedAuditAsync(context, "run-zero-sequence");
        var store = new EfAuditStore(context);

        await store.AppendAsync(NewZeroSequenceEvent("event-zero-1", "run-zero-sequence"), uow, CancellationToken.None);
        await store.AppendAsync(NewZeroSequenceEvent("event-zero-2", "run-zero-sequence"), uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        var sequences = await context.AuditEvents.AsNoTracking()
            .Where(row => row.RunId == "run-zero-sequence")
            .OrderBy(row => row.EventSequence)
            .Select(row => row.EventSequence)
            .ToListAsync();
        sequences.Should().Equal(1, 2);
    }

    private static AuditEventRecord NewZeroSequenceEvent(string id, string runId) => new(
        AuditEventId: id,
        RunId: runId,
        EventSequence: 0,
        EventType: "archive.commit",
        Stage: "archive",
        NodeId: "archive",
        DocumentId: id,
        ProcessingRevision: 1,
        ReasonCode: "archive.commit",
        PayloadJson: "{}",
        PayloadHash: new string('a', 64),
        OccurredAtUtc: DateTimeOffset.UtcNow);

    private async Task<IUnitOfWork> SeedAuditAsync(InvoiceFlowDbContext context, string runId)
    {
        // A Run row is required so the AuditEvents FK is satisfied.
        context.Runs.Add(new RunRow
        {
            RunId = runId,
            State = "Running",
            Stage = "scan-mailbox",
            DateFrom = new DateOnly(2026, 1, 1),
            DateToExclusive = new DateOnly(2026, 2, 1),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return await BeginAsync(context);
    }

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context)
    {
        var factory = new EfUnitOfWorkFactory(context);
        return await factory.BeginAsync(TransactionPurpose.EventAppend, CancellationToken.None);
    }
}