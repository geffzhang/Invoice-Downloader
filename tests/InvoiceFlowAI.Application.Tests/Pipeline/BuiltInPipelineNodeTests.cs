using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Pipeline.Nodes;
using InvoiceFlowAI.Domain.Runs;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Graph;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class BuiltInPipelineNodeTests
{
    [Fact]
    public void Validate_request_node_exposes_the_declared_run_input_port()
    {
        var node = new ValidateRequestNode();

        node.InputPorts.Should().ContainSingle()
            .Which.DataType.Should().Be(typeof(RunInput));
        node.OutputPorts.Select(port => (port.Name, port.DataType)).Should().BeEquivalentTo(
            new[]
            {
                ("Valid", typeof(ValidatedRunInput)),
                ("Failure", typeof(RunFailure)),
            });
    }

    [Fact]
    public async Task Archive_node_preserves_run_output_root_sequence_and_final_metadata()
    {
        var pairingBatch = new PairingBatch(Array.Empty<InvoiceFlowAI.Application.Pairing.PairingResult>(), Array.Empty<InvoiceFlowAI.Domain.Candidates.CandidateProcessResult>());
        var source = new PacketSourceNode(new PipelineItem<PairingBatch>("run-context", pairingBatch, 17, true, "C:\\invoice-output"));
        var stage = new CapturingArchiveStage();
        var archive = new ArchiveDocumentsNode(stage);
        var sink = new CaptureNode<PipelineItem<ArchiveBatch>>();
        var graph = new PipelineGraph();
        graph.AddNode(source).AddNode(archive).AddNode(sink);
        graph.Connect(source.Output, archive.InputPorts.OfType<InputPort<PipelineItem<PairingBatch>>>().Single());
        graph.Connect(archive.OutputPorts.OfType<OutputPort<PipelineItem<ArchiveBatch>>>().Single(), sink.Input);

        var executor = new PipelineExecutor(graph);
        var context = new PipelineContext(CancellationToken.None);
        await executor.InitializeAsync(context, CancellationToken.None);
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await executor.ExecuteStepAsync(context, CancellationToken.None);
        }

        stage.Request.Should().NotBeNull();
        stage.Request!.RunId.Should().Be("run-context");
        stage.Request.OutputRoot.Should().Be("C:\\invoice-output");
        sink.Value.Should().NotBeNull();
        sink.Value!.RunId.Should().Be("run-context");
        sink.Value.Sequence.Should().Be(17);
        sink.Value.IsFinal.Should().BeTrue();
        sink.Value.OutputRoot.Should().Be("C:\\invoice-output");
    }

    [Fact]
    public async Task Scan_node_carries_user_save_path_as_output_root()
    {
        var request = new RunInput(
            "run-save-path",
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 2),
            "C:\\user-output",
            string.Empty,
            "account-1");
        var source = new ValidatedInputSourceNode(new ValidatedRunInput(request));
        var scan = new ScanMailboxNode(new EmptyMailboxScanner());
        var sink = new CaptureNode<PipelineItem<MailboxScanResult>>();
        var graph = new PipelineGraph();
        graph.AddNode(source).AddNode(scan).AddNode(sink);
        graph.Connect(source.Output, scan.InputPorts.OfType<InputPort<ValidatedRunInput>>().Single());
        graph.Connect(scan.OutputPorts.OfType<OutputPort<PipelineItem<MailboxScanResult>>>().Single(), sink.Input);

        var executor = new PipelineExecutor(graph);
        var context = new PipelineContext(CancellationToken.None);
        await executor.InitializeAsync(context, CancellationToken.None);
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await executor.ExecuteStepAsync(context, CancellationToken.None);
        }

        sink.Value.Should().NotBeNull();
        sink.Value!.RunId.Should().Be(request.RunId);
        sink.Value.OutputRoot.Should().Be(request.SavePath);
        sink.Value.IsFinal.Should().BeTrue();
    }

        [Fact]
        public async Task Validate_request_node_emits_typed_input_through_real_executor()
        {
            var request = new RunInput(
                "run-1",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 2),
                "output",
                string.Empty,
                "account-1");
            var source = new RunInputSourceNode(request);
            var validate = new ValidateRequestNode();
            var sink = new CaptureNode<ValidatedRunInput>();
            var graph = new PipelineGraph();
            graph.AddNode(source).AddNode(validate).AddNode(sink);
            graph.Connect(source.Output, validate.InputPorts.OfType<InputPort<RunInput>>().Single());
            graph.Connect(validate.OutputPorts.OfType<OutputPort<ValidatedRunInput>>().Single(), sink.Input);

            var executor = new PipelineExecutor(graph);
            var context = new PipelineContext(CancellationToken.None);
            await executor.InitializeAsync(context, CancellationToken.None);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await executor.ExecuteStepAsync(context, CancellationToken.None);
            }

            sink.Value.Should().Be(new ValidatedRunInput(request));
        }

        private sealed class RunInputSourceNode : PipelineNode
        {
            private readonly RunInput _request;
            private bool _sent;

            public RunInputSourceNode(RunInput request) : base("test-source", "Test source")
            {
                _request = request;
                Output = AddOutputPort<RunInput>("Output");
            }

            public OutputPort<RunInput> Output { get; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (!_sent)
                {
                    Output.Emit(_request, 1);
                    _sent = true;
                }
                return Task.CompletedTask;
            }
        }

        private sealed class CaptureNode<T> : PipelineNode
        {
            public CaptureNode() : base("test-sink", "Test sink") =>
                Input = AddInputPort<T>("Input", 1, BackpressurePolicy.Block);

            public InputPort<T> Input { get; }
            public T? Value { get; private set; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (Input.TryReceive(out var packet) && !packet.IsEndOfStream)
                {
                    Value = packet.Payload;
                }
                return Task.CompletedTask;
            }
        }

        private sealed class PacketSourceNode : PipelineNode
        {
            private readonly PipelineItem<PairingBatch> _item;
            private bool _sent;

            public PacketSourceNode(PipelineItem<PairingBatch> item) : base("archive-context-source", "Archive context source")
            {
                _item = item;
                Output = AddOutputPort<PipelineItem<PairingBatch>>("Output");
            }

            public OutputPort<PipelineItem<PairingBatch>> Output { get; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (!_sent)
                {
                    Output.Emit(_item, _item.Sequence);
                    _sent = true;
                }
                return Task.CompletedTask;
            }

        }

        private sealed class CapturingArchiveStage : IDocumentArchivingStage
        {
            public ArchiveStageRequest? Request { get; private set; }

            public Task<ArchiveBatch> ExecuteAsync(ArchiveStageRequest request, CancellationToken cancellationToken)
            {
                Request = request;
                return Task.FromResult(new ArchiveBatch(request.Batch.CandidateResults, Array.Empty<RunFailure>()));
            }
        }

        private sealed class ValidatedInputSourceNode : PipelineNode
        {
            private readonly ValidatedRunInput _input;
            private bool _sent;

            public ValidatedInputSourceNode(ValidatedRunInput input) : base("validated-input-source", "Validated input source")
            {
                _input = input;
                Output = AddOutputPort<ValidatedRunInput>("Output");
            }

            public OutputPort<ValidatedRunInput> Output { get; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (!_sent)
                {
                    Output.Emit(_input, 1);
                    _sent = true;
                }
                return Task.CompletedTask;
            }
        }

        private sealed class EmptyMailboxScanner : IMailboxScanner
        {
            public Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new MailboxScanResult(Array.Empty<InvoiceFlowAI.Domain.Runs.MailboxMessage>(), Array.Empty<MailboxAttachmentCandidate>(), 0, "uid-validity", false));
        }
}