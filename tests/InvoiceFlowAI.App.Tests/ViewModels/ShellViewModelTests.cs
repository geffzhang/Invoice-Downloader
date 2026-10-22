using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.ViewModels;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task Initialize_activates_the_first_page_in_shell_order()
    {
        var analysis = new FakePage("analysis");
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(analysis, settings, processing);

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        shell.CurrentPage.Should().BeSameAs(settings);
        settings.Activations.Should().Be(1);
        analysis.Activations.Should().Be(0);
    }

    [Fact]
    public async Task Navigation_deactivates_previous_page_and_activates_target()
    {
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(settings, processing);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await shell.NavigateAsync("processing", TestContext.Current.CancellationToken);

        settings.Deactivations.Should().Be(1);
        processing.Activations.Should().Be(1);
        shell.CurrentPage.Should().BeSameAs(processing);
    }

    [Fact]
    public async Task Navigation_to_unknown_or_current_page_is_ignored()
    {
        var settings = new FakePage("settings");
        var shell = CreateShell(settings);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await shell.NavigateAsync("missing", TestContext.Current.CancellationToken);
        await shell.NavigateAsync("settings", TestContext.Current.CancellationToken);

        settings.Activations.Should().Be(1);
        settings.Deactivations.Should().Be(0);
    }

    [Fact]
    public async Task Page_can_navigate_through_injected_navigator()
    {
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(settings, processing);
        var navigator = (AppNavigator)shell.GetType()
            .GetField("_navigator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(shell)!;
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await navigator.NavigateAsync("processing", TestContext.Current.CancellationToken);

        shell.CurrentPage.Should().BeSameAs(processing);
    }

    [Fact]
    public async Task Navigation_from_a_lifecycle_hook_fails_fast_instead_of_deadlocking()
    {
        var navigator = new AppNavigator();
        var settings = new NavigatingPage("settings", navigator, "processing");
        var processing = new FakePage("processing");
        var shell = new ShellViewModel(
            navigator,
            new IPageViewModel[] { settings, processing },
            new FakeDisclaimerService(),
            new FakePathLauncher());

        var initialization = shell.InitializeAsync(TestContext.Current.CancellationToken);

        var exception = await FluentActions
            .Awaiting(() => initialization.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();

        exception.WithMessage("导航不能在页面生命周期回调期间发起。");
    }

    private static ShellViewModel CreateShell(params FakePage[] pages)
        => new(
            new AppNavigator(),
            pages,
            new FakeDisclaimerService(),
            new FakePathLauncher());

    private sealed class FakePage(string key) : IPageViewModel
    {
        public string Key { get; } = key;
        public string Title => Key;
        public string Eyebrow => "Step";
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }

        public Task OnActivatedAsync(CancellationToken cancellationToken)
        {
            Activations++;
            return Task.CompletedTask;
        }

        public Task OnDeactivatedAsync(CancellationToken cancellationToken)
        {
            Deactivations++;
            return Task.CompletedTask;
        }
    }

    private sealed class NavigatingPage(string key, AppNavigator navigator, string target) : IPageViewModel
    {
        public string Key { get; } = key;
        public string Title => Key;
        public string Eyebrow => "Step";

        public Task OnActivatedAsync(CancellationToken cancellationToken)
            => navigator.NavigateAsync(target, CancellationToken.None);

        public Task OnDeactivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDisclaimerService : IDisclaimerService
    {
        public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePathLauncher : IDesktopPathLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
