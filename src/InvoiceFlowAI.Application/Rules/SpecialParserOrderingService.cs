using InvoiceFlowAI.Contracts.Parsers;

namespace InvoiceFlowAI.Application.Rules;

/// <summary>Deterministic ordering: <c>Priority DESC, ParserId ASC</c>.</summary>
public sealed class SpecialParserOrderingService
{
    public IReadOnlyList<ParserDefinition> Order(ParserRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Parsers
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.ParserId, StringComparer.Ordinal)
            .ToArray();
    }
}