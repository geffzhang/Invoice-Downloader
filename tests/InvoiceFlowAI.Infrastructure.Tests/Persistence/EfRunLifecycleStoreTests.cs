using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class EfRunLifecycleStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public EfRunLifecycleStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TryCreate_persists_run_context_and_created_state()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        await using var uow = await new EfUnitOfWorkFactory(context)
            .BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None);

        var created = await store.TryCreateAsync(NewRequest("run-1"), uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        created.Should().BeTrue();
        var persisted = await store.FindAsync("run-1", CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.State.Should().Be(RunLifecycleState.Created);
        persisted.Stage.Should().Be("admitted");
        persisted.CancellationRequestedAtUtc.Should().BeNull();
        var row = await context.Runs.FindAsync("run-1");
        row!.AccountId.Should().Be("account-1");
        row.OutputRoot.Should().Be("C:\\Invoices");
        row.DateToExclusive.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task TryCreate_rejects_a_second_active_run_but_allows_one_after_terminal_state()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        var factory = new EfUnitOfWorkFactory(context);

        await using (var firstUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            (await store.TryCreateAsync(NewRequest("run-1"), firstUow, CancellationToken.None)).Should().BeTrue();
            await firstUow.CommitAsync(CancellationToken.None);
        }

        await using (var secondUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            (await store.TryCreateAsync(NewRequest("run-2"), secondUow, CancellationToken.None)).Should().BeFalse();
            await secondUow.RollbackAsync(CancellationToken.None);
        }

        context.Runs.Single(run => run.RunId == "run-1").State = "Completed";
        await context.SaveChangesAsync(CancellationToken.None);

        await using var thirdUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None);
        (await store.TryCreateAsync(NewRequest("run-3"), thirdUow, CancellationToken.None)).Should().BeTrue();
        await thirdUow.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TryRequestCancellation_is_idempotent_and_rejects_terminal_runs()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        var factory = new EfUnitOfWorkFactory(context);
        await using (var createUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await store.TryCreateAsync(NewRequest("run-1"), createUow, CancellationToken.None);
            await createUow.CommitAsync(CancellationToken.None);
        }

        var firstRequestAt = DateTimeOffset.Parse("2026-09-28T12:01:00Z");
        await using (var firstStopUow = await factory.BeginAsync(TransactionPurpose.RunTransition, CancellationToken.None))
        {
            (await store.TryRequestCancellationAsync("run-1", firstRequestAt, firstStopUow, CancellationToken.None))
                .Should().BeTrue();
            await firstStopUow.CommitAsync(CancellationToken.None);
        }

        await using (var repeatedStopUow = await factory.BeginAsync(TransactionPurpose.RunTransition, CancellationToken.None))
        {
            (await store.TryRequestCancellationAsync("run-1", firstRequestAt.AddMinutes(1), repeatedStopUow, CancellationToken.None))
                .Should().BeFalse();
            await repeatedStopUow.CommitAsync(CancellationToken.None);
        }

        context.Runs.Single().State = "Completed";
        await context.SaveChangesAsync(CancellationToken.None);
        await using var terminalStopUow = await factory.BeginAsync(TransactionPurpose.RunTransition, CancellationToken.None);
        (await store.TryRequestCancellationAsync("run-1", firstRequestAt.AddMinutes(2), terminalStopUow, CancellationToken.None))
            .Should().BeFalse();
        await terminalStopUow.RollbackAsync(CancellationToken.None);

        var persisted = await store.FindAsync("run-1", CancellationToken.None);
        persisted!.CancellationRequestedAtUtc.Should().Be(firstRequestAt);
    }

    [Fact]
    public async Task Lifecycle_transitions_created_to_running_and_then_to_terminal_state()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        var factory = new EfUnitOfWorkFactory(context);
        await using (var createUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await store.TryCreateAsync(NewRequest("run-1"), createUow, CancellationToken.None);
            await createUow.CommitAsync(CancellationToken.None);
        }

        await using (var runningUow = await factory.BeginAsync(TransactionPurpose.RunTransition, CancellationToken.None))
        {
            (await store.TryMarkRunningAsync("run-1", "scan-mailbox", runningUow, CancellationToken.None))
                .Should().BeTrue();
            await runningUow.CommitAsync(CancellationToken.None);
        }

        await using (var terminalUow = await factory.BeginAsync(TransactionPurpose.TerminalCommit, CancellationToken.None))
        {
            await store.UpdateTerminalStateAsync(
                new RunStateSnapshot("run-1", RunLifecycleState.Completed, "lifecycle", "COMPLETED", 1,
                    DateTimeOffset.UtcNow, null),
                terminalUow,
                CancellationToken.None);
            await terminalUow.CommitAsync(CancellationToken.None);
        }

        var persisted = await store.FindAsync("run-1", CancellationToken.None);
        persisted!.State.Should().Be(RunLifecycleState.Completed);
        persisted.Stage.Should().Be("lifecycle");
        persisted.EndedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_creation_and_running_transition_can_commit_in_the_same_unit_of_work()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        await using var uow = await new EfUnitOfWorkFactory(context)
            .BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None);

        (await store.TryCreateAsync(NewRequest("run-same-uow"), uow, CancellationToken.None)).Should().BeTrue();
        (await store.TryMarkRunningAsync("run-same-uow", "initializing", uow, CancellationToken.None))
            .Should().BeTrue();
        await uow.CommitAsync(CancellationToken.None);

        var persisted = await store.FindAsync("run-same-uow", CancellationToken.None);
        persisted!.State.Should().Be(RunLifecycleState.Running);
        persisted.Stage.Should().Be("initializing");
    }

    [Fact]
    public async Task Terminal_summary_counts_and_failure_reasons_round_trip_through_run_store()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        var factory = new EfUnitOfWorkFactory(context);
        await using (var createUow = await factory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await store.TryCreateAsync(NewRequest("run-summary"), createUow, CancellationToken.None);
            await createUow.CommitAsync(CancellationToken.None);
        }

        var summary = new RunSummary(
            "run-summary", RunTerminalStatus.PartialSuccess, "RUN_PARTIAL_SUCCESS",
            2, 1, 3, 4, 5, 6, 7, 8, 9,
            new Dictionary<string, int> { ["EXTRACTION_FAILED"] = 2, ["ARCHIVE_REJECTED"] = 1 },
            null, null, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), false, Array.Empty<RunFailure>());
        await using (var terminalUow = await factory.BeginAsync(TransactionPurpose.TerminalCommit, CancellationToken.None))
        {
            await store.UpdateTerminalStateAsync(
                new RunStateSnapshot("run-summary", RunLifecycleState.PartialSuccess, "lifecycle",
                    summary.TerminalReasonCode, 1, summary.CompletedAtUtc, null, summary),
                terminalUow,
                CancellationToken.None);
            await terminalUow.CommitAsync(CancellationToken.None);
        }

        var persisted = await store.FindAsync("run-summary", CancellationToken.None);
        persisted!.Summary.Should().BeEquivalentTo(summary);
        context.Runs.Single().SummaryJson.Should().NotBeNullOrWhiteSpace();
    }

    private static RunCreationRequest NewRequest(string runId) => new(
        RunId: runId,
        DateFrom: new DateOnly(2026, 9, 1),
        DateToExclusive: new DateOnly(2026, 10, 1),
        AccountId: "account-1",
        AccountRevision: 3,
        Mailbox: "INBOX",
        OutputRoot: "C:\\Invoices",
        SettingsRevision: 7,
        RuleSetId: "default",
        RuleSetVersion: 1,
        ConfigurationFingerprint: "fingerprint",
        RecipeVersion: "1.0.0",
        StartedAtUtc: DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
}