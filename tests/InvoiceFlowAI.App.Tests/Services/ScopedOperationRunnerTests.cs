using FluentAssertions;
using InvoiceFlowAI.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Services;

public sealed class ScopedOperationRunnerTests
{
    [Fact]
    public async Task Each_operation_gets_its_own_scope()
    {
        var services = new ServiceCollection();
        services.AddScoped<Probe>();
        await using var provider = services.BuildServiceProvider();
        var runner = new ScopedOperationRunner(provider.GetRequiredService<IServiceScopeFactory>());

        var first = await runner.RunAsync(
            (scope, _) => Task.FromResult(scope.GetRequiredService<Probe>().Id),
            TestContext.Current.CancellationToken);
        var second = await runner.RunAsync(
            (scope, _) => Task.FromResult(scope.GetRequiredService<Probe>().Id),
            TestContext.Current.CancellationToken);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Scoped_service_is_disposed_when_operation_completes()
    {
        var probe = new Probe();
        var services = new ServiceCollection();
        services.AddScoped(_ => probe);
        await using var provider = services.BuildServiceProvider();
        var runner = new ScopedOperationRunner(provider.GetRequiredService<IServiceScopeFactory>());

        await runner.RunAsync((scope, _) =>
        {
            scope.GetRequiredService<Probe>();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        probe.Disposed.Should().BeTrue();
    }

    private sealed class Probe : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
