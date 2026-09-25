using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public static class AppRpcComposition
{
    private static readonly string[] SettingsMethods =
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
            [.. SettingsMethods]));
        dispatcher.RegisterHandler("settings.get", new ScopedRpcHandler<SettingsGetRpcHandler>(scopeFactory, "settings.get"));
        dispatcher.RegisterHandler("settings.update", new ScopedRpcHandler<SettingsUpdateRpcHandler>(scopeFactory, "settings.update"));
        dispatcher.RegisterHandler("account.list", new ScopedRpcHandler<AccountListRpcHandler>(scopeFactory, "account.list"));
        dispatcher.RegisterHandler("account.save", new ScopedRpcHandler<AccountSaveRpcHandler>(scopeFactory, "account.save"));
        dispatcher.RegisterHandler("account.test", new ScopedRpcHandler<AccountTestRpcHandler>(scopeFactory, "account.test"));
        dispatcher.RegisterHandler("provider.test", new ScopedRpcHandler<ProviderTestRpcHandler>(scopeFactory, "provider.test"));
        dispatcher.RegisterHandler("secret.set", new ScopedRpcHandler<SecretSetRpcHandler>(scopeFactory, "secret.set"));
        dispatcher.RegisterHandler("secret.delete", new ScopedRpcHandler<SecretDeleteRpcHandler>(scopeFactory, "secret.delete"));
        dispatcher.RegisterHandler("directory.choose", new ScopedRpcHandler<DirectoryChooseRpcHandler>(scopeFactory, "directory.choose"));
        return dispatcher;
    }

}