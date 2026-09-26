namespace InvoiceFlowAI.Contracts.Recipe;

public sealed record RecipeExecutionPolicy(
    int ControlCapacity,
    int MailboxBatchCapacity,
    int CandidateBatchCapacity,
    int ExtractionCapacity,
    int ResultCapacity,
    int EventCapacity,
    int ImapConcurrency,
    int OcrPageConcurrency,
    int OcrLineWorkers,
    int DeepSeekConcurrency,
    int BrowserConcurrency,
    int ArchiveConcurrency,
    int SqliteWriterConcurrency,
    int MaxInFlightCandidates,
    int MaxReorderItems,
    int MaxRetryAttempts,
    int NodeTimeoutSeconds,
    int RetryGapTimeoutSeconds);