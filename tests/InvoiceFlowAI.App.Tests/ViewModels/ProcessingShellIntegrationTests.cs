using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

/// <summary>
/// 处理页在真实外壳下的导航集成测试:页面生命周期回调持有导航锁,
/// 轮询循环在回调返回后必须仍能自动跳转到结果页。
/// </summary>
public sealed class ProcessingShellIntegrationTests
{
    [Fact]
    public async Task Terminal_run_navigates_to_analysis_through_the_shell_guard()
    {
        var runs = new YieldingRunService();
        var operations = TestScopedOperationRunner.Create(services =>
            services.AddSingleton<IDesktopRunService>(runs));
        var navigator = new AppNavigator();
        var processing = new ProcessingViewModel(
            operations,
            new FakeRunSession(),
            navigator,
            new NullNotificationService(),
            new AppEventBus(NullLogger<AppEventBus>.Instance),
            new SynchronousUiDispatcher(),
            ProcessingOptions.Fast,
            NullLogger<ProcessingViewModel>.Instance);
        var analysis = new CompletionPage("analysis");
        var shell = new ShellViewModel(
            navigator,
            new IPageViewModel[] { new StubPage("settings"), processing, analysis },
            new NullDisclaimerService(),
            new NullPathLauncher());

        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.NavigateAsync("processing", TestContext.Current.CancellationToken);
        await analysis.Activated.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        shell.CurrentPage.Should().NotBeNull();
        shell.CurrentPage!.Key.Should().Be("analysis");
        processing.PageError.Should().BeEmpty();
        await shell.FlushAsync(TestContext.Current.CancellationToken);
    }

    private sealed class YieldingRunService : IDesktopRunService
    {
        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RunContextSnapshot(
                false, false, false, 0, "run-1", null, null, null, null, null));

        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RunStartResult(true, request.RunId, null));

        public async Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return new RunProgressSnapshot(
                RunState.Completed,
                false,
                false,
                false,
                100,
                "completed",
                new RunProgressStats(5, 5, 0),
                [],
                null,
                false,
                null,
                null,
                null,
                null);
        }

        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RunStopResult(true, false, null));
    }

    private sealed class StubPage(string key) : IPageViewModel
    {
        public string Key { get; } = key;
        public string Title => Key;
        public string Eyebrow => "Step";
        public Task OnActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnDeactivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CompletionPage(string key) : IPageViewModel
    {
        public string Key { get; } = key;
        public string Title => Key;
        public string Eyebrow => "Step";
        public TaskCompletionSource Activated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnActivatedAsync(CancellationToken cancellationToken)
        {
            Activated.TrySetResult();
            return Task.CompletedTask;
        }

        public Task OnDeactivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullDisclaimerService : IDisclaimerService
    {
        public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullPathLauncher : IDesktopPathLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
