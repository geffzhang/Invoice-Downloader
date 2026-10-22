using FluentAssertions;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Infrastructure.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using InvoiceFlowAI.Infrastructure.Reports;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AppServiceProviderFactoryTests
{
    [Fact]
    public async Task Create_reconciles_archive_inventory_before_returning_provider()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-startup-recovery-{Guid.NewGuid():N}");
        var recovery = new RecordingArchiveRecoveryService();
        try
        {
            await using (var initialProvider = AppServiceProviderFactory.Create(appDataDirectory))
            await using (var scope = initialProvider.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<InvoiceFlowAI.Infrastructure.Persistence.InvoiceFlowDbContext>();
                context.Runs.AddRange(
                    NewRun("prior-run-a", @"C:\Invoices\A"),
                    NewRun("prior-run-b", @"D:\Invoices\B"));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using var provider = AppServiceProviderFactory.Create(
                appDataDirectory,
                services => services.AddSingleton<IArchiveRecoveryService>(recovery));

            recovery.Calls.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(appDataDirectory)) Directory.Delete(appDataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Create_applies_test_service_overrides_before_building_provider()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-app-override-{Guid.NewGuid():N}");
        try
        {
            await using var provider = AppServiceProviderFactory.Create(
                appDataDirectory,
                services => services.AddSingleton<IMailboxSessionFactory, TestMailboxSessionFactory>());

            provider.GetRequiredService<IMailboxSessionFactory>()
                .Should().BeOfType<TestMailboxSessionFactory>();
        }
        finally
        {
            if (Directory.Exists(appDataDirectory)) Directory.Delete(appDataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Create_migrates_database_bootstraps_default_settings_and_registers_rpc_services()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-app-{Guid.NewGuid():N}");
        try
        {
            await using (var provider = AppServiceProviderFactory.Create(appDataDirectory))
            await using (var scope = provider.CreateAsyncScope())
            {
                provider.GetRequiredService<IRunEventPublisher>().Should().BeOfType<RunEventPublisherFanOut>();
                provider.GetRequiredService<IAppEventBus>().Should().BeOfType<AppEventBus>();
                provider.GetRequiredService<IDesktopRunExecutorLeaseFactory>()
                    .Should().BeOfType<ScopedDesktopRunExecutorLeaseFactory>();
                provider.GetRequiredService<IAppNavigator>().Should().BeOfType<AppNavigator>();
                provider.GetRequiredService<IRunSession>().Should().BeOfType<RunSession>();
                provider.GetRequiredService<ShellViewModel>().Pages.Should().HaveCount(3);
                var pages = provider.GetServices<IPageViewModel>().ToArray();
                pages.Should().Contain(page => page.GetType() == typeof(SettingsViewModel));
                pages.Should().NotContain(page =>
                    page.GetType() == typeof(WebPageViewModel) && page.Key == "settings");
                scope.ServiceProvider.GetRequiredService<IDesktopRunService>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IDesktopRunExecutor>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IMailboxScanner>().Should().BeOfType<MailKitMailboxScanner>();
                scope.ServiceProvider.GetRequiredService<IRunCoordinator>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IRunLifecycleStore>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IRunCheckpointStore>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IEventReplayStore>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IAuditEventStore>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IReportRunDataSource>().Should().BeOfType<EfReportRunDataStore>();
                scope.ServiceProvider.GetRequiredService<IReportPathStore>()
                    .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IReportRunDataSource>());
                scope.ServiceProvider.GetRequiredService<IReportExporter>().Should().BeOfType<ClosedXmlReportExporter>();
                scope.ServiceProvider.GetRequiredService<IReportOpenTokenStore>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IReportApplicationService>().Should().BeOfType<ReportApplicationService>();
                scope.ServiceProvider.GetRequiredService<IReportExportStage>().Should().BeOfType<ReportExportStage>();
                scope.ServiceProvider.GetRequiredService<RunResultsQueryService>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<RunContextRpcHandler>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<RunStartRpcHandler>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<RunStatusRpcHandler>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<RunStopRpcHandler>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<ReportExportRpcHandler>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<RunResultsGetRpcHandler>().Should().NotBeNull();
                await using var secondScope = provider.CreateAsyncScope();
                secondScope.ServiceProvider.GetRequiredService<ActiveRunRegistry>()
                    .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<ActiveRunRegistry>());
                secondScope.ServiceProvider.GetRequiredService<IDesktopRunService>()
                    .Should().NotBeSameAs(scope.ServiceProvider.GetRequiredService<IDesktopRunService>());

                var settings = await scope.ServiceProvider.GetRequiredService<IUserSettingsStore>()
                    .LoadAsync(TestContext.Current.CancellationToken);

                settings.RuleSetId.Should().Be("default");
                settings.RuleSetVersion.Should().Be(1);
                settings.Revision.Should().BeGreaterThan(0);
                (await scope.ServiceProvider.GetRequiredService<InvoiceFlowAI.Infrastructure.Persistence.InvoiceFlowDbContext>()
                    .Database.GetPendingMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
            {
                Directory.Delete(appDataDirectory, recursive: true);
            }
        }
    }

    private sealed class TestMailboxSessionFactory : IMailboxSessionFactory
    {
        public IMailboxSession Create() => throw new NotSupportedException();
    }

    private static RunRow NewRun(string runId, string outputRoot) => new()
    {
        RunId = runId,
        State = "Completed",
        Stage = "complete",
        DateFrom = new DateOnly(2026, 9, 1),
        DateToExclusive = new DateOnly(2026, 10, 1),
        OutputRoot = outputRoot,
        StartedAtUtc = DateTimeOffset.UtcNow,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingArchiveRecoveryService : IArchiveRecoveryService
    {
        public int Calls { get; private set; }

        public Task<ArchiveStartupRecoveryResult> ReconcileAllKnownRootsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ArchiveStartupRecoveryResult([], []));
        }

        public Task<IReadOnlyList<ArchiveRecoveryEntry>> ScanAsync(string runId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ArchiveRecoveryDecision> ResolveAsync(ArchiveRecoveryEntry entry, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LegacyArchiveRecoveryDecision>> ReconcileLegacyAsync(string outputRoot, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ArchiveStartupRecoveryResult> ReconcileBeforeRunAsync(string outputRoot, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}