// Verifies RpcDispatcher (design §6 / Task 9):
//   * bridge.hello handshake returns the app + version + protocol info
//   * Unknown methods (including the old pywebview.api.* surface) are
//     rejected with RPC_METHOD_UNKNOWN before any handler runs
//   * Protocol mismatch returns RPC_PROTOCOL_MISMATCH
//   * Cancellation propagates as RPC_CANCELLED
//   * Unexpected exceptions map to a stable internal-error code and
//     never leak the exception text
//   * Duplicate handler registration throws at startup

using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class RpcDispatcherTests
{
    [Fact]
    public async Task Bridge_hello_returns_app_metadata_and_registered_methods()
    {
        var dispatcher = new RpcDispatcher(NewHello());

        var response = await dispatcher.DispatchAsync(NewHelloRequest(), CancellationToken.None);

        response.Ok.Should().BeTrue();
        response.Result.Should().NotBeNull();
        var json = response.Result!.Value.GetRawText();
        json.Should().Contain("\"appName\":\"InvoiceFlowAI\"");
        json.Should().Contain("\"protocol\":\"invoiceflow.rpc.v1\"");
        json.Should().Contain("bridge.hello");
    }

    [Fact]
    public async Task Unknown_method_returns_RPC_METHOD_UNKNOWN()
    {
        var dispatcher = new RpcDispatcher(NewHello());

        var response = await dispatcher.DispatchAsync(
            NewRequest("settings.set", JsonDocument.Parse("{}").RootElement), CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("RPC_METHOD_UNKNOWN");
    }

    [Fact]
    public async Task Old_pywebview_api_method_is_rejected()
    {
        var dispatcher = new RpcDispatcher(NewHello());

        var response = await dispatcher.DispatchAsync(
            NewRequest("pywebview.api.start_pipeline", JsonDocument.Parse("{}").RootElement),
            CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("RPC_METHOD_UNKNOWN");
        response.Error.UserMessage.Should().Contain("pywebview.api.start_pipeline");
    }

    [Fact]
    public async Task Protocol_mismatch_returns_RPC_PROTOCOL_MISMATCH()
    {
        var dispatcher = new RpcDispatcher(NewHello());

        var response = await dispatcher.DispatchAsync(
            new RpcRequest<JsonElement?>("legacy.v0", "id-1", "bridge.hello", null),
            CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("RPC_PROTOCOL_MISMATCH");
    }

    [Fact]
    public async Task Registered_handler_runs_and_returns_result()
    {
        var dispatcher = new RpcDispatcher(NewHello());
        dispatcher.RegisterHandler("ping", new PingHandler());

        var response = await dispatcher.DispatchAsync(NewRequest("ping", null), CancellationToken.None);

        response.Ok.Should().BeTrue();
        response.Result!.Value.GetProperty("pong").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Handler_error_surfaces_rpc_error_envelope()
    {
        var dispatcher = new RpcDispatcher(NewHello());
        dispatcher.RegisterHandler("fail", new FailingHandler());

        var response = await dispatcher.DispatchAsync(NewRequest("fail", null), CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("BAD");
    }

    [Fact]
    public async Task Cancellation_propagates_as_RPC_CANCELLED()
    {
        var dispatcher = new RpcDispatcher(NewHello());
        dispatcher.RegisterHandler("slow", new SlowHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await dispatcher.DispatchAsync(NewRequest("slow", null), cts.Token);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("RPC_CANCELLED");
    }

    [Fact]
    public async Task Unexpected_exception_maps_to_internal_error()
    {
        var dispatcher = new RpcDispatcher(NewHello());
        dispatcher.RegisterHandler("boom", new ThrowingHandler());

        var response = await dispatcher.DispatchAsync(NewRequest("boom", null), CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be("RPC_INTERNAL_ERROR");
        response.Error.UserMessage.Should().NotContain("secret error text");
    }

    [Fact]
    public void Duplicate_handler_registration_throws()
    {
        var dispatcher = new RpcDispatcher(NewHello());
        dispatcher.RegisterHandler("ping", new PingHandler());

        var act = () => dispatcher.RegisterHandler("ping", new PingHandler());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reserved_hello_method_cannot_be_registered_as_handler()
    {
        var dispatcher = new RpcDispatcher(NewHello());

        var act = () => dispatcher.RegisterHandler("bridge.hello", new PingHandler());

        act.Should().Throw<InvalidOperationException>();
    }

    private static BridgeHelloInfo NewHello() => new(
        AppName: "InvoiceFlowAI",
        AppVersion: "1.0.0",
        BackendKind: "WebView2",
        Protocol: "invoiceflow.rpc.v1",
        RegisteredMethods: new[] { "bridge.hello" });

    private static RpcRequest<JsonElement?> NewHelloRequest() =>
        new("invoiceflow.rpc.v1", "req-hello-1", "bridge.hello", null);

    private static RpcRequest<JsonElement?> NewRequest(string method, JsonElement? param) =>
        new("invoiceflow.rpc.v1", $"req-{method}", method, param);

    private sealed class PingHandler : IRpcHandler
    {
        public string Method => "ping";
        public Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
            => Task.FromResult(new RpcHandlerResult(JsonDocument.Parse("{\"pong\":true}").RootElement, null));
    }

    private sealed class FailingHandler : IRpcHandler
    {
        public string Method => "fail";
        public Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
            => Task.FromResult(new RpcHandlerResult(null,
                new RpcError("BAD", "rpc", false, "boom", false)));
    }

    private sealed class SlowHandler : IRpcHandler
    {
        public string Method => "slow";
        public async Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new RpcHandlerResult(JsonDocument.Parse("{}").RootElement, null);
        }
    }

    private sealed class ThrowingHandler : IRpcHandler
    {
        public string Method => "boom";
        public Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("secret error text");
    }
}
