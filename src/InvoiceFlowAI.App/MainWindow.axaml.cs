using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using AtomUI.Icons.AntDesign;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App;

public partial class MainWindow : AtomUI.Desktop.Controls.Window
{
    private readonly ShellViewModel _shell;
    private readonly AvaloniaMainWindowAccessor _windowAccessor;
    private readonly IRunShutdownService _shutdown;
    private readonly ILogger<MainWindow> _logger;
    private readonly Dictionary<string, NavMenuNode> _nodes = new(StringComparer.Ordinal);
    private bool _syncingSelection;
    private bool _closeConfirmed;

    /// <summary>
    /// XAML 运行时加载器的构造入口。应用始终通过依赖注入组合本窗口,
    /// 不提供服务时无法独立构建。
    /// </summary>
    public MainWindow()
    {
        throw new InvalidOperationException("MainWindow 必须由应用的服务提供程序创建。");
    }

    public MainWindow(
        ShellViewModel shell,
        AvaloniaMainWindowAccessor windowAccessor,
        IRunShutdownService shutdown,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _windowAccessor = windowAccessor ?? throw new ArgumentNullException(nameof(windowAccessor));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DataContext = shell;
        _windowAccessor.Set(this);
        BuildNavigation();

        NavigationMenu.NavMenuNodeSelected += OnNavigationNodeSelected;
        _shell.PropertyChanged += OnShellPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void BuildNavigation()
    {
        var icons = new Dictionary<string, PathIcon>(StringComparer.Ordinal)
        {
            ["settings"] = new SettingOutlined(),
            ["processing"] = new ThunderboltOutlined(),
            ["analysis"] = new FundOutlined(),
        };

        var nodes = new List<NavMenuNode>(_shell.Pages.Count);
        foreach (var page in _shell.Pages)
        {
            var node = new NavMenuNode
            {
                Header = page.Title,
                ItemKey = new EntityKey(page.Key),
                Icon = icons.GetValueOrDefault(page.Key),
            };
            _nodes[page.Key] = node;
            nodes.Add(node);
        }

        NavigationMenu.ItemsSource = nodes;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _shell.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "壳初始化失败。");
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            UpdateContent();
        }
    }

    private void UpdateContent()
    {
        try
        {
            var page = _shell.CurrentPage;
            NativeContent.Content = page;
            SyncSelection(page);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "页面切换失败。");
        }
    }

    private void SyncSelection(IPageViewModel? page)
    {
        if (page is null || !_nodes.TryGetValue(page.Key, out var node))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            NavigationMenu.SelectedItem = node;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private async void OnNavigationNodeSelected(object? sender, NavMenuNodeSelectedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        var key = (e.NavMenuNode as NavMenuNode)?.ItemKey?.Value;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        // 选中状态由 SyncSelection 回写,NavMenu 会再异步发一次选中事件;当前页重复选中直接忽略。
        if (string.Equals(_shell.CurrentPage?.Key, key, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _shell.NavigateAsync(key, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "导航失败:{PageKey}", key);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        try
        {
            await _shell.FlushAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "关闭前保存设置失败。");
        }

        try
        {
            var decision = await _shutdown.ConfirmCloseAsync(CancellationToken.None);
            if (decision == RunCloseDecision.Close)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "关闭确认失败。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shell.PropertyChanged -= OnShellPropertyChanged;
        NavigationMenu.NavMenuNodeSelected -= OnNavigationNodeSelected;
        _windowAccessor.Set(null);
    }
}
