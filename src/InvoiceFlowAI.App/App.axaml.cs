using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace InvoiceFlowAI.App;

/// <summary>
/// Avalonia UI 12 Application root. Full hosting logic (WebView local navigation,
/// JSON/RPC bridge, lifecycle/state-machine wiring) lands in Tasks 9 and following
/// per the migration implementation plan. This minimal Application compiles the
/// empty scaffold so `dotnet build` succeeds before the full bridge is wired.
/// </summary>
public partial class App : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
