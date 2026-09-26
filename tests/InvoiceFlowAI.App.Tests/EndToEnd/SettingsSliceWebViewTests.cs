using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.EndToEnd;

public sealed class SettingsSliceWebViewTests
{
    [Fact]
    public async Task Bridge_messages_complete_settings_load_and_update_round_trip()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-webview-{Guid.NewGuid():N}");
        try
        {
            await using var provider = AppServiceProviderFactory.Create(appDataDirectory);
            var dispatcher = AppRpcComposition.CreateDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(), "3.0.0");
            var channel = new RecordingChannel();
            var bridge = new WebViewRpcBridge(dispatcher, channel, new WebViewRpcBridgeOptions());
            bridge.Start();

            channel.Raise(BuildRequest("hello-1", RpcDispatcher.HelloMethod, null));
            var hello = await channel.ReadResponseAsync(0, TimeSpan.FromSeconds(5));
            hello.GetProperty("ok").GetBoolean().Should().BeTrue();
            hello.GetProperty("result").GetProperty("registeredMethods").EnumerateArray()
                .Select(method => method.GetString()).Should().Contain("settings.get");

            channel.Raise(BuildRequest("load-1", "settings.get", null));
            var loaded = await channel.ReadResponseAsync(1, TimeSpan.FromSeconds(5));
            loaded.GetProperty("ok").GetBoolean().Should().BeTrue();
            var revision = loaded.GetProperty("result").GetProperty("revision").GetInt32();

            channel.Raise(BuildRequest("update-1", "settings.update", new
            {
                expectedRevision = revision,
                companyName = "Bridge Buyer",
                lastOutputDirectory = "C:/BridgeInvoices",
            }));
            var updated = await channel.ReadResponseAsync(2, TimeSpan.FromSeconds(5));
            updated.GetProperty("ok").GetBoolean().Should().BeTrue();
            updated.GetProperty("result").GetProperty("companyName").GetString().Should().Be("Bridge Buyer");
            updated.GetProperty("result").GetProperty("lastOutputDirectory").GetString().Should().Be("C:/BridgeInvoices");

            bridge.Stop();
        }
        finally
        {
            if (Directory.Exists(appDataDirectory)) Directory.Delete(appDataDirectory, recursive: true);
        }
    }

    private static string BuildRequest(string id, string method, object? parameters)
        => JsonSerializer.Serialize(new
        {
            protocol = RpcDispatcher.Protocol,
            id,
            method,
            @params = parameters,
        }, JsonOptions.Default);

    private sealed class RecordingChannel : IWebViewMessageChannel
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public event Action<string>? MessageReceived;

        public void PostMessage(string payload) => _messages.Enqueue(payload);
        public void Raise(string payload) => MessageReceived?.Invoke(payload);

        public async Task<JsonElement> ReadResponseAsync(int index, TimeSpan timeout)
        {
            var started = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - started < timeout)
            {
                var messages = _messages.ToArray();
                if (messages.Length > index)
                {
                    using var document = JsonDocument.Parse(messages[index]);
                    return document.RootElement.Clone();
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("WebView bridge response was not received.");
        }
    }
}