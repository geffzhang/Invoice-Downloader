namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 星型拓扑导航:页面只依赖 IAppNavigator,由 ShellViewModel 在初始化时 Attach 真正的处理器,
/// 避免页面之间互相引用造成循环依赖。
/// </summary>
public sealed class AppNavigator : IAppNavigator
{
    private Func<string, CancellationToken, Task>? _handler;

    public void Attach(Func<string, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
        => _handler is null ? Task.CompletedTask : _handler(key, cancellationToken);
}
