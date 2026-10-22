using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public static class AppRpcComposition
{
    private static readonly string[] V1Methods =
    [
        RpcDispatcher.HelloMethod,
        "run.results.get",
        "run.report.export",
        "run.folder.open",
        "run.manual-review.open",
        "run.file.open",
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
        dispatcher.RegisterHandler("run.results.get", new ScopedRpcHandler<RunResultsGetRpcHandler>(scopeFactory, "run.results.get"));
        dispatcher.RegisterHandler("run.report.export", new ScopedRpcHandler<ReportExportRpcHandler>(scopeFactory, "run.report.export"));
        dispatcher.RegisterHandler("run.folder.open", new ScopedRpcHandler<RunFolderOpenRpcHandler>(scopeFactory, "run.folder.open"));
        dispatcher.RegisterHandler("run.manual-review.open", new ScopedRpcHandler<ManualReviewFolderOpenRpcHandler>(scopeFactory, "run.manual-review.open"));
        dispatcher.RegisterHandler("run.file.open", new ScopedRpcHandler<RunFileOpenRpcHandler>(scopeFactory, "run.file.open"));
        return dispatcher;
    }

}