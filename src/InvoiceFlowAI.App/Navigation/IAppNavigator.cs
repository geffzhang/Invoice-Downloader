namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 页面导航入口。不要在 <see cref="IPageViewModel"/> 的生命周期回调中调用:
/// 回调期间导航锁已被持有,该场景会立即抛出 <see cref="InvalidOperationException"/>。
/// </summary>
public interface IAppNavigator
{
    Task NavigateAsync(string key, CancellationToken cancellationToken = default);
}
