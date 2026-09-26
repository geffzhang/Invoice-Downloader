// Smoke test ensuring xUnit discovers the test project. Real Domain tests land in
// Task 2 of the migration implementation plan alongside the Domain records they cover.

using Xunit;

namespace InvoiceFlowAI.Domain.Tests;

public sealed class DomainScaffoldSmokeTests
{
    [Fact]
    public void Domain_assembly_is_loadable_and_carries_marker()
    {
        var marker = typeof(global::InvoiceFlowAI.Domain.DomainPlaceholder).Assembly
            .GetType("InvoiceFlowAI.Domain.DomainPlaceholder");
        Assert.NotNull(marker);
    }
}
