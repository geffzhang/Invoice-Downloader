using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App;

/// <summary>
/// Avalonia UI application root and desktop RPC composition point.
/// </summary>
public partial class App : Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _serviceProvider = AppServiceProviderFactory.Create();
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var dispatcher = AppRpcComposition.CreateDispatcher(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                version);
            var windowAccessor = _serviceProvider.GetRequiredService<AvaloniaMainWindowAccessor>();
            desktop.MainWindow = new MainWindow(dispatcher, windowAccessor);
            desktop.Exit += (_, _) =>
            {
                _serviceProvider?.Dispose();
                _serviceProvider = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
