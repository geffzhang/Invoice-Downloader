using Avalonia.Controls;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.WebView;

namespace InvoiceFlowAI.App.Views;

/// <summary>
/// 过渡期宿主:一个共享的 WebView,以嵌入模式加载 SPA。
/// 首次导航设置 Source;后续导航通过 window.__invoiceFlowHostNavigate 在页内切换,
/// JS 桥未就绪时回退为重新加载。Task 12 随 WebView 一起删除。
/// </summary>
public sealed class EmbedWebViewHost : UserControl, IDisposable
{
    private readonly NativeWebView _webView = new();
    private readonly WebViewRunEventPublisher _runEventPublisher;
    private WebViewRpcBridge? _bridge;
    private bool _loaded;
    private bool _disposed;

    public EmbedWebViewHost(IRpcDispatcher rpcDispatcher, WebViewRunEventPublisher runEventPublisher)
    {
        ArgumentNullException.ThrowIfNull(rpcDispatcher);
        _runEventPublisher = runEventPublisher ?? throw new ArgumentNullException(nameof(runEventPublisher));
        Content = _webView;

        var channel = new AvaloniaNativeWebViewMessageChannel(_webView);
        _bridge = new WebViewRpcBridge(rpcDispatcher, channel, new WebViewRpcBridgeOptions());
        _runEventPublisher.Attach(_bridge);
        _bridge.Start();
    }

    public async Task NavigateAsync(WebPageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_disposed)
        {
            return;
        }

        if (!_loaded)
        {
            _loaded = true;
            _webView.Source = BuildIndexUri(page.Key);
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                // WebView2 的 InvokeScript 返回 JSON 编码后的原始结果,数字不带引号,
                // 因此脚本返回数字而不是字符串,这里与 "1" 比较。
                var ready = await _webView.InvokeScript("window.__invoiceFlowHostNavigate ? 1 : 0");
                if (_disposed)
                {
                    return;
                }

                if (ready == "1")
                {
                    await _webView.InvokeScript(
                        $"window.__invoiceFlowHostNavigate('{page.Path.Replace("'", "\\'")}');");
                    return;
                }
            }
            catch (Exception)
            {
                // 页面尚未完成加载;继续重试,超时后回退为整页加载。
            }

            await Task.Delay(100);
            if (_disposed)
            {
                return;
            }
        }

        _webView.Source = BuildIndexUri(page.Key);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_bridge is not null)
        {
            _bridge.Stop();
            _runEventPublisher.Detach(_bridge);
            _bridge = null;
        }
    }

    private static Uri BuildIndexUri(string viewKey)
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
        return new UriBuilder(new Uri(indexPath))
        {
            Query = $"embed=1&view={Uri.EscapeDataString(viewKey)}",
        }.Uri;
    }
}
