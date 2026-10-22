using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using AtomUI;
using InvoiceFlowAI.App.Bootstrap;

namespace InvoiceFlowAI.App.Tests.Headless;

/// <summary>
/// Avalonia.Headless.XUnit 12.1.4 与 xunit.v3 4.0.2 不兼容(设计 §7.2);另外
/// SetupWithoutStarting 会把 Avalonia dispatcher 绑定到调用线程,而 xunit v3 可能在
/// 不同线程上跑测试。这里用一条专用 STA 线程持有 headless 应用与调度器,所有 UI 工作
/// 通过队列在这条线程上执行。
/// </summary>
public static class AtomUiHeadlessHost
{
    private static readonly BlockingCollection<PendingWork> s_queue = new();
    private static readonly Lazy<Thread> s_thread = new(StartThread, LazyThreadSafetyMode.ExecutionAndPublication);

    public static T Run<T>(Func<T> work) => (T)RunCore(() => work())!;

    public static void Run(Action work) => RunCore(() =>
    {
        work();
        return null;
    });

    private static object? RunCore(Func<object?> work)
    {
        _ = s_thread.Value;
        var pending = new PendingWork(work);
        s_queue.Add(pending);
        return pending.Completion.Task.GetAwaiter().GetResult();
    }

    private static Thread StartThread()
    {
        var thread = new Thread(() =>
        {
            AppBuilder.Configure<AtomUiTestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .UseAtomUI(AtomUiConfiguration.Configure)
                .SetupWithoutStarting();

            foreach (var pending in s_queue.GetConsumingEnumerable())
            {
                try
                {
                    pending.Completion.SetResult(pending.Work());
                }
                catch (Exception exception)
                {
                    pending.Completion.SetException(exception);
                }
            }
        })
        {
            Name = "AtomUI headless host",
            IsBackground = true,
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        return thread;
    }

    private sealed record PendingWork(Func<object?> Work)
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
