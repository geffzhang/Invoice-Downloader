namespace InvoiceFlowAI.Contracts.Reports;

public sealed record ReportExportRpcResult(
    string RunId,
    string ReportPath,
    string ContentHash,
    int InvoiceRowCount,
    int ManualReviewRowCount,
    string TemplateVersion,
    bool AlreadyExisted);