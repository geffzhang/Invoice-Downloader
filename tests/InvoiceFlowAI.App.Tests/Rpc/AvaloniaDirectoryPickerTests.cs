using FluentAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using InvoiceFlowAI.App.Settings;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AvaloniaDirectoryPickerTests
{
    // Avalonia.Headless.XUnit 12.1.4 is incompatible with xunit.v3 4.0.2 (its discoverer
    // calls a TestIntrospectionHelper method removed in v3 4.0), and a manually managed
    // HeadlessUnitTestSession deadlocks in Dispose under v3 (no AsyncTestSyncContext to
    // pump the dispatcher during application teardown). This test only needs
    // IWindowingPlatform registered so Window can be constructed, so we initialize the
    // headless platform without starting a dispatcher loop. Intentionally never disposed:
    // the test process exits with the run. See design spec §7.2.
    private static void EnsureHeadlessPlatform()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
        }
    }

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
    public void Main_window_accessor_tracks_window_open_and_close()
    {
        EnsureHeadlessPlatform();

        var accessor = new AvaloniaMainWindowAccessor();
        var window = new Window();

        accessor.Set(window);
        accessor.MainWindow.Should().BeSameAs(window);

        accessor.Set(null);
        accessor.MainWindow.Should().BeNull();
    }
}
