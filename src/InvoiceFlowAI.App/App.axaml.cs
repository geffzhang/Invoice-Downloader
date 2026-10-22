using Avalonia.Controls.ApplicationLifetimes;
using InvoiceFlowAI.App.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

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
            try
            {
                _serviceProvider = AppServiceProviderFactory.Create();
                desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                desktop.Exit += (_, _) =>
                {
                    _serviceProvider?.Dispose();
                    _serviceProvider = null;
                };
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "桌面服务初始化失败。");
                desktop.MainWindow = new BootstrapErrorWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
