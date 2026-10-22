using Avalonia.Controls;
using Avalonia.Interactivity;
using Serilog;

namespace InvoiceFlowAI.App.Views;

public partial class SettingsPageView : UserControl
{
    private static readonly Uri ApiKeysUri = new("https://platform.deepseek.com/api_keys");

    public SettingsPageView()
    {
        InitializeComponent();
    }

    private async void OnOpenApiKeysClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null)
            {
                await topLevel.Launcher.LaunchUriAsync(ApiKeysUri);
            }
        }
        catch (Exception exception)
        {
            // async void 事件处理器不允许异常逃逸到 UI 线程,否则会终止进程。
            Log.Error(exception, "打开 API Key 页面失败。");
        }
    }
}
