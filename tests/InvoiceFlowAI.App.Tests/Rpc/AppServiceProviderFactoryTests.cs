using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
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
                await context.SaveChangesAsync();
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
                provider.GetRequiredService<IRunEventPublisher>()
                    .Should().BeSameAs(provider.GetRequiredService<WebViewRunEventPublisher>());
                provider.GetRequiredService<IDesktopRunExecutorLeaseFactory>()
                    .Should().BeOfType<ScopedDesktopRunExecutorLeaseFactory>();
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
                    .LoadAsync(CancellationToken.None);

                settings.RuleSetId.Should().Be("default");
                settings.RuleSetVersion.Should().Be(1);
                settings.Revision.Should().BeGreaterThan(0);
                (await scope.ServiceProvider.GetRequiredService<InvoiceFlowAI.Infrastructure.Persistence.InvoiceFlowDbContext>()
                    .Database.GetPendingMigrationsAsync()).Should().BeEmpty();
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

    [Fact]
    public async Task Typed_rpc_surface_dispatches_without_exposing_secrets_or_legacy_methods()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-rpc-{Guid.NewGuid():N}");
        try
        {
            await using (var provider = AppServiceProviderFactory.Create(appDataDirectory))
            {
                var dispatcher = AppRpcComposition.CreateDispatcher(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    "3.0.0");

                var settings = await DispatchAsync(dispatcher, "settings.get", parameters: null);
                settings.GetProperty("revision").GetInt32().Should().Be(1);

                var updatedSettings = await DispatchAsync(dispatcher, "settings.update",
                    new SettingsUpdateRequest(1, CompanyName: "RPC Buyer"));
                updatedSettings.GetProperty("companyName").GetString().Should().Be("RPC Buyer");

                var accounts = await DispatchAsync(dispatcher, "account.list", parameters: null);
                accounts.GetProperty("items").GetArrayLength().Should().Be(0);

                var savedAccount = await DispatchAsync(dispatcher, "account.save",
                    new AccountSaveRequest(new MailboxAccountDraft(
                        "rpc-account",
                        "buyer@example.com",
                        "imap.example.com",
                        993,
                        true,
                        "mail.imap.auth-code",
                        "Buyer"), ExpectedRevision: 0));
                savedAccount.GetProperty("credentialConfigured").GetBoolean().Should().BeFalse();

                var accountTest = await DispatchAsync(dispatcher, "account.test", new AccountTestRequest("rpc-account"));
                accountTest.GetProperty("failureCode").GetString().Should().Be("CREDENTIALS_NOT_CONFIGURED");

                var providerTest = await DispatchAsync(dispatcher, "provider.test",
                    new ProviderTestRequest("deepseek", "deepseek.api-key"));
                providerTest.GetProperty("failureCode").GetString().Should().Be("CREDENTIALS_NOT_CONFIGURED");

                const string secretValue = "temporary-rpc-secret";
                var secretSet = await DispatchAsync(dispatcher, "secret.set",
                    new SecretSetRequest("deepseek.api-key", secretValue, SecretRetention.Session));
                secretSet.GetRawText().Should().NotContain(secretValue);
                (await DispatchAsync(dispatcher, "secret.delete", new SecretDeleteRequest("deepseek.api-key")))
                    .GetProperty("configured").GetBoolean().Should().BeFalse();

                var directory = await DispatchAsync(dispatcher, "directory.choose", parameters: null);
                directory.GetProperty("cancelled").GetBoolean().Should().BeTrue();
                dispatcher.RegisteredMethods.Should().Contain("run.start");
                dispatcher.RegisteredMethods.Should().Contain("run.results.get");
                dispatcher.RegisteredMethods.Should().Contain([
                    "run.folder.open",
                    "run.manual-review.open",
                    "run.file.open",
                    "window.minimize",
                    "window.maximize",
                    "window.close",
                ]);

                var unavailableFolder = await DispatchAsync(dispatcher, "run.folder.open", parameters: null);
                unavailableFolder.GetProperty("succeeded").GetBoolean().Should().BeFalse();

                var context = await DispatchAsync(dispatcher, "run.context.get", parameters: null);
                context.GetProperty("explicitRunContext").GetBoolean().Should().BeFalse();

                var status = await DispatchAsync(dispatcher, "run.progress.get", parameters: null);
                status.GetProperty("isRunning").GetBoolean().Should().BeFalse();

                var invalidStart = await dispatcher.DispatchAsync(
                    new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "invalid-run", "run.start", null),
                    CancellationToken.None);
                invalidStart.Ok.Should().BeFalse();
                invalidStart.Error!.Code.Should().Be(RpcDispatcher.InvalidParamsCode);

                var missingReport = await dispatcher.DispatchAsync(
                    new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "missing-report", "run.report.export",
                        JsonSerializer.SerializeToElement(new ReportExportRequest("missing-run"), JsonOptions.Default)),
                    CancellationToken.None);
                missingReport.Ok.Should().BeFalse();
                missingReport.Error!.Code.Should().Be(RpcErrorCodes.ReportExportFailed);

                var missingResults = await dispatcher.DispatchAsync(
                    new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "missing-results", "run.results.get", null),
                    CancellationToken.None);
                missingResults.Ok.Should().BeFalse();
                missingResults.Error!.Code.Should().Be(RpcErrorCodes.RunNotFound);
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

    private static async Task<JsonElement> DispatchAsync(RpcDispatcher dispatcher, string method, object? parameters)
    {
        JsonElement? element = parameters is null
            ? null
            : JsonSerializer.SerializeToElement(parameters, JsonOptions.Default);
        var response = await dispatcher.DispatchAsync(
            new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, Guid.NewGuid().ToString("N"), method, element),
            CancellationToken.None);
        response.Ok.Should().BeTrue(response.Error?.UserMessage);
        return response.Result!.Value;
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