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
        var candidates = _registry.ResolveBySourceKind(workItem.SourceKind);
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

        // Run every candidate parser. A document that is recognised by
        // multiple parsers must surface a conflict, not silently pick one.
        var outcomes = new List<ParserOutcome>(ordered.Count);
        foreach (var parser in ordered)
        {
            var outcome = await parser.ParseAsync(workItem, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
        }

        var successOutcomes = outcomes
            .Where(o => o.Failure is null && o.Invoice is not null && o.MissingFields.Count == 0)
            .ToList();
        if (successOutcomes.Count >= 2)
        {
            // Two or more parsers claim the document with complete
            // output — route to manual review so a human disambiguates.
            return new ParserPipelineOutcome(
                Selected: null,
                Conflict: new ParserConflictOutcome(
                    ReasonCode: "SPECIAL_PARSER_CONFLICT",
                    Candidates: successOutcomes,
                    Resolution: "manual_review",
                    RequiresManualReview: true),
                AllOutcomes: outcomes);
        }

        if (successOutcomes.Count == 1)
        {
            return new ParserPipelineOutcome(Selected: successOutcomes[0], Conflict: null, AllOutcomes: outcomes);
        }

        var partial = outcomes
            .Where(o => o.Failure is null && o.Invoice is not null && o.MissingFields.Count > 0)
            .OrderByDescending(o => PriorityFor(o.ParserId))
            .FirstOrDefault();
        if (partial is not null)
        {
            return new ParserPipelineOutcome(Selected: partial, Conflict: null, AllOutcomes: outcomes);
        }

        // Every parser failed: surface the highest-priority failure so
        // the run barrier records the canonical reason code.
        var firstFailure = outcomes
            .Where(o => o.Failure is not null)
            .OrderByDescending(o => PriorityFor(o.ParserId))
            .FirstOrDefault();
        return new ParserPipelineOutcome(Selected: firstFailure, Conflict: null, AllOutcomes: outcomes);
    }

    private int PriorityFor(string parserId) => _registry.Resolve(parserId)?.Priority ?? 0;
}
