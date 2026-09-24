// Parser pipeline orchestrator. Selects candidate parsers by SourceKind
// and, when multiple parsers claim the same document, surfaces a
// parser-conflict candidate failure that the run barrier routes to
// NeedsManualReview (not Failed). A single-parser path is the normal
// case; conflict + missing-field are the two non-success terminal
// reasons the pipeline emits.

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Parsers;

public interface IParserPipeline
{
    Task<ParserPipelineOutcome> RunAsync(ParserWorkItem workItem, CancellationToken cancellationToken);
}

public sealed record ParserPipelineOutcome(
    ParserOutcome? Selected,
    ParserConflictOutcome? Conflict,
    IReadOnlyList<ParserOutcome> AllOutcomes);

public sealed record ParserConflictOutcome(
    string ReasonCode,
    IReadOnlyList<ParserOutcome> Candidates,
    string Resolution,
    bool RequiresManualReview);

public sealed class ParserPipeline : IParserPipeline
{
    private readonly IParserRegistry _registry;

    public ParserPipeline(IParserRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<ParserPipelineOutcome> RunAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        var candidates = _registry.ResolveBySourceKind(workItem.SourceKind)
            .Where(parser => parser.CanParse(workItem))
            .ToList();
        if (candidates.Count == 0)
        {
            return new ParserPipelineOutcome(
                Selected: null,
                Conflict: new ParserConflictOutcome(
                    ReasonCode: "PARSER_NOT_FOUND",
                    Candidates: Array.Empty<ParserOutcome>(),
                    Resolution: "manual_review",
                    RequiresManualReview: true),
                AllOutcomes: Array.Empty<ParserOutcome>());
        }

        // Deterministic order: priority desc, then parserId asc.
        var ordered = candidates
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.ParserId, StringComparer.Ordinal)
            .ToList();

        var highestPriority = ordered[0].Priority;
        var highestPriorityParsers = ordered
            .TakeWhile(parser => parser.Priority == highestPriority)
            .ToList();
        if (highestPriorityParsers.Count > 1)
        {
            var matchingOutcomes = highestPriorityParsers
                .Select(parser => new ParserOutcome(
                    parser.ParserId,
                    parser.Version,
                    Invoice: null,
                    MissingFields: Array.Empty<string>(),
                    Failure: null,
                    Disposition: ParserOutcomeDisposition.Failed))
                .ToList();
            return new ParserPipelineOutcome(
                Selected: null,
                Conflict: new ParserConflictOutcome(
                    ReasonCode: "SPECIAL_PARSER_CONFLICT",
                    Candidates: matchingOutcomes,
                    Resolution: "manual_review",
                    RequiresManualReview: true),
                AllOutcomes: matchingOutcomes);
        }

        var selected = await highestPriorityParsers[0]
            .ParseAsync(workItem, cancellationToken)
            .ConfigureAwait(false);
        return new ParserPipelineOutcome(Selected: selected, Conflict: null, AllOutcomes: new[] { selected });
    }
}
