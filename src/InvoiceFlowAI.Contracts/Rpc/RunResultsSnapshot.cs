namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunResultsSnapshot(
    IReadOnlyDictionary<string, int> Categories,
    IReadOnlyList<RunInvoiceResult> SuccessInvoices,
    IReadOnlyList<RunErrorInvoiceResult> ErrorInvoices,
    IReadOnlyList<RunGroupedErrorResult> GroupedErrorInvoices,
    string? ManualCheckPath,
    string? OutputPath,
    IReadOnlyDictionary<string, int> Summary,
    string? BuildIdentity,
    string? RawDateRange,
    string? ImapQueryRange,
    IReadOnlyDictionary<string, int> ResultBreakdown,
    IReadOnlyDictionary<string, int> ReasonCodeBreakdown,
    bool QuotaExhausted,
    string? QuotaMessage,
    string? LastExportPath);