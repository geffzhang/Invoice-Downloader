using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public static class AppRpcComposition
{
    private static readonly string[] V1Methods =
    [
        RpcDispatcher.HelloMethod,
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
        return dispatcher;
    }

}
