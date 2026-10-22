namespace InvoiceFlowAI.App.Events;

/// <summary>
/// 运行事件的 UI 侧副本。Payload 只作刷新触发器使用,页面不得解析其内容,
/// 以免把后端原始字段(状态码、英文日志)泄漏到界面。
/// </summary>
public sealed record AppRunEvent(
    string EventName,
    string RunId,
    long EventSequence,
    DateTimeOffset EmittedAtUtc,
    object Payload);
