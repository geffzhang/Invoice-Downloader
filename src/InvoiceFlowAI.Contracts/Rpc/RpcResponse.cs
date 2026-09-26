namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>
/// Wire envelope for a JSON-RPC response. <c>Ok=true</c> requires a
/// populated <c>Result</c>; <c>Ok=false</c> requires a populated
/// <c>Error</c>. Both fields are mutually exclusive.
/// </summary>
public sealed record RpcResponse<TResult>(
    string Protocol,
    string Id,
    bool Ok,
    TResult? Result,
    RpcError? Error);