// Verifies RunEvents sequence uniqueness from design §5:
//   * (RunId, EventSequence) is unique — replaying or re-publishing a
//     sequence must throw
//   * The RunEvents table is for run progress (compactable) and not the
//     AuditEvents append-only ledger

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class RunEventSequenceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public RunEventSequenceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DuplicateRunEventSequence_throws_unique_constraint()
    {
        await _fixture.ResetAsync();
        await using (var context = _fixture.CreateContext())
        {
            await SeedRunAsync(context, "run-1");

            await using (var uow = await BeginAsync(context))
            {
                var store = new EfEventReplayStore(context);
                await store.AppendAsync(
                    new StoredRunEventRecord(
                        "run-1", 1, "run.created", "{}", DateTimeOffset.UtcNow, null),
                    uow,
                    CancellationToken.None);
                await uow.CommitAsync(CancellationToken.None);
            }
        }

        // Use a fresh context for the duplicate so EF's change tracker doesn't
        // complain about identity-map conflicts on the same key value. The
        // unique constraint is enforced by SQLite regardless of the context.
        await using var context2 = _fixture.CreateContext();
        await using var uow2 = await BeginAsync(context2);
        var store2 = new EfEventReplayStore(context2);
        var act = async () =>
        {
            await store2.AppendAsync(
                new StoredRunEventRecord(
                    "run-1", 1, "run.created-dup", "{}", DateTimeOffset.UtcNow, null),
                uow2,
                CancellationToken.None);
            await uow2.CommitAsync(CancellationToken.None);
        };

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ReadSince_returns_events_in_sequence_order()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        await SeedRunAsync(context, "run-2");

        await using (var uow = await BeginAsync(context))
        {
            var store = new EfEventReplayStore(context);
            foreach (var seq in new long[] { 1, 2, 3 })
            {
                await store.AppendAsync(
                    new StoredRunEventRecord(
                        "run-2", seq, $"run.event.{seq}", "{}", DateTimeOffset.UtcNow, null),
                    uow,
                    CancellationToken.None);
            }
            await uow.CommitAsync(CancellationToken.None);
        }

        await using var query = _fixture.CreateContext();
        var reader = new EfEventReplayStore(query);
        var result = await reader.ReadSinceAsync("run-2", afterSequence: 0, limit: 100, CancellationToken.None)
            ;

        result.Events.Select(e => e.EventSequence).Should().Equal(1, 2, 3);
    }

    private static async Task SeedRunAsync(InvoiceFlowDbContext context, string runId)
    {
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
    }

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context)
    {
        var factory = new EfUnitOfWorkFactory(context);
        return await factory.BeginAsync(TransactionPurpose.EventAppend, CancellationToken.None);
    }
}