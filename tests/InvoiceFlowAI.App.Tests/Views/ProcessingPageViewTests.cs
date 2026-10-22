using Avalonia.Controls;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class ProcessingPageViewTests
{
    [Fact]
    public void Processing_page_view_builds_progress_and_log_list()
    {
        var result = AtomUiHeadlessHost.Run(() =>
        {
            var view = new ProcessingPageView();
            var window = new Avalonia.Controls.Window
            {
                Width = 1180,
                Height = 760,
                Content = view,
            };
            window.Measure(new Avalonia.Size(1180, 760));
            return new ViewSnapshot(
                view.GetVisualDescendants().OfType<AtomUI.Desktop.Controls.ProgressBar>().Count(),
                view.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Count());
        });

        result.ProgressBars.Should().BeGreaterThanOrEqualTo(1);
        result.Lists.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed record ViewSnapshot(int ProgressBars, int Lists);
}
