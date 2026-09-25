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
    private readonly ServiceProvider? _ownedServiceProvider;

    public MainWindow() : this(CreateStandaloneHost())
    {
    }

    private MainWindow(StandaloneHost host)
        : this(host.Dispatcher, host.WindowAccessor, host.ServiceProvider)
    {
    }

    public MainWindow(IRpcDispatcher dispatcher)
        : this(dispatcher, null, null)
    {
    }

    public MainWindow(IRpcDispatcher dispatcher, AvaloniaMainWindowAccessor? windowAccessor)
        : this(dispatcher, windowAccessor, null)
    {
    }

    private MainWindow(IRpcDispatcher dispatcher, AvaloniaMainWindowAccessor? windowAccessor, ServiceProvider? ownedServiceProvider)
    {
        InitializeComponent();
        _windowAccessor = windowAccessor;
        _ownedServiceProvider = ownedServiceProvider;
        _windowAccessor?.Set(this);

        var channel = new AvaloniaNativeWebViewMessageChannel(AppWebView);
        _bridge = new WebViewRpcBridge(dispatcher, channel, new WebViewRpcBridgeOptions());
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
        _windowAccessor?.Set(null);
        _ownedServiceProvider?.Dispose();
    }

    private static StandaloneHost CreateStandaloneHost()
    {
        var provider = AppServiceProviderFactory.Create();
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var dispatcher = AppRpcComposition.CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), version);
        return new StandaloneHost(dispatcher, provider.GetRequiredService<AvaloniaMainWindowAccessor>(), provider);
    }

    private sealed record StandaloneHost(
        IRpcDispatcher Dispatcher,
        AvaloniaMainWindowAccessor WindowAccessor,
        ServiceProvider ServiceProvider);
}
