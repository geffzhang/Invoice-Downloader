using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Settings;

public sealed class AvaloniaDirectoryPicker(IMainWindowAccessor windowAccessor) : IDirectoryPicker
{
    public async Task<DirectoryChooseResult> ChooseAsync(CancellationToken cancellationToken)
    {
        var window = windowAccessor.MainWindow;
        if (window is null)
        {
            return new DirectoryChooseResult(Cancelled: true, Path: null);
        }

        var selectedPath = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择发票输出目录",
                AllowMultiple = false,
            });
            return folders.Count == 0 ? null : folders[0].Path.LocalPath;
        }).WaitAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(selectedPath)
            ? new DirectoryChooseResult(Cancelled: true, Path: null)
            : new DirectoryChooseResult(Cancelled: false, Path: System.IO.Path.GetFullPath(selectedPath));
    }
}