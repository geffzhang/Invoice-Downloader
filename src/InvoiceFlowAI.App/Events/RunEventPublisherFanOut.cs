using InvoiceFlowAI.Application.Runs;

namespace InvoiceFlowAI.App.Events;

/// <summary>
/// 过渡期发布器:同一个运行事件同时送给 WebView 桥与原生事件总线。
/// 任务 12 删除 WebView 后,本类型与其注册一并删除。
/// </summary>
public sealed class RunEventPublisherFanOut : IRunEventPublisher
{
    private readonly IReadOnlyList<IRunEventPublisher> _publishers;

    public RunEventPublisherFanOut(IEnumerable<IRunEventPublisher> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        _publishers = publishers.ToArray();
    }

    public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
    {
        foreach (var publisher in _publishers)
        {
            publisher.Publish(eventName, payload, runId, eventSequence, emittedAtUtc);
        }
    }
}
