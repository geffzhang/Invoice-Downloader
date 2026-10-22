using FluentAssertions;
using InvoiceFlowAI.App;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Services;

public sealed class PageServiceAliasTests
{
    [Fact]
    public void Account_and_results_aliases_resolve_to_scoped_implementations()
    {
        using var provider = AppServiceProviderFactory.Create();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAccountApplicationService>()
            .Should().BeOfType<AccountApplicationService>();
        scope.ServiceProvider.GetRequiredService<IAccountTestService>()
            .Should().BeOfType<AccountTestService>();
        scope.ServiceProvider.GetRequiredService<IRunResultsQueryService>()
            .Should().BeOfType<RunResultsQueryService>();
        scope.ServiceProvider.GetRequiredService<IScopedOperationRunner>()
            .Should().BeOfType<ScopedOperationRunner>();
    }
}
