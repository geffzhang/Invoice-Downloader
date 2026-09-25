using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class ScopedDesktopRunExecutorLeaseFactoryTests
{
    [Fact]
    public async Task Lease_owns_scoped_executor_until_async_disposal()
    {
        var tracker = new DisposalTracker();
        var services = new ServiceCollection();
        services.AddScoped<IDesktopRunExecutor>(_ => new TrackedExecutor(tracker));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var factory = new ScopedDesktopRunExecutorLeaseFactory(provider.GetRequiredService<IServiceScopeFactory>());

        var lease = await factory.CreateAsync();
        lease.Executor.Should().BeOfType<TrackedExecutor>();
        tracker.DisposeCalls.Should().Be(0);

        await lease.DisposeAsync();

        tracker.DisposeCalls.Should().Be(1);
    }

    private sealed class DisposalTracker
    {
        public int DisposeCalls { get; set; }
    }

    private sealed class TrackedExecutor(DisposalTracker tracker) : IDesktopRunExecutor, IDisposable
    {
        public Task ExecuteAsync(RunStartRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => tracker.DisposeCalls++;
    }
}
