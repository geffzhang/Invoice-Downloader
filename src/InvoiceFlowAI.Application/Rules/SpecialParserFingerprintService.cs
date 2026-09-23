using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Contracts.Parsers;

namespace InvoiceFlowAI.Application.Rules;

public sealed class SpecialParserFingerprintService
{
    private readonly SpecialParserOrderingService _order = new();
    private readonly ConfigurationFingerprintService _fingerprint = new();

    public string Compute(ParserRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var ordered = _order.Order(registry);
        var projected = new
        {
            registry.SchemaVersion,
            Parsers = ordered.Select(p => new
            {
                p.ParserId,
                p.Version,
                p.Priority,
                SourceKinds = p.SourceKinds.ToArray(),
                p.FixtureId,
                p.FailureCode,
            }).ToArray(),
        };
        return _fingerprint.ComputeFromObject(projected);
    }
}