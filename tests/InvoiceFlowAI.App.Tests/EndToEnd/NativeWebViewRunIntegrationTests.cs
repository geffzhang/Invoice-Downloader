using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ClosedXML.Excel;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.WebView;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using InvoiceFlowAI.Infrastructure.Mail;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.EndToEnd;

public sealed class NativeWebViewRunIntegrationTests
{
    [NativeWebViewFact]
    public async Task Packaged_page_runs_empty_mailbox_exports_report_and_opens_it_through_host()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-native-run-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(dataDirectory, "output");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new NativeRunWebViewTestContext(dataDirectory, outputDirectory, completion);
        NativeRunWebViewTestContext.Current = context;

        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<NativeWebViewRunIntegrationApplication>()
                    .UsePlatformDetect()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime([]);
                completion.TrySetException(new InvalidOperationException("Desktop lifetime exited before the run completed."));
            }
            catch (Exception exception)
            {
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
                result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Native WebView run test did not complete.", exception);
            }

            var failureMessage = result.TryGetProperty("message", out var message)
                ? message.GetString()
                : "no failure message";
            result.GetProperty("ok").GetBoolean().Should().BeTrue(failureMessage);
            result.GetProperty("start").GetProperty("accepted").GetBoolean().Should().BeTrue();
            result.GetProperty("progress").GetProperty("isRunning").GetBoolean().Should().BeFalse();
            var persistedSummary = result.GetProperty("results").GetProperty("summary");
            persistedSummary.ValueKind.Should().Be(JsonValueKind.Object);
            result.GetProperty("export").GetProperty("reportPath").GetString().Should().EndWith("report.xlsx");
            result.GetProperty("open").GetProperty("succeeded").GetBoolean().Should().BeTrue();

            var events = result.GetProperty("events").EnumerateArray().ToArray();
            events.Should().NotBeEmpty();
            events[^1].GetProperty("event").GetString().Should().Be("run.terminal");
            var sequences = events.Select(item => item.GetProperty("eventSequence").GetInt64()).ToArray();
            sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(value => (long)value));
            events[^1].GetProperty("payload").GetProperty("runState").GetString()
                .Should().Be("completed");

            result.GetRawText().Should().NotContain(NativeRunWebViewTestContext.SecretFixture);
            result.GetRawText().Should().NotContain("token=");
            context.PathLauncher.OpenedPath.Should().NotBeNull();
            File.Exists(context.PathLauncher.OpenedPath).Should().BeTrue();
            using var workbook = new XLWorkbook(context.PathLauncher.OpenedPath!);
            workbook.Worksheets.Select(sheet => sheet.Name)
                .Should().Equal("Summary", "Invoices", "Manual Reviews");
            workbook.Worksheet("Summary").Cell("B5").GetValue<int>()
                .Should().Be(persistedSummary.GetProperty("success_count").GetInt32());
            workbook.Worksheet("Summary").Cell("B6").GetValue<int>()
                .Should().Be(persistedSummary.GetProperty("manual_check_count").GetInt32());
            (workbook.Worksheet("Invoices").RowsUsed().Count() - 1)
                .Should().Be(persistedSummary.GetProperty("success_count").GetInt32());
            (workbook.Worksheet("Manual Reviews").RowsUsed().Count() - 1)
                .Should().Be(persistedSummary.GetProperty("manual_check_count").GetInt32());
        }
        finally
        {
            if (context.Lifetime is not null)
            {
                try { await Dispatcher.UIThread.InvokeAsync(() => context.Lifetime.TryShutdown()); }
                catch (OperationCanceledException) { }
            }
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5)));
            NativeRunWebViewTestContext.Current = null;
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }
}

internal sealed class NativeRunWebViewTestContext(
    string dataDirectory,
    string outputDirectory,
    TaskCompletionSource<JsonElement> completion)
{
    public const string RunId = "native-webview-empty-run";
    public const string AccountId = "native-webview-account";
    public const string SecretFixture = "native-webview-secret-fixture";
    public static NativeRunWebViewTestContext? Current { get; set; }
    public string DataDirectory { get; } = dataDirectory;
    public string OutputDirectory { get; } = outputDirectory;
    public TaskCompletionSource<JsonElement> Completion { get; } = completion;
    public RecordingDesktopPathLauncher PathLauncher { get; } = new();
    public IClassicDesktopStyleApplicationLifetime? Lifetime { get; set; }
}

internal sealed class NativeWebViewRunIntegrationApplication : Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var context = NativeRunWebViewTestContext.Current
                ?? throw new InvalidOperationException("Native WebView run test context was not initialized.");
            context.Lifetime = desktop;
            _serviceProvider = AppServiceProviderFactory.Create(context.DataDirectory, services =>
            {
                services.AddSingleton<IMailboxSessionFactory, EmptyMailboxSessionFactory>();
                services.AddSingleton<InvoiceFlowAI.App.Desktop.IDesktopPathLauncher>(context.PathLauncher);
            });
            var dispatcher = AppRpcComposition.CreateDispatcher(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(), "native-run-test");
            var webView = new NativeWebView();
            var bridge = new WebViewRpcBridge(dispatcher, new AvaloniaNativeWebViewMessageChannel(webView), new WebViewRpcBridgeOptions());
            _serviceProvider.GetRequiredService<WebViewRunEventPublisher>().Attach(bridge);
            var window = new Window { Width = 1000, Height = 700, Content = webView };
            webView.WebMessageReceived += (_, args) =>
            {
                HandleResult(args.Body, context, desktop);
            };
            webView.NavigationCompleted += (_, _) => _ = StartRunScenarioAsync(webView, context);
            window.Closed += async (_, _) =>
            {
                if (_serviceProvider is not null)
                {
                    await _serviceProvider.DisposeAsync();
                }
            };
            window.Loaded += (_, _) =>
            {
                bridge.Start();
                var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
                webView.Source = new Uri(indexPath);
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartRunScenarioAsync(NativeWebView webView, NativeRunWebViewTestContext context)
    {
        const string script = """
            (function runScenario() {
                if (!window.RpcClient || !window.invoiceFlowRpcReady) {
                    window.setTimeout(runScenario, 100);
                    return;
                }
                window.RpcClient.init(window, { timeoutMs: 10000 });
                window.invoiceFlowRpcReady.then(async function () {
                    const events = [];
                    const capture = envelope => events.push({
                        event: envelope.event,
                        runId: envelope.runId,
                        eventSequence: envelope.eventSequence,
                        emittedAtUtc: envelope.emittedAtUtc,
                        payload: envelope.payload
                    });
                    window.RpcClient.on("run.progress", capture);
                    window.RpcClient.on("run.terminal", capture);
                    await window.RpcClient.handshake();
                    await window.RpcClient.call("account.save", {
                        account: {
                            accountId: "native-webview-account",
                            emailAddress: "native@example.invalid",
                            imapHost: "mail.invalid",
                            imapPort: 993,
                            useTls: true,
                            credentialName: "mail.imap.auth-code",
                            displayName: "Native Test"
                        },
                        expectedRevision: 0
                    });
                    await window.RpcClient.call("settings.update", {
                        expectedRevision: 1,
                        accountId: "native-webview-account",
                        companyName: "Native Run Test",
                        lastOutputDirectory: OUTPUT_DIRECTORY
                    });
                    await window.RpcClient.call("secret.set", {
                        name: "mail.imap.auth-code",
                        value: "SECRET_FIXTURE",
                        retention: "Session"
                    });
                    const start = await window.RpcClient.call("run.start", {
                        runId: "native-webview-empty-run",
                        accountId: "native-webview-account",
                        dateFrom: "2026-09-01",
                        dateTo: "2026-09-24",
                        outputDirectory: OUTPUT_DIRECTORY,
                        companyName: "Native Run Test",
                        runMode: "download"
                    });
                    let progress;
                    const deadline = Date.now() + 30000;
                    do {
                        progress = await window.RpcClient.call("run.progress.get", { runId: "native-webview-empty-run" });
                        if (!progress.isRunning) break;
                        await new Promise(resolve => window.setTimeout(resolve, 100));
                    } while (Date.now() < deadline);
                    if (progress.isRunning) throw new Error("Run did not reach a terminal state.");
                    const results = await window.RpcClient.call("run.results.get", { runId: "native-webview-empty-run" });
                    const exported = await window.RpcClient.call("run.report.export", { runId: "native-webview-empty-run" });
                    const opened = await window.RpcClient.call("run.file.open", {
                        runId: exported.runId,
                        reportPath: exported.reportPath,
                        contentHash: exported.contentHash
                    });
                    window.RpcClient.off("run.progress", capture);
                    window.RpcClient.off("run.terminal", capture);
                    window.chrome.webview.postMessage(JSON.stringify({
                        kind: "invoiceflow-native-webview-run-result",
                        ok: true,
                        start,
                        progress,
                        results,
                        export: exported,
                        open: opened,
                        events
                    }));
                }).catch(function (error) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        kind: "invoiceflow-native-webview-run-result",
                        ok: false,
                        message: String(error && error.message || "Run RPC failed")
                    }));
                });
            })();
            """;
        var scenario = script
            .Replace("OUTPUT_DIRECTORY", JsonSerializer.Serialize(context.OutputDirectory), StringComparison.Ordinal)
            .Replace("SECRET_FIXTURE", NativeRunWebViewTestContext.SecretFixture, StringComparison.Ordinal);
        try { await webView.InvokeScript(scenario); }
        catch (Exception exception)
        {
            context.Completion.TrySetException(exception);
        }
    }

    private static void HandleResult(
        string? body,
        NativeRunWebViewTestContext context,
        IClassicDesktopStyleApplicationLifetime lifetime)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind)) return;
            if (kind.GetString() != "invoiceflow-native-webview-run-result") return;
            context.Completion.TrySetResult(root.Clone());
            lifetime.TryShutdown();
        }
        catch (JsonException)
        {
        }
    }
}

internal sealed class EmptyMailboxSessionFactory : IMailboxSessionFactory
{
    public IMailboxSession Create() => new EmptyMailboxSession();

    private sealed class EmptyMailboxSession : IMailboxSession
    {
        public Task ConnectAsync(MailboxConnectionSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string userName, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IdentifyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<MailboxSessionInfo> OpenReadOnlyAsync(string mailboxName, CancellationToken cancellationToken)
            => Task.FromResult(new MailboxSessionInfo(1));
        public Task<IReadOnlyList<MailboxFetchedMessage>> SearchAsync(MailboxSearchCriteria criteria, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MailboxFetchedMessage>>(Array.Empty<MailboxFetchedMessage>());
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed class RecordingDesktopPathLauncher : InvoiceFlowAI.App.Desktop.IDesktopPathLauncher
{
    public string? OpenedPath { get; private set; }

    public Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenedPath = path;
        return Task.CompletedTask;
    }
}
