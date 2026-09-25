using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class DesktopActionRpcHandlerTests
{
    [Fact]
    public async Task Folder_handlers_forward_only_run_identity_and_return_safe_result()
    {
        var actions = new FakeDesktopActionService();
        var outputHandler = new RunFolderOpenRpcHandler(actions);
        var reviewHandler = new ManualReviewFolderOpenRpcHandler(actions);

        var output = await DispatchAsync(outputHandler, new RunFolderOpenRequest("run-1"));
        var review = await DispatchAsync(reviewHandler, new RunFolderOpenRequest("run-1"));

        output.Error.Should().BeNull();
        output.Result!.Value.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        review.Error.Should().BeNull();
        actions.OpenRunId.Should().Be("run-1");
        actions.ManualReviewRunId.Should().Be("run-1");
    }

    [Fact]
    public async Task File_handler_forwards_document_or_persisted_report_identity()
    {
        var actions = new FakeDesktopActionService();
        var handler = new RunFileOpenRpcHandler(actions);
        var request = new RunFileOpenRequest("run-1", ReportPath: "reports/run-1/report.xlsx", ContentHash: "hash");

        var result = await DispatchAsync(handler, request);

        result.Error.Should().BeNull();
        actions.FileRequest.Should().BeEquivalentTo(request);
        result.Result!.Value.GetProperty("succeeded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Window_handler_binds_command_to_registered_method_and_accepts_empty_params()
    {
        var actions = new FakeDesktopActionService();
        var handler = new WindowMinimizeRpcHandler(actions);

        var result = await DispatchAsync(handler, null);

        result.Error.Should().BeNull();
        actions.WindowCommand.Should().Be("minimize");
        result.Result!.Value.GetProperty("succeeded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task File_handler_rejects_missing_typed_params_before_calling_native_service()
    {
        var actions = new FakeDesktopActionService();
        var handler = new RunFileOpenRpcHandler(actions);

        var result = await DispatchAsync(handler, null);

        result.Error!.Code.Should().Be(RpcDispatcher.InvalidParamsCode);
        actions.FileRequest.Should().BeNull();
    }

    private static Task<RpcHandlerResult> DispatchAsync<TParams, TResult>(
        TypedRpcHandler<TParams, TResult> handler,
        TParams? parameters)
        where TParams : class
    {
        var json = parameters is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(parameters, JsonOptions.Default);
        return handler.HandleAsync(
            new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, "action-test", handler.Method, json),
            CancellationToken.None);
    }

    private sealed class FakeDesktopActionService : IDesktopActionService
    {
        public string? OpenRunId { get; private set; }
        public string? ManualReviewRunId { get; private set; }
        public RunFileOpenRequest? FileRequest { get; private set; }
        public string? WindowCommand { get; private set; }

        public Task<DesktopActionResult> OpenRunFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            OpenRunId = runId;
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenManualReviewFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            ManualReviewRunId = runId;
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenFileAsync(RunFileOpenRequest request, CancellationToken cancellationToken)
        {
            FileRequest = request;
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> ExecuteWindowCommandAsync(string command, CancellationToken cancellationToken)
        {
            WindowCommand = command;
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }
    }
}
