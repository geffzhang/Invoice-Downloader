using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class DirectoryChooseRpcHandlerTests
{
    [Fact]
    public async Task Choose_returns_selected_normalized_path()
    {
        var handler = new DirectoryChooseRpcHandler(new FakeDirectoryPicker(new DirectoryChooseResult(false, "C:/Invoices")));

        var result = await handler.HandleAsync(NewRequest(), CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("cancelled").GetBoolean().Should().BeFalse();
        result.Result.Value.GetProperty("path").GetString().Should().Be("C:/Invoices");
    }

    [Fact]
    public async Task Choose_preserves_user_cancellation()
    {
        var handler = new DirectoryChooseRpcHandler(new FakeDirectoryPicker(new DirectoryChooseResult(true, null)));

        var result = await handler.HandleAsync(NewRequest(), CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("cancelled").GetBoolean().Should().BeTrue();
        result.Result.Value.GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static RpcRequest<JsonElement?> NewRequest()
        => new(RpcDispatcher.Protocol, "directory-1", "directory.choose", null);

    private sealed class FakeDirectoryPicker(DirectoryChooseResult result) : IDirectoryPicker
    {
        public Task<DirectoryChooseResult> ChooseAsync(CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}