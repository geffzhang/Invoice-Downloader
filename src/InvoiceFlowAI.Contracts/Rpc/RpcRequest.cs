namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>
/// Wire envelope for a JSON-RPC request. <c>Protocol</c> is fixed at
/// <c>invoiceflow.rpc.v1</c> for the first release. Unknown methods are
/// rejected by the dispatcher before reaching handler code.
/// </summary>
public sealed record RpcRequest<TParams>(
    string Protocol,
    string Id,
    string Method,
    TParams Params);