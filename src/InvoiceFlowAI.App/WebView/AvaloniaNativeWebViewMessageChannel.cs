using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using InvoiceFlowAI.App.Rpc;

namespace InvoiceFlowAI.App.WebView;

public sealed class AvaloniaNativeWebViewMessageChannel : IWebViewMessageChannel
{
    private readonly NativeWebView _webView;

    public AvaloniaNativeWebViewMessageChannel(NativeWebView webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    public event Action<string>? MessageReceived;

    public void PostMessage(string payload)
    {
        var encodedPayload = JsonSerializer.Serialize(payload);
        var script = $"(window.chrome && window.chrome.webview ? window.chrome.webview : window).dispatchEvent(new MessageEvent('message', {{ data: {encodedPayload} }}));";
        Dispatcher.UIThread.Post(() => _ = _webView.InvokeScript(script));
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.Body)) MessageReceived?.Invoke(args.Body);
    }
}