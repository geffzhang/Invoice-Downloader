using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InvoiceFlowAI.App.Bootstrap;

public partial class BootstrapErrorWindow : Window
{
    public BootstrapErrorWindow()
    {
        InitializeComponent();
        LogPathText.Text = $"日志目录:{LogPaths.LogDirectory}";
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
