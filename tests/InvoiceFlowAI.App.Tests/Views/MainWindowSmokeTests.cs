using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void Main_window_builds_with_three_native_pages()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-window-{Guid.NewGuid():N}");
        try
        {
            var pageKeys = AtomUiHeadlessHost.Run(() =>
            {
                using var provider = AppServiceProviderFactory.Create(appDataDirectory);
                var window = provider.GetRequiredService<MainWindow>();
                var shell = window.DataContext.Should().BeOfType<ShellViewModel>().Subject;
                return shell.Pages.Select(page => page.Key).ToArray();
            });

            pageKeys.Should().Equal("settings", "processing", "analysis");
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
            {
                Directory.Delete(appDataDirectory, recursive: true);
            }
        }
    }
}
