using Avalonia.Controls;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.WebView;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App;

public partial class MainWindow : Window
{
    private readonly IWebViewRpcBridge _bridge;
    private readonly AvaloniaMainWindowAccessor? _windowAccessor;
    private readonly WebViewRunEventPublisher? _runEventPublisher;
    private readonly ServiceProvider? _ownedServiceProvider;

    public MainWindow() : this(CreateStandaloneHost())
    {
    }

    private MainWindow(StandaloneHost host)
        : this(host.Dispatcher, host.WindowAccessor, host.RunEventPublisher, host.ServiceProvider)
    {
    }

    public MainWindow(IRpcDispatcher dispatcher)
        : this(dispatcher, null, (WebViewRunEventPublisher?)null, null)
    {
    }

    public MainWindow(IRpcDispatcher dispatcher, AvaloniaMainWindowAccessor? windowAccessor)
        : this(dispatcher, windowAccessor, null, null)
    {
    }

    public MainWindow(
        IRpcDispatcher dispatcher,
        AvaloniaMainWindowAccessor? windowAccessor,
        WebViewRunEventPublisher runEventPublisher)
        : this(dispatcher, windowAccessor, runEventPublisher, null)
    {
    }

    private MainWindow(IRpcDispatcher dispatcher, AvaloniaMainWindowAccessor? windowAccessor, ServiceProvider? ownedServiceProvider)
        : this(dispatcher, windowAccessor, null, ownedServiceProvider)
    {
    }

    private MainWindow(
        IRpcDispatcher dispatcher,
        AvaloniaMainWindowAccessor? windowAccessor,
        WebViewRunEventPublisher? runEventPublisher,
        ServiceProvider? ownedServiceProvider)
    {
        InitializeComponent();
        _windowAccessor = windowAccessor;
        _runEventPublisher = runEventPublisher;
        _ownedServiceProvider = ownedServiceProvider;
        _windowAccessor?.Set(this);

        var channel = new AvaloniaNativeWebViewMessageChannel(AppWebView);
        _bridge = new WebViewRpcBridge(dispatcher, channel, new WebViewRpcBridgeOptions());
        _runEventPublisher?.Attach(_bridge);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _bridge.Start();
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
        AppWebView.Source = new Uri(indexPath);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _bridge.Stop();
        if (_runEventPublisher is not null)
        {
            _runEventPublisher.Detach(_bridge);
        }
        _windowAccessor?.Set(null);
        _ownedServiceProvider?.Dispose();
    }

    private static StandaloneHost CreateStandaloneHost()
    {
        var provider = AppServiceProviderFactory.Create();
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var dispatcher = AppRpcComposition.CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), version);
        return new StandaloneHost(
            dispatcher,
            provider.GetRequiredService<AvaloniaMainWindowAccessor>(),
            provider.GetRequiredService<WebViewRunEventPublisher>(),
            provider);
    }

    private sealed record StandaloneHost(
        IRpcDispatcher Dispatcher,
        AvaloniaMainWindowAccessor WindowAccessor,
        WebViewRunEventPublisher RunEventPublisher,
        ServiceProvider ServiceProvider);
}
