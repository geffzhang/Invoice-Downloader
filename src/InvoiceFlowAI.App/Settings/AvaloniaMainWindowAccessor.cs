using Avalonia.Controls;

namespace InvoiceFlowAI.App.Settings;

public sealed class AvaloniaMainWindowAccessor : IMainWindowAccessor
{
    private Window? _mainWindow;

    public Window? MainWindow => Volatile.Read(ref _mainWindow);

    public void Set(Window? window) => Volatile.Write(ref _mainWindow, window);
}