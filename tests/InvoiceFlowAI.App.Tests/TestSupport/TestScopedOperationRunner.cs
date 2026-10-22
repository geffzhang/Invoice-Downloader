using InvoiceFlowAI.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Tests.TestSupport;

/// <summary>
/// 测试用作用域执行器:用真实容器构建作用域,但里面注册的全是假实现。
/// </summary>
public sealed class TestScopedOperationRunner(IServiceProvider provider) : IScopedOperationRunner
{
    public static TestScopedOperationRunner Create(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return new TestScopedOperationRunner(services.BuildServiceProvider());
    }

    public async Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        return await operation(scope.ServiceProvider, cancellationToken);
    }

    public async Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        await operation(scope.ServiceProvider, cancellationToken);
    }
}
