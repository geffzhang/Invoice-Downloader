using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.Events;

public sealed class AppEventBus(ILogger<AppEventBus> logger) : IAppEventBus
{
    private readonly object _gate = new();
    private ImmutableArray<Action<AppRunEvent>> _handlers = [];

    public void Publish(AppRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        // ImmutableArray is a struct, so Volatile.Read cannot be used here.
        // The field is swapped under the lock and the array contents never mutate,
        // so publishing reads a consistent snapshot without taking the lock.
        var handlers = _handlers;
        foreach (var handler in handlers)
        {
            try
            {
                handler(runEvent);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "运行事件订阅者处理失败:{EventName}", runEvent.EventName);
            }
        }
    }

    public IDisposable Subscribe(Action<AppRunEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<AppRunEvent> handler)
    {
        lock (_gate)
        {
            _handlers = _handlers.Remove(handler);
        }
    }

    private sealed class Subscription(AppEventBus owner, Action<AppRunEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(handler);
            }
        }
    }
}
