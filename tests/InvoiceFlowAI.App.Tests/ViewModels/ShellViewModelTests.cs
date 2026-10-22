using Avalonia.Threading;
using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.Headless;
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

    [Fact]
    public void Deferred_navigation_posted_during_activation_is_queued_not_rejected()
    {
        // 决策探针(记录实际行为):激活回调持有导航锁时,把一次独立点击投递到 UI 线程。
        // 实际行为与最终评审的期望不同:Avalonia 的 SynchronizationContext 以 Normal 优先级
        // 投递 Task.Yield 续延,高于 Dispatcher.UIThread.Post 的默认(Default)优先级,
        // 因此激活回调先完成、生命周期作用域先失效并释放导航锁;投递的点击随后才执行,
        // 正常完成导航 → 点击被排队而不是被拒绝。这是接受的取舍:不会死锁,也不会丢点击。
        var result = AtomUiHeadlessHost.Run(() =>
        {
            var navigator = new AppNavigator();
            var processing = new FakePage("processing");
            var settings = new PostedNavigationPage("settings", navigator, "processing");
            var shell = new ShellViewModel(
                navigator,
                new IPageViewModel[] { settings, processing },
                new FakeDisclaimerService(),
                new FakePathLauncher());

            var initialization = shell.InitializeAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!initialization.IsCompleted || !settings.PostedOutcome.Task.IsCompleted)
                   && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
            }

            return new DeferredNavigationResult(
                initialization.IsCompletedSuccessfully,
                settings.PostedOutcome.Task.IsCompleted ? settings.PostedOutcome.Task.Result : null,
                settings.Logged,
                settings.Deactivations,
                shell.CurrentPage?.Key);
        });

        result.InitializationCompleted.Should().BeTrue();
        result.PostedError.Should().BeNull("the navigation must be deferred, not rejected");
        result.Logged.Should().BeFalse();
        result.SettingsDeactivations.Should().Be(1);
        result.CurrentPageKey.Should().Be("processing");
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

    private sealed class PostedNavigationPage : IPageViewModel
    {
        private readonly AppNavigator _navigator;
        private readonly string _target;

        public PostedNavigationPage(string key, AppNavigator navigator, string target)
        {
            Key = key;
            _navigator = navigator;
            _target = target;
        }

        public string Key { get; }
        public string Title => Key;
        public string Eyebrow => "Step";
        public TaskCompletionSource<Exception?> PostedOutcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Logged { get; private set; }
        public int Deactivations { get; private set; }

        public Task OnActivatedAsync(CancellationToken cancellationToken)
        {
            // 模拟激活尚未结束时用户点击侧边栏:点击投递到 UI 线程,
            // 等回调让出线程后才会执行。
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await _navigator.NavigateAsync(_target, CancellationToken.None);
                    PostedOutcome.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    // 与 MainWindow.OnNavigationNodeSelected 的事件处理器一致:
                    // 捕获并记录,而不是让异常逃逸到 UI 线程。
                    Logged = true;
                    PostedOutcome.TrySetResult(exception);
                }
            });
            return YieldAsync();
        }

        public Task OnDeactivatedAsync(CancellationToken cancellationToken)
        {
            Deactivations++;
            return Task.CompletedTask;
        }

        private static async Task YieldAsync() => await Task.Yield();
    }

    private sealed record DeferredNavigationResult(
        bool InitializationCompleted,
        Exception? PostedError,
        bool Logged,
        int SettingsDeactivations,
        string? CurrentPageKey);

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
