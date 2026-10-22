namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 过渡期占位页面:内容由嵌入模式的 WebView 承载,原生侧只负责标题与路由。
/// 每迁移一页,对应注册改用原生 ViewModel,Task 12 删除本类型。
/// </summary>
public sealed class WebPageViewModel(string key, string title, string eyebrow, string path) : IPageViewModel
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Eyebrow { get; } = eyebrow;
    public string Path { get; } = path;

    public Task OnActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnDeactivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
