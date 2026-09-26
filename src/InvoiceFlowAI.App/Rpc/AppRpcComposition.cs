using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public static class AppRpcComposition
{
    private static readonly string[] V1Methods =
    [
        RpcDispatcher.HelloMethod,
        "settings.get",
        "settings.update",
        "account.list",
        "account.save",
        "account.test",
        "provider.test",
        "secret.set",
        "secret.delete",
        "directory.choose",
        "run.context.get",
        "run.start",
        "run.progress.get",
        "run.stop",
        "run.results.get",
        "run.report.export",
        "run.folder.open",
        "run.manual-review.open",
        "run.file.open",
        "window.minimize",
        "window.maximize",
        "window.close",
    ];

    public static RpcDispatcher CreateDispatcher(IServiceScopeFactory scopeFactory, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentException.ThrowIfNullOrEmpty(appVersion);

        var dispatcher = new RpcDispatcher(new BridgeHelloInfo(
            "InvoiceFlowAI",
            appVersion,
            "Avalonia.NativeWebView",
            RpcDispatcher.Protocol,
            [.. V1Methods]));
        dispatcher.RegisterHandler("settings.get", new ScopedRpcHandler<SettingsGetRpcHandler>(scopeFactory, "settings.get"));
        dispatcher.RegisterHandler("settings.update", new ScopedRpcHandler<SettingsUpdateRpcHandler>(scopeFactory, "settings.update"));
        dispatcher.RegisterHandler("account.list", new ScopedRpcHandler<AccountListRpcHandler>(scopeFactory, "account.list"));
        dispatcher.RegisterHandler("account.save", new ScopedRpcHandler<AccountSaveRpcHandler>(scopeFactory, "account.save"));
        dispatcher.RegisterHandler("account.test", new ScopedRpcHandler<AccountTestRpcHandler>(scopeFactory, "account.test"));
        dispatcher.RegisterHandler("provider.test", new ScopedRpcHandler<ProviderTestRpcHandler>(scopeFactory, "provider.test"));
        dispatcher.RegisterHandler("secret.set", new ScopedRpcHandler<SecretSetRpcHandler>(scopeFactory, "secret.set"));
        dispatcher.RegisterHandler("secret.delete", new ScopedRpcHandler<SecretDeleteRpcHandler>(scopeFactory, "secret.delete"));
        dispatcher.RegisterHandler("directory.choose", new ScopedRpcHandler<DirectoryChooseRpcHandler>(scopeFactory, "directory.choose"));
        dispatcher.RegisterHandler("run.context.get", new ScopedRpcHandler<RunContextRpcHandler>(scopeFactory, "run.context.get"));
        dispatcher.RegisterHandler("run.start", new ScopedRpcHandler<RunStartRpcHandler>(scopeFactory, "run.start"));
        dispatcher.RegisterHandler("run.progress.get", new ScopedRpcHandler<RunStatusRpcHandler>(scopeFactory, "run.progress.get"));
        dispatcher.RegisterHandler("run.stop", new ScopedRpcHandler<RunStopRpcHandler>(scopeFactory, "run.stop"));
        dispatcher.RegisterHandler("run.results.get", new ScopedRpcHandler<RunResultsGetRpcHandler>(scopeFactory, "run.results.get"));
        dispatcher.RegisterHandler("run.report.export", new ScopedRpcHandler<ReportExportRpcHandler>(scopeFactory, "run.report.export"));
        dispatcher.RegisterHandler("run.folder.open", new ScopedRpcHandler<RunFolderOpenRpcHandler>(scopeFactory, "run.folder.open"));
        dispatcher.RegisterHandler("run.manual-review.open", new ScopedRpcHandler<ManualReviewFolderOpenRpcHandler>(scopeFactory, "run.manual-review.open"));
        dispatcher.RegisterHandler("run.file.open", new ScopedRpcHandler<RunFileOpenRpcHandler>(scopeFactory, "run.file.open"));
        dispatcher.RegisterHandler("window.minimize", new ScopedRpcHandler<WindowMinimizeRpcHandler>(scopeFactory, "window.minimize"));
        dispatcher.RegisterHandler("window.maximize", new ScopedRpcHandler<WindowMaximizeRpcHandler>(scopeFactory, "window.maximize"));
        dispatcher.RegisterHandler("window.close", new ScopedRpcHandler<WindowCloseRpcHandler>(scopeFactory, "window.close"));
        return dispatcher;
    }

}