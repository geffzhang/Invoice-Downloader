namespace InvoiceFlowAI.App.Services;

/// <summary>
/// 单例页面 ViewModel 访问 scoped 应用服务的唯一通道。每次操作创建一个新作用域,
/// 避免长生命周期对象持有 DbContext。
/// </summary>
public interface IScopedOperationRunner
{
    Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}
