using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly string[] PageOrder = ["settings", "processing", "analysis"];

    private readonly AppNavigator _navigator;
    private readonly IDisclaimerService _disclaimer;
    private readonly IDesktopPathLauncher _pathLauncher;
    private readonly IReadOnlyDictionary<string, IPageViewModel> _pages;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);

    // 生命周期回调持有导航锁;标记用于识别回调内发起的导航并快速失败,避免自死锁。
    private static readonly AsyncLocal<bool> LifecycleCallbackActive = new();

    [ObservableProperty]
    private IPageViewModel? _currentPage;

    private bool _initialized;

    public ShellViewModel(
        IAppNavigator navigator,
        IEnumerable<IPageViewModel> pages,
        IDisclaimerService disclaimer,
        IDesktopPathLauncher pathLauncher)
    {
        // 壳是导航图的唯一装配点,因此必须持有可 Attach 的具体实现。
        _navigator = navigator as AppNavigator ?? throw new ArgumentException(
            "Shell navigation requires an AppNavigator instance.",
            nameof(navigator));
        _disclaimer = disclaimer ?? throw new ArgumentNullException(nameof(disclaimer));
        _pathLauncher = pathLauncher ?? throw new ArgumentNullException(nameof(pathLauncher));
        ArgumentNullException.ThrowIfNull(pages);

        Pages = pages
            .OrderBy(page => Array.IndexOf(PageOrder, page.Key) is var index && index >= 0 ? index : int.MaxValue)
            .ToArray();
        _pages = Pages.ToDictionary(page => page.Key, StringComparer.Ordinal);
        Version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public IReadOnlyList<IPageViewModel> Pages { get; }

    public string Version { get; }

    public string WebsiteUrl => "https://github.com/EthanYoQ/Invoice-Downloader";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _navigator.Attach(NavigateAsync);
        if (Pages.Count > 0)
        {
            await NavigateAsync(Pages[0].Key, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task NavigateAsync(string key, CancellationToken cancellationToken)
    {
        if (LifecycleCallbackActive.Value)
        {
            throw new InvalidOperationException("导航不能在页面生命周期回调期间发起。");
        }

        if (!_pages.TryGetValue(key, out var target) || ReferenceEquals(CurrentPage, target))
        {
            return;
        }

        await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        LifecycleCallbackActive.Value = true;
        try
        {
            var previous = CurrentPage;
            if (previous is not null)
            {
                await previous.OnDeactivatedAsync(cancellationToken).ConfigureAwait(true);
            }

            CurrentPage = target;
            await target.OnActivatedAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            LifecycleCallbackActive.Value = false;
            _navigationGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (CurrentPage is { } page)
        {
            await page.OnDeactivatedAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task GoHomeAsync(CancellationToken cancellationToken) => NavigateAsync("settings", cancellationToken);

    [RelayCommand]
    private Task ShowDisclaimerAsync(CancellationToken cancellationToken) => _disclaimer.ShowAsync(cancellationToken);

    [RelayCommand]
    private Task OpenWebsiteAsync(CancellationToken cancellationToken) => _pathLauncher.OpenAsync(WebsiteUrl, cancellationToken);
}
