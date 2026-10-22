using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AppRpcCompositionTests
{
    [Fact]
    public async Task Typed_v1_methods_are_advertised_and_legacy_task_methods_are_not()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = AppRpcComposition.CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), "3.0.0");

        dispatcher.RegisteredMethods.Should().Contain([
            "run.results.get",
            "run.report.export",
            "run.folder.open",
            "run.manual-review.open",
            "run.file.open",
        ]);
        dispatcher.RegisteredMethods.Should().NotContain("report.open");
        dispatcher.RegisteredMethods.Should().NotContain("start_processing");
        dispatcher.RegisteredMethods.Should().NotContain("run.context.get");
        dispatcher.RegisteredMethods.Should().NotContain("run.start");
        dispatcher.RegisteredMethods.Should().NotContain("run.progress.get");
        dispatcher.RegisteredMethods.Should().NotContain("run.stop");

        var hello = await dispatcher.DispatchAsync(
            new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "hello", RpcDispatcher.HelloMethod, null),
            CancellationToken.None);
        hello.Result!.Value.GetProperty("registeredMethods").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(["run.report.export", "run.file.open"]);
    }

    [Fact]
    public async Task Scoped_handler_disposes_request_scope_after_dispatch()
    {
        var services = new ServiceCollection();
        services.AddScoped<DisposablePingHandler>();
        await using var provider = services.BuildServiceProvider();
        var handler = new ScopedRpcHandler<DisposablePingHandler>(
            provider.GetRequiredService<IServiceScopeFactory>(), "ping");
        var request = new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "ping-1", "ping", null);

        var result = await handler.HandleAsync(request, CancellationToken.None);

        result.Error.Should().BeNull();
        DisposablePingHandler.DisposedCount.Should().Be(1);
    }

    private sealed class DisposablePingHandler : IRpcHandler, IAsyncDisposable
    {
        public static int DisposedCount;
        public string Method => "ping";

        public Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
            => Task.FromResult(new RpcHandlerResult(JsonDocument.Parse("true").RootElement, null));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposedCount);
            return ValueTask.CompletedTask;
        }
    }
}