using Avalonia.Controls;

namespace InvoiceFlowAI.App.Settings;

public interface IMainWindowAccessor
{
    Window? MainWindow { get; }
}