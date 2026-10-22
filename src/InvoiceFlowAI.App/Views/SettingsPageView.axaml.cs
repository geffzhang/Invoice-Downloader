using Avalonia.Controls;
using Avalonia.Interactivity;

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
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            await topLevel.Launcher.LaunchUriAsync(ApiKeysUri);
        }
    }
}
