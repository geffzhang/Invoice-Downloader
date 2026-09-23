namespace InvoiceFlowAI.Contracts.Url;

/// <summary>One row in the URL error matrix used by the recovery pipeline.</summary>
public sealed record UrlError(
    string Code,
    bool Retryable,
    bool AllowBrowserFallback,
    string CandidateScope);