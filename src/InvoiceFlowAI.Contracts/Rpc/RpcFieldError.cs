namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>
/// Field-level error reported in <c>RpcError.Details</c>. <c>Path</c> uses
/// JSON-pointer-style paths ("params.account.emailAddress"); <c>Code</c> is
/// one of the stable error codes (e.g. <c>FIELD_REQUIRED</c>,
/// <c>FIELD_OUT_OF_RANGE</c>); <c>Message</c> is human-readable and never
/// contains secret-shaped values.
/// </summary>
public sealed record RpcFieldError(string Path, string Code, string Message);