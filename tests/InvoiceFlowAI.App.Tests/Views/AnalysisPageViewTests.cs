using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class AnalysisPageViewTests
{
    [Fact]
    public void Analysis_page_view_builds_summary_review_and_output_sections()
    {
        var result = AtomUiHeadlessHost.Run(() =>
        {
            var view = new AnalysisPageView();
            var window = new Avalonia.Controls.Window
            {
                Width = 1180,
                Height = 760,
                Content = view,
            };
            window.Measure(new Avalonia.Size(1180, 760));
            return (
                Cards: view.GetVisualDescendants().OfType<Card>().Count(),
                Buttons: view.GetVisualDescendants().OfType<AtomUI.Desktop.Controls.Button>().Count());
        });

        result.Cards.Should().BeGreaterThanOrEqualTo(6);
        result.Buttons.Should().BeGreaterThanOrEqualTo(3);
    }
}
