using FluentAssertions;
using Avalonia.Controls;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.Tests.Headless;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AvaloniaDirectoryPickerTests
{
    [Fact]
    public async Task Choose_without_active_window_returns_cancelled_result()
    {
        var accessor = new AvaloniaMainWindowAccessor();
        var picker = new AvaloniaDirectoryPicker(accessor);

        var result = await picker.ChooseAsync(TestContext.Current.CancellationToken);

        result.Cancelled.Should().BeTrue();
        result.Path.Should().BeNull();
    }

    [Fact]
    public void Main_window_accessor_tracks_window_open_and_close()
    {
        // Avalonia can only be set up once per process, and the AtomUI headless host
        // owns that setup on its dedicated STA thread (see design spec §7.2). All UI
        // construction therefore goes through the shared host.
        AtomUiHeadlessHost.Run(() =>
        {
            var accessor = new AvaloniaMainWindowAccessor();
            var window = new Window();

            accessor.Set(window);
            accessor.MainWindow.Should().BeSameAs(window);

            accessor.Set(null);
            accessor.MainWindow.Should().BeNull();
        });
    }
}
