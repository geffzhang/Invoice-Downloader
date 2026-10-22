namespace InvoiceFlowAI.App.ViewModels;

/// <summary>
/// 处理中心的固定产品文案。后端阶段码、失败码与英文消息一律在此翻译,
/// 不进入任何绑定属性。
/// </summary>
public static class ProcessingCopy
{
    public const string Title = "处理中心";
    public const string Eyebrow = "Step 2";
    public const string Waiting = "等待任务开始...";
    public const string Running = "正在处理...";
    public const string StopRequested = "已收到安全停止指令,正在收尾...";
    public const string Completed = "处理完成";
    public const string Failed = "处理失败";
    public const string Cancelled = "任务已停止";
    public const string RunUnavailable = "任务记录不可用,请返回重新开始。";
    public const string LoadFailed = "进度加载失败,请稍后重试。";
    public const string StopAccepted = "已收到安全停止指令,正在收尾...";
    public const string StopRejected = "当前任务已无法停止。";
    public const string StopFailed = "停止指令发送失败。";
    public const string QuotaWarning = "大模型额度已用尽，请补充额度后重新运行。";
    public const string UnknownFailure = "本次处理出现异常,详情已写入日志。";
    public const string ProviderAuthFailure = "API Key 验证失败,请更新后重试。";
    public const string PipelineIncomplete = "本次处理未能完成,请查看结果或重新运行。";
    public const string MailboxLoginFailure = "邮箱授权码无效或已过期,请更新授权码后重试。";
    public const string MailboxConnectionFailure = "无法连接邮箱服务器,请检查网络后重试。";
    public const string MailboxFetchFailure = "部分邮件读取失败,已跳过并记录。";
    public const string ReadyLog = "已连接任务进度。";
    public const string CompletedLog = "处理完成。";
    public const string FailedLog = "处理失败。";
    public const string CancelledLog = "任务已停止。";

    public static string MapStage(string stage) => stage switch
    {
        "validate-request" => "正在校验任务参数...",
        "scan-mailbox" => "正在拉取邮件...",
        "collect-candidates" => "正在整理票据候选...",
        "recover-urls" => "正在补全票据链接...",
        "extract-documents" => "正在识别票据...",
        "pair-artifacts" => "正在复核与配对...",
        "manual_review" => "正在生成待复核记录...",
        "archive-documents" => "正在归档票据...",
        "export-report" => "正在生成结果...",
        _ => Running,
    };

    public static string MapFailureCode(string? failureCode) => failureCode switch
    {
        "IMAP_LOGIN_FAILED" => MailboxLoginFailure,
        "IMAP_CONNECTION_FAILED" => MailboxConnectionFailure,
        "IMAP_MESSAGE_FETCH_FAILED" => MailboxFetchFailure,
        "PROVIDER_AUTH_FAILED" => ProviderAuthFailure,
        "QUOTA_EXHAUSTED" => QuotaWarning,
        "RUN_PIPELINE_INCOMPLETE" or "RUN_EXECUTION_FAILED" => PipelineIncomplete,
        _ => UnknownFailure,
    };
}
