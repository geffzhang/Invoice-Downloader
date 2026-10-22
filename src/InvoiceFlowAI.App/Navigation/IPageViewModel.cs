namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 页面契约。生命周期回调执行期间持有导航锁,因此实现不得从
/// <see cref="OnActivatedAsync"/> / <see cref="OnDeactivatedAsync"/> 中发起导航。
/// </summary>
public interface IPageViewModel
{
    string Key { get; }
    string Title { get; }
    string Eyebrow { get; }
    Task OnActivatedAsync(CancellationToken cancellationToken);
    Task OnDeactivatedAsync(CancellationToken cancellationToken);
}
