using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Settings;
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
        services.AddDbContext<InvoiceFlowDbContext>(options => options.UseSqlite(
            $"Data Source={databasePath};Cache=Shared;Pooling=False"));
        services.AddSingleton<IPersistentSecretStore>(new DpapiSecretStore(secretPath, entropy));
        services.AddSingleton<IUserSettingsSnapshotStore>(new JsonUserSettingsSnapshotStore(legacySnapshotPath));
        services.AddSingleton<AvaloniaMainWindowAccessor>();
        services.AddSingleton<WebViewRunEventPublisher>();
        services.AddSingleton<IDesktopPathLauncher, ShellDesktopPathLauncher>();
        services.AddSingleton<IAvaloniaUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IWindowCommandDispatcher, AvaloniaWindowCommandDispatcher>();
        services.AddSingleton<IRunEventPublisher>(provider => provider.GetRequiredService<WebViewRunEventPublisher>());
        services.AddSingleton<IDesktopRunExecutorLeaseFactory, ScopedDesktopRunExecutorLeaseFactory>();
        services.AddSingleton<IMainWindowAccessor>(provider => provider.GetRequiredService<AvaloniaMainWindowAccessor>());
        services.AddSingleton<IDirectoryPicker, AvaloniaDirectoryPicker>();
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

        services.AddScoped<SettingsGetRpcHandler>();
        services.AddScoped<SettingsUpdateRpcHandler>();
        services.AddScoped<AccountListRpcHandler>();
        services.AddScoped<AccountSaveRpcHandler>();
        services.AddScoped<AccountTestRpcHandler>();
        services.AddScoped<ProviderTestRpcHandler>();
        services.AddScoped<SecretSetRpcHandler>();
        services.AddScoped<SecretDeleteRpcHandler>();
        services.AddScoped<DirectoryChooseRpcHandler>();
        services.AddScoped<IDesktopActionService, AvaloniaDesktopActionService>();
        services.AddScoped<RunFolderOpenRpcHandler>();
        services.AddScoped<ManualReviewFolderOpenRpcHandler>();
        services.AddScoped<RunFileOpenRpcHandler>();
        services.AddScoped<WindowMinimizeRpcHandler>();
        services.AddScoped<WindowMaximizeRpcHandler>();
        services.AddScoped<WindowCloseRpcHandler>();
        services.AddScoped<RunContextRpcHandler>();
        services.AddScoped<RunStartRpcHandler>();
        services.AddScoped<RunStatusRpcHandler>();
        services.AddScoped<RunStopRpcHandler>();
        services.AddScoped<RunResultsGetRpcHandler>();
        services.AddScoped<ReportExportRpcHandler>();

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
    }
}