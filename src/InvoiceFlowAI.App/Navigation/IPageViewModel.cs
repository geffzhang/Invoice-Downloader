namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 页面契约。实现不得从 <see cref="OnActivatedAsync"/> / <see cref="OnDeactivatedAsync"/>
/// 中发起导航:由导航触发的回调持有导航锁,该场景会立即抛出
/// <see cref="InvalidOperationException"/>。
/// </summary>
public interface IPageViewModel
{
    string Key { get; }
    string Title { get; }
    string Eyebrow { get; }
    Task OnActivatedAsync(CancellationToken cancellationToken);
    Task OnDeactivatedAsync(CancellationToken cancellationToken);
}
