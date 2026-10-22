using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace InvoiceFlowAI.App;

public static class AppServiceProviderFactory
{
    public static ServiceProvider Create(
        string? appDataDirectory = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var dataDirectory = Path.GetFullPath(appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InvoiceFlowAI"));
        Directory.CreateDirectory(dataDirectory);

        var databasePath = Path.Combine(dataDirectory, "invoiceflow.sqlite3");
        var secretPath = Path.Combine(dataDirectory, "secrets.json");
        var legacySnapshotPath = Path.Combine(dataDirectory, "user-settings.snapshot.json");
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("InvoiceFlowAI|secret-store|v1"));

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));
        services.AddDbContext<InvoiceFlowDbContext>(options => options.UseSqlite(
            $"Data Source={databasePath};Cache=Shared;Pooling=False"));
        services.AddSingleton<IPersistentSecretStore>(new DpapiSecretStore(secretPath, entropy));
        services.AddSingleton<IUserSettingsSnapshotStore>(new JsonUserSettingsSnapshotStore(legacySnapshotPath));
        services.AddSingleton<AvaloniaMainWindowAccessor>();
        services.AddSingleton<IDesktopPathLauncher, ShellDesktopPathLauncher>();
        services.AddSingleton<IAvaloniaUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IAppEventBus, AppEventBus>();
        services.AddSingleton<NativeRunEventPublisher>();
        services.AddSingleton<IRunEventPublisher>(provider => provider.GetRequiredService<NativeRunEventPublisher>());
        services.AddSingleton<IDesktopRunExecutorLeaseFactory, ScopedDesktopRunExecutorLeaseFactory>();
        services.AddSingleton<IMainWindowAccessor>(provider => provider.GetRequiredService<AvaloniaMainWindowAccessor>());
        services.AddSingleton<IDirectoryPicker, AvaloniaDirectoryPicker>();
        services.AddSingleton<IScopedOperationRunner, ScopedOperationRunner>();
        services.AddSingleton<IRunSession, RunSession>();
        services.AddSingleton<IAppNavigator, AppNavigator>();
        services.AddSingleton<IDialogService, AtomUiDialogService>();
        services.AddSingleton<INotificationService, AtomUiNotificationService>();
        services.AddSingleton<IDisclaimerService, DisclaimerService>();
        services.AddSingleton<IRunShutdownService, RunShutdownService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsViewModel>(provider => new SettingsViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IDirectoryPicker>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IPersistentSecretStore>(),
            provider.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<SettingsViewModel>());
        services.AddSingleton(ProcessingOptions.Production);
        services.AddSingleton<ProcessingViewModel>(provider => new ProcessingViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IAppEventBus>(),
            provider.GetRequiredService<IAvaloniaUiDispatcher>(),
            provider.GetRequiredService<ProcessingOptions>(),
            provider.GetRequiredService<ILogger<ProcessingViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<ProcessingViewModel>());
        services.AddSingleton(AnalysisOptions.Production);
        services.AddSingleton<AnalysisViewModel>(provider => new AnalysisViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<AnalysisOptions>(),
            provider.GetRequiredService<ILogger<AnalysisViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<AnalysisViewModel>());
        services.AddInvoiceFlowInfrastructure();
        services.AddInvoiceFlowApplication();

        services.AddScoped<IUserSettingsStore, EfUserSettingsStore>();
        services.AddScoped(provider => new InvoiceExtractionRules(
            provider.GetRequiredService<IUserSettingsStore>()
                .LoadAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .CompanyName));
        services.AddScoped<IMailboxAccountStore, EfMailboxAccountStore>();
        services.AddScoped<InvoiceFlowAI.Application.Mail.IMailboxAccountReader>(provider =>
            provider.GetRequiredService<IMailboxAccountStore>());
        services.AddScoped<IRuleSetBootstrapStore, EfRuleSetBootstrapStore>();
        services.AddScoped<RuleSetValidator>();

        services.AddScoped<IDesktopActionService, AvaloniaDesktopActionService>();

        configureServices?.Invoke(services);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try
        {
            InitializeAsync(serviceProvider, legacySnapshotPath).GetAwaiter().GetResult();
            return serviceProvider;
        }
        catch
        {
            serviceProvider.Dispose();
            throw;
        }
    }

    private static async Task InitializeAsync(IServiceProvider serviceProvider, string legacySnapshotPath)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        await using (var unitOfWork = await unitOfWorkFactory
            .BeginAsync(TransactionPurpose.RulesetBootstrap, CancellationToken.None)
            .ConfigureAwait(false))
        {
            var bootstrapper = new RuleSetBootstrapper(
                scope.ServiceProvider.GetRequiredService<IRuleSetBootstrapStore>(),
                scope.ServiceProvider.GetRequiredService<RuleSetValidator>());
            await bootstrapper.EnsureDefaultInTransactionAsync(unitOfWork, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var importer = new LegacySettingsSnapshotImporter(
            legacySnapshotPath,
            scope.ServiceProvider.GetRequiredService<IUserSettingsSnapshotStore>(),
            scope.ServiceProvider.GetRequiredService<IUserSettingsStore>());
        await importer.ImportIfPresentAsync(CancellationToken.None).ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<InvoiceFlowAI.Application.Archive.IArchiveRecoveryService>()
            .ReconcileAllKnownRootsAsync(CancellationToken.None).ConfigureAwait(false);
    }
}