using FluentAssertions;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryServiceRegistrationTests
{
    [Fact]
    public void Resolves_parent_worker_client_without_dependency_cycle()
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowInfrastructure();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IUrlRecoveryClient>().Should().BeOfType<UrlRecoveryWorkerClient>();
    }

    [Fact]
    public void Worker_composition_resolves_registry_directly_without_spawning_child_worker()
    {
        var services = new ServiceCollection();
        services.AddUrlRecoveryStrategies();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IUrlRecoveryClient>().Should().BeOfType<UrlRecoveryStrategyRegistry>();
    }

    [Fact]
    public void Production_strategy_registration_keeps_browser_fallback_disabled()
    {
        var services = new ServiceCollection();
        services.AddUrlRecoveryStrategies();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IUrlRecoveryStrategy>().Select(strategy => strategy.GetType()).Should().BeEquivalentTo(
        [
            typeof(GenericUrlRecoveryStrategy),
            typeof(NuonuoScanRecoveryStrategy),
            typeof(DirectInvoiceRecoveryStrategy),
            typeof(BaiwangRecoveryStrategy),
        ]);
    }
}