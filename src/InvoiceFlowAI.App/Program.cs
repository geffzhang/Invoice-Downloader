using System;
using Avalonia;

namespace InvoiceFlowAI.App;

/// <summary>
/// Process entry point for the Avalonia 12 desktop app. Single-instance lock,
/// AppPaths resolution, Serilog startup, release-manifest verification, SQLite
/// integrity check, EF Core migrations, recipe validation, and graph build per
/// design §3 startup state machine all live in AppHost.StartAsync() once Task 9
/// lands. The minimal Main here exists so the project compiles before the full
/// bootstrap is wired.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
