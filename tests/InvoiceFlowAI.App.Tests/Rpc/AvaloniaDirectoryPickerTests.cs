using FluentAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using InvoiceFlowAI.App.Settings;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AvaloniaDirectoryPickerTests
{
    [Fact]
    public async Task Choose_without_active_window_returns_cancelled_result()
    {
        var accessor = new AvaloniaMainWindowAccessor();
        var picker = new AvaloniaDirectoryPicker(accessor);

        var result = await picker.ChooseAsync(CancellationToken.None);

        result.Cancelled.Should().BeTrue();
        result.Path.Should().BeNull();
    }

    [Fact]
    public async Task Main_window_accessor_tracks_window_open_and_close()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DirectoryPickerTestApplication));

        await session.Dispatch(() =>
        {
            var accessor = new AvaloniaMainWindowAccessor();
            var window = new Window();

            accessor.Set(window);
            accessor.MainWindow.Should().BeSameAs(window);

            accessor.Set(null);
            accessor.MainWindow.Should().BeNull();
        }, CancellationToken.None);
    }
}

public sealed class DirectoryPickerTestApplication : Avalonia.Application
{
}