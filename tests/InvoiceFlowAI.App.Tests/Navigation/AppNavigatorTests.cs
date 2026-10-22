using FluentAssertions;
using InvoiceFlowAI.App.Navigation;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Navigation;

public sealed class AppNavigatorTests
{
    [Fact]
    public async Task Navigate_before_attach_is_a_no_op()
    {
        var navigator = new AppNavigator();
        await navigator.NavigateAsync("settings", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Navigate_forwards_to_attached_handler()
    {
        var navigator = new AppNavigator();
        var calls = new List<string>();
        navigator.Attach((key, _) =>
        {
            calls.Add(key);
            return Task.CompletedTask;
        });

        await navigator.NavigateAsync("processing", TestContext.Current.CancellationToken);

        calls.Should().Equal("processing");
    }
}
