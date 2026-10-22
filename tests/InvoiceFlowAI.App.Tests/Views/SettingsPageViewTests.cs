using Avalonia.Controls;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class SettingsPageViewTests
{
    [Fact]
    public void Settings_page_view_builds_its_control_tree()
    {
        var cardCount = AtomUiHeadlessHost.Run(() =>
        {
            var view = new SettingsPageView();
            var window = new Avalonia.Controls.Window
            {
                Width = 1180,
                Height = 760,
                Content = view,
            };
            window.Measure(new Avalonia.Size(1180, 760));
            return view.GetVisualDescendants().OfType<Card>().Count();
        });

        cardCount.Should().BeGreaterThanOrEqualTo(4);
    }
}
