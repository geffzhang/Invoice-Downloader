namespace InvoiceFlowAI.Contracts.Reports;

public sealed record ReportExportRequest(
    string RunId,
    string? ReportName = null);