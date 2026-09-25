using System.Text.Json;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Runs;

public sealed class RunExecutionService : IDesktopRunExecutor
{
    private const int MaximumPipelineCycles = 64;
    private readonly RecipeRegistry _recipeRegistry;
    private readonly PipelineRunFactory _pipelineRunFactory;
    private readonly IRunCoordinator _coordinator;
    private readonly IRunEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;

    public RunExecutionService(
        RecipeRegistry recipeRegistry,
        PipelineRunFactory pipelineRunFactory,
        IRunCoordinator coordinator,
        IRunEventPublisher eventPublisher,
        TimeProvider timeProvider)
    {
        _recipeRegistry = recipeRegistry ?? throw new ArgumentNullException(nameof(recipeRegistry));
        _pipelineRunFactory = pipelineRunFactory ?? throw new ArgumentNullException(nameof(pipelineRunFactory));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task ExecuteAsync(RunStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<CandidateProcessResult> candidateResults = Array.Empty<CandidateProcessResult>();
        IReadOnlyList<RunFailure> finalizerFailures = Array.Empty<RunFailure>();
        RunSummary? pipelineSummary = null;
        RunFailure? runFailure = null;
        var cancellationRequested = false;
        var allCandidatesArrived = false;
        var eventSequence = 0L;
        var executionStage = "run-started-event";

        try
        {
            await CommitAndPublishAsync(
                request.RunId,
                ++eventSequence,
                "run.started",
                "initializing",
                JsonSerializer.Serialize(new { runId = request.RunId, stage = "initializing" }),
                new RunProgressPayload("initializing", 0, 1, 0),
                cancellationToken).ConfigureAwait(false);

            executionStage = "load-recipe";
            var recipe = await _recipeRegistry.LoadDefaultAsync(cancellationToken).ConfigureAwait(false);
            executionStage = "create-pipeline";
            await using (var run = await _pipelineRunFactory.CreateAsync(recipe, cancellationToken).ConfigureAwait(false))
            {
                run.Submit(new RunInput(
                    request.RunId,
                    request.DateFrom,
                    request.DateTo,
                    Path.GetFullPath(request.OutputDirectory),
                    string.Empty,
                    request.AccountId,
                    RunMode: request.RunMode));

                executionStage = "execute-pipeline";
                for (var cycle = 0; cycle < MaximumPipelineCycles && run.CompletedSummary is null; cycle++)
                {
                    await run.ExecuteStepAsync(cancellationToken).ConfigureAwait(false);
                }

                executionStage = "collect-pipeline-results";
                candidateResults = run.ArchivedBatch?.Results ?? Array.Empty<CandidateProcessResult>();
                finalizerFailures = run.ArchivedBatch?.Failures ?? Array.Empty<RunFailure>();
                pipelineSummary = run.CompletedSummary;
                runFailure = run.Failures.FirstOrDefault();

                if (run.CompletedSummary is null && runFailure is null)
                {
                    runFailure = new RunFailure(
                        request.RunId,
                        "run-execution",
                        "RUN_PIPELINE_INCOMPLETE",
                        FailureCategory.Internal,
                        Retryable: false,
                        SafeMessage: "The processing pipeline did not reach its completion stage.");
                }
                if (run.CompletedSummary is not null || runFailure is not null)
                {
                    allCandidatesArrived = true;
                }
            }

            foreach (var result in candidateResults)
            {
                executionStage = "commit-candidate-result";
                var candidatePayload = new RunCandidateEventPayload(
                    result.Candidate.DocumentId.Value,
                    result.Status.ToString(),
                    result.Failure?.ReasonCode);
                await CommitAndPublishAsync(
                    request.RunId,
                    ++eventSequence,
                    "run.candidate",
                    "archive-documents",
                    JsonSerializer.Serialize(candidatePayload),
                    candidatePayload,
                    CancellationToken.None,
                    [result]).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationRequested = true;
            allCandidatesArrived = true;
        }
        catch (Exception exception)
        {
            runFailure = new RunFailure(
                request.RunId,
                executionStage,
                "RUN_EXECUTION_FAILED",
                FailureCategory.Internal,
                Retryable: false,
                SafeMessage: "The run could not be completed.",
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name);
            allCandidatesArrived = true;
        }

        var completedAtUtc = pipelineSummary?.CompletedAtUtc ?? _timeProvider.GetUtcNow();
        var decision = await _coordinator.FinalizeAsync(
            new RunFinalizationRequest(
                request.RunId,
                cancellationRequested,
                allCandidatesArrived,
                candidateResults,
                runFailure,
                finalizerFailures,
                completedAtUtc,
                pipelineSummary?.ReportPath,
                pipelineSummary?.ReportContentHash),
            CancellationToken.None).ConfigureAwait(false);

        var terminalState = decision.Status switch
        {
            RunTerminalStatus.Cancelled => RunState.Cancelled,
            RunTerminalStatus.Failed => RunState.Failed,
            _ => RunState.Completed,
        };
        if (decision.TerminalEventSequence <= 0)
        {
            throw new InvalidOperationException("The committed terminal event sequence is missing.");
        }
        _eventPublisher.Publish(
            "run.terminal",
            new { runState = terminalState, reasonCode = decision.ReasonCode, completedAtUtc },
            request.RunId,
            decision.TerminalEventSequence,
            completedAtUtc);
    }

    private async Task CommitAndPublishAsync(
        string runId,
        long eventSequence,
        string eventType,
        string stage,
        string payloadJson,
        object payload,
        CancellationToken cancellationToken,
        IReadOnlyList<CandidateProcessResult>? candidateResults = null)
    {
        var emittedAtUtc = _timeProvider.GetUtcNow();
        await _coordinator.CommitPacketAsync(
            new PacketCommitRequest(
                runId,
                "run-execution",
                stage,
                eventSequence,
                eventType,
                payloadJson,
                emittedAtUtc,
                null,
                candidateResults ?? Array.Empty<CandidateProcessResult>()),
            cancellationToken).ConfigureAwait(false);
        _eventPublisher.Publish(eventType == "run.started" ? "run.progress" : eventType, payload, runId, eventSequence, emittedAtUtc);
    }
}