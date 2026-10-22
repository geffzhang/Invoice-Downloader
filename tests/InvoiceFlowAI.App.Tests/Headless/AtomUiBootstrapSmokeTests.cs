using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Styling;
using FluentAssertions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Headless;

public sealed class AtomUiBootstrapSmokeTests
{
    [Fact]
    public void AtomUI_bootstrap_provides_desktop_controls_and_dark_theme()
    {
        var snapshot = AtomUiHeadlessHost.Run(() =>
        {
            var application = Avalonia.Application.Current!;
            var navMenu = new NavMenu { Mode = NavMenuMode.Vertical };
            var lineEdit = new LineEdit { PlaceholderText = "测试" };
            return new BootstrapSnapshot(
                application.Styles.Count,
                navMenu.Mode,
                lineEdit.PlaceholderText,
                application.RequestedThemeVariant);
        });

        snapshot.StyleCount.Should().BeGreaterThan(0);
        snapshot.NavMenuMode.Should().Be(NavMenuMode.Vertical);
        snapshot.PlaceholderText.Should().Be("测试");
        snapshot.ThemeVariant.Should().Be(ThemeVariant.Dark);
    }

    private sealed record BootstrapSnapshot(
        int StyleCount,
        NavMenuMode NavMenuMode,
        string? PlaceholderText,
        ThemeVariant? ThemeVariant);
}
