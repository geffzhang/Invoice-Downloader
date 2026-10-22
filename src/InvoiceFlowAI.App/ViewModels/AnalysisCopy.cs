namespace InvoiceFlowAI.App.ViewModels;

/// <summary>
/// 分析页的固定产品文案与结果摘要算法。摘要算法与现有页面保持一致,
/// 后端分组标签、失败原因与英文配额消息不直接进入 UI。
/// </summary>
public static class AnalysisCopy
{
    public const string Title = "结果分析";
    public const string Eyebrow = "Step 3";
    public const string ResultTitle = "本次处理完成";
    public const string StatusSummaryKicker = "运行结果";
    public const string SuccessLabel = "处理成功";
    public const string RetentionLabel = "暂存记录";
    public const string ManualLabel = "待人工复核";
    public const string SuccessHelper = "归档文件已写入输出目录。";
    public const string SuccessHelperEmpty = "当前还没有成功记录。";
    public const string RetentionHelper = "系统已保全但未纳入成功归档，可按需导出查看。";
    public const string RetentionHelperEmpty = "当前没有暂存记录。";
    public const string ManualHelper = "这是当前需要优先处理的内容。";
    public const string ManualHelperEmpty = "当前没有待人工复核项。";
    public const string QuotaWarning = "大模型额度已用尽，请补充额度后重新运行。";
    public const string ManualReviewHint = "请优先前往待人工复核文件夹处理这些记录。";
    public const string ManualReviewButton = "打开待人工复核";
    public const string OutputEyebrow = "输出目录";
    public const string OutputTitle = "归档结果与暂存记录已写入输出目录";
    public const string OutputTitleEmpty = "当前还没有可打开的输出目录";
    public const string OutputHint = "成功归档文件、暂存记录和导出结果都可以从这里继续查看。";
    public const string OutputHintEmpty = "待本轮生成结果后，可从这里直接打开输出目录。";
    public const string OutputButton = "打开输出目录";
    public const string ExportButton = "导出结果明细";
    public const string ExportingButton = "导出中...";
    public const string StartNewBatchButton = "开始新批次";
    public const string LastExportPrefix = "最近导出:";
    public const string RunUnavailable = "结果记录不可用，请先完成一次处理。";
    public const string LoadFailed = "结果加载失败，请稍后重试。";
    public const string ExportFailed = "导出失败，请稍后重试。";
    public const string ExportOpenedFailed = "报表已导出，但打开文件失败。";
    public const string OpenFailed = "打开失败，请稍后重试。";
    public const string UnknownGroupLabel = "待处理记录";

    public static string MapGroupLabel(string key) => key switch
    {
        "retained_record" => RetentionLabel,
        "manual_review" => ManualLabel,
        "processing_error" => "真实异常",
        _ => UnknownGroupLabel,
    };

    public static string ProcessingErrorWarning(int count)
        => $"检测到 {count} 条真实异常，请通过“导出结果明细”继续排查。";

    public static string ManualReviewTitle(int count) => $"检测到 {count} 条待人工复核记录";

    public static string BuildStatusText(int successCount, int manualCheckCount, int retentionCount, int processingErrorCount)
    {
        var pendingCount = manualCheckCount + retentionCount + processingErrorCount;
        if (successCount > 0 && pendingCount == 0)
        {
            return "主要结果已经整理完成，本轮没有需要额外关注的记录。";
        }

        if (successCount > 0 && pendingCount > 0)
        {
            if (manualCheckCount > 0 && retentionCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；请先处理 {manualCheckCount} 条待人工复核记录，另有 {retentionCount} 条暂存记录与 {processingErrorCount} 条真实异常需要查看。";
            }

            if (manualCheckCount > 0 && retentionCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；请优先查看 {manualCheckCount} 条待人工复核记录，另有 {retentionCount} 条暂存记录可按需导出查看。";
            }

            if (manualCheckCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；当前需要关注 {manualCheckCount} 条待人工复核记录和 {processingErrorCount} 条真实异常。";
            }

            if (manualCheckCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；当前需要你关注的是 {manualCheckCount} 条待人工复核记录。";
            }

            if (retentionCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，另有 {retentionCount} 条暂存记录和 {processingErrorCount} 条真实异常需要查看。";
            }

            if (retentionCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，另有 {retentionCount} 条暂存记录可按需导出查看。";
            }

            if (processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，但仍有 {processingErrorCount} 条真实异常需要排查。";
            }
        }

        if (successCount == 0 && pendingCount > 0)
        {
            if (manualCheckCount > 0)
            {
                return $"本轮暂无可直接归档的记录；请先查看 {manualCheckCount} 条待人工复核记录，其余结果可按需导出查看。";
            }

            if (retentionCount > 0 && processingErrorCount > 0)
            {
                return $"本轮暂无可直接归档的记录，当前有 {retentionCount} 条暂存记录和 {processingErrorCount} 条真实异常。";
            }

            if (retentionCount > 0)
            {
                return "本轮暂无可直接归档的记录，当前结果可通过导出明细进一步查看。";
            }

            if (processingErrorCount > 0)
            {
                return $"本轮暂无可直接归档的记录，当前有 {processingErrorCount} 条真实异常需要排查。";
            }
        }

        return "本轮尚未生成可展示的结果记录。";
    }
}
