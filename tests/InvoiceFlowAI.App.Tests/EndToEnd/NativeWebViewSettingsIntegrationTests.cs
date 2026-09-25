using System.Text.Json;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.WebView;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.EndToEnd;

public sealed class NativeWebViewSettingsIntegrationTests
{
    [NativeWebViewFact]
    public async Task Packaged_page_handshakes_and_updates_settings_through_native_webview()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-native-webview-{Guid.NewGuid():N}");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new NativeWebViewTestContext(dataDirectory, completion);
        context.StartedAt = Stopwatch.GetTimestamp();
        NativeWebViewTestContext.Current = context;
        var traceListener = new ContextTraceListener(context);
        Trace.Listeners.Add(traceListener);
        context.Log($"interactive={Environment.UserInteractive}; webRoot={Path.Combine(AppContext.BaseDirectory, "Web")}");

        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<NativeWebViewIntegrationApplication>()
                    .UsePlatformDetect()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime([]);
                context.Log("desktop lifetime returned normally");
                completion.TrySetException(new InvalidOperationException("Desktop lifetime exited before the WebView handshake completed."));
            }
            catch (Exception exception)
            {
                context.Log($"desktop lifetime exception: {exception.GetType().Name}: {exception.Message}");
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            JsonElement result;
            try
            {
                result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Native WebView test did not complete. {context.Diagnostics}", exception);
            }
            var failureMessage = result.TryGetProperty("message", out var message)
                ? message.GetString()
                : "no failure message";
            result.GetProperty("ok").GetBoolean().Should().BeTrue(
                $"{failureMessage} | {context.Diagnostics}");
            result.GetProperty("companyName").GetString().Should().Be("Native WebView Buyer");
            result.GetProperty("lastOutputDirectory").GetString().Should().Be("C:/NativeInvoices");
        }
        finally
        {
            if (context.Lifetime is not null)
            {
                try { await Dispatcher.UIThread.InvokeAsync(() => context.Lifetime.TryShutdown()); }
                catch (OperationCanceledException) { context.Log("dispatcher stopped before test cleanup"); }
            }
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5)));
            Trace.Listeners.Remove(traceListener);
            NativeWebViewTestContext.Current = null;
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }
}

internal sealed class NativeWebViewFactAttribute : FactAttribute
{
    public NativeWebViewFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            Skip = "Requires an interactive Windows desktop.";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST"), "1", StringComparison.Ordinal))
        {
            Skip = "Set INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST=1 to run the native WebView test.";
        }
    }
}

internal sealed class ContextTraceListener(NativeWebViewTestContext context) : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) context.Log($"trace: {message.Trim()}");
    }

    public override void WriteLine(string? message) => Write(message);
}

internal sealed class NativeWebViewTestContext(
    string dataDirectory,
    TaskCompletionSource<JsonElement> completion)
{
    public static NativeWebViewTestContext? Current { get; set; }
    public string DataDirectory { get; } = dataDirectory;
    public TaskCompletionSource<JsonElement> Completion { get; } = completion;
    public IClassicDesktopStyleApplicationLifetime? Lifetime { get; set; }
    public long StartedAt { get; set; }
    private readonly List<string> _diagnostics = [];
    public string Diagnostics => string.Join(" | ", _diagnostics);
    public void Log(string message) => _diagnostics.Add($"{Stopwatch.GetElapsedTime(StartedAt).TotalMilliseconds:F0}ms {message}");
}

internal sealed class NativeWebViewIntegrationApplication : Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var context = NativeWebViewTestContext.Current
                ?? throw new InvalidOperationException("Native WebView test context was not initialized.");
            context.Log("framework initialized with desktop lifetime");
            context.Lifetime = desktop;
            _serviceProvider = AppServiceProviderFactory.Create(context.DataDirectory);
            context.Log("application services initialized");
            var dispatcher = AppRpcComposition.CreateDispatcher(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(), "native-test");
            var diagnosticDispatcher = new DiagnosticRpcDispatcher(dispatcher, context);
            var webView = new NativeWebView();
            var channel = new DiagnosticWebViewChannel(new AvaloniaNativeWebViewMessageChannel(webView), context);
            channel.MessageReceived += payload =>
            {
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    var method = document.RootElement.TryGetProperty("method", out var methodElement)
                        ? methodElement.GetString()
                        : "missing";
                    var requestId = document.RootElement.TryGetProperty("id", out var idElement)
                        ? idElement.GetString()
                        : "missing";
                    context.Log($"host received request method={method} id={requestId}");
                }
                catch (JsonException)
                {
                    context.Log("host received non-JSON message");
                }
            };
            var bridge = new WebViewRpcBridge(diagnosticDispatcher, channel,
                new WebViewRpcBridgeOptions { RequestTimeout = TimeSpan.FromSeconds(2) });
            var window = new Window { Width = 1000, Height = 700, Content = webView };
            webView.WebMessageReceived += (_, args) =>
            {
                context.Log($"web message received; length={args.Body?.Length ?? 0}");
                HandleTestResult(args.Body, context, desktop);
            };
            webView.NavigationStarted += (_, args) => context.Log($"navigation started: {args}");
            webView.NavigationCompleted += (_, args) =>
            {
                context.Log($"navigation completed: {args}");
                _ = StartSettingsRoundTripAsync(webView, context);
            };
            window.Closed += (_, _) => _serviceProvider?.Dispose();
            window.Loaded += (_, _) =>
            {
                context.Log("window loaded");
                bridge.Start();
                context.Log($"bridge started; methods={dispatcher.RegisteredMethods.Count}");
                var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
                context.Log($"setting source; exists={File.Exists(indexPath)}");
                webView.Source = new Uri(indexPath);
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartSettingsRoundTripAsync(NativeWebView webView, NativeWebViewTestContext context)
    {
        const string script = """
            (function runSettingsRoundTrip() {
                if (!window.RpcClient || !window.SettingsRpc || !window.invoiceFlowRpcReady) {
                    window.setTimeout(runSettingsRoundTrip, 100);
                    return;
                }
                window.RpcClient.init(undefined, { timeoutMs: 1500 });
                window.invoiceFlowRpcReady.then(async function () {
                    const loaded = await window.SettingsRpc.load(window.RpcClient);
                    const updated = await window.SettingsRpc.saveSettings(window.RpcClient, loaded.settings, {
                        companyName: "Native WebView Buyer",
                        lastOutputDirectory: "C:/NativeInvoices"
                    });
                    window.chrome.webview.postMessage(JSON.stringify({
                        kind: "invoiceflow-native-webview-result",
                        ok: true,
                        companyName: updated.companyName,
                        lastOutputDirectory: updated.lastOutputDirectory
                    }));
                }).catch(function (error) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        kind: "invoiceflow-native-webview-result",
                        ok: false,
                        message: String(error && error.message || "RPC failed")
                    }));
                });
            })();
            """;

        try
        {
            var invocation = await webView.InvokeScript(script);
            context.Log($"test script invocation returned: {invocation}");
        }
        catch (Exception exception)
        {
            context.Log($"test script invocation exception: {exception.GetType().Name}: {exception.Message}");
            context.Completion.TrySetException(exception);
        }
    }

    private static void HandleTestResult(
        string? body,
        NativeWebViewTestContext context,
        IClassicDesktopStyleApplicationLifetime lifetime)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind)
                || kind.GetString() != "invoiceflow-native-webview-result")
            {
                return;
            }
            context.Completion.TrySetResult(root.Clone());
            lifetime.TryShutdown();
        }
        catch (JsonException)
        {
        }
    }
}

internal sealed class DiagnosticWebViewChannel(
    IWebViewMessageChannel inner,
    NativeWebViewTestContext context) : IWebViewMessageChannel
{
    public event Action<string>? MessageReceived
    {
        add => inner.MessageReceived += value;
        remove => inner.MessageReceived -= value;
    }

    public void PostMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var errorCode = root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                ? error.GetProperty("code").GetString()
                : "none";
            context.Log($"host response id={root.GetProperty("id").GetString()} ok={root.GetProperty("ok").GetBoolean()} code={errorCode}");
        }
        catch (JsonException)
        {
            context.Log("host posted non-JSON payload");
        }
        try
        {
            inner.PostMessage(payload);
        }
        catch (Exception exception)
        {
            context.Log($"inner channel post threw {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }
}

internal sealed class DiagnosticRpcDispatcher(
    IRpcDispatcher inner,
    NativeWebViewTestContext context) : IRpcDispatcher
{
    public IReadOnlyCollection<string> RegisteredMethods => inner.RegisteredMethods;

    public void RegisterHandler(string method, IRpcHandler handler) => inner.RegisterHandler(method, handler);

    public async Task<RpcResponse<JsonElement?>> DispatchAsync(
        RpcRequest<JsonElement?> request,
        CancellationToken cancellationToken)
    {
        context.Log($"dispatcher invoked method={request.Method}");
        var response = await inner.DispatchAsync(request, cancellationToken);
        context.Log($"dispatcher returned method={request.Method} ok={response.Ok}");
        try
        {
            var serialized = JsonSerializer.Serialize(response, JsonOptions.Default);
            context.Log($"dispatcher response serialized; length={serialized.Length}");
        }
        catch (Exception exception)
        {
            context.Log($"dispatcher response serialization failed: {exception.GetType().Name}: {exception.Message}");
        }
        return response;
    }
}
