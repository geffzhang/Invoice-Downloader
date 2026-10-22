using AtomUI;
using Avalonia;
using InvoiceFlowAI.App.Bootstrap;
using Serilog;

namespace InvoiceFlowAI.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LogPaths.EnsureLogDirectory();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                LogPaths.ApplicationLogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动失败。");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseAtomUIPlatformDetect()
            .WithInterFont()
            .WithAtomUIDefaultOptions()
            .UseAtomUI(AtomUiConfiguration.Configure)
            .LogToTrace();
}
