using FluentAssertions;
using InvoiceFlowAI.Application.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class ActiveRunRegistryTests
{
    [Fact]
    public async Task Holds_the_single_run_slot_until_execution_and_finalizers_finish()
    {
        var registry = new ActiveRunRegistry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFinalizers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.TryStart("run-1", async _ =>
        {
            entered.SetResult();
            await finishFinalizers.Task;
        }).Should().BeTrue();

        await entered.Task;
        registry.TryStart("run-2", _ => Task.CompletedTask).Should().BeFalse();
        registry.ActiveRunId.Should().Be("run-1");

        finishFinalizers.SetResult();
        await registry.WaitForIdleAsync(CancellationToken.None);

        registry.TryStart("run-2", _ => Task.CompletedTask).Should().BeTrue();
        await registry.WaitForIdleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Repeated_stop_requests_cancel_once_and_preserve_run_ownership_until_exit()
    {
        var registry = new ActiveRunRegistry();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFinalizers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.TryStart("run-1", async cancellationToken =>
        {
            entered.SetResult(cancellationToken);
            await finishFinalizers.Task;
        }).Should().BeTrue();

        var runToken = await entered.Task;
        registry.RequestCancellation("run-1").Should().BeTrue();
        registry.RequestCancellation("run-1").Should().BeFalse();
        runToken.IsCancellationRequested.Should().BeTrue();
        registry.ActiveRunId.Should().Be("run-1");

        finishFinalizers.SetResult();
        await registry.WaitForIdleAsync(CancellationToken.None);
        registry.ActiveRunId.Should().BeNull();
    }

    [Fact]
    public void Cancellation_for_a_non_active_run_is_not_accepted()
    {
        var registry = new ActiveRunRegistry();

        registry.RequestCancellation("missing-run").Should().BeFalse();
        registry.ActiveRunId.Should().BeNull();
    }
}