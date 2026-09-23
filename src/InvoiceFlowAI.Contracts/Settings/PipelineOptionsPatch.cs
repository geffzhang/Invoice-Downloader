namespace InvoiceFlowAI.Contracts.Settings;

public sealed record PipelineOptionsPatch(
    int? MailboxScanConcurrency = null,
    int? OcrPageConcurrency = null,
    int? AiRequestConcurrency = null,
    int? BrowserConcurrency = null,
    int? MaxInFlightCandidates = null,
    int? MaxRetryAttempts = null);