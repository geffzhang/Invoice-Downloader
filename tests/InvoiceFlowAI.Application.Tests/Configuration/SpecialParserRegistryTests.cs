// Tests for the SpecialParserRegistry ordering + fingerprint. Per design §5
// special parsers must be ordered (Priority DESC, ParserId ASC).

using FluentAssertions;
using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Contracts.Parsers;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Configuration;

public sealed class SpecialParserRegistryTests
{
    [Fact]
    public void Parsers_are_sorted_by_priority_desc_then_parser_id_asc()
    {
        var registry = new ParserRegistry("1.0", "fixture", new[]
        {
            new ParserDefinition("foreign-invoice", "1.0", 380, new[] { "pdf", "image" }, "fixture", "FOREIGN_INVOICE_PARSER_FAILED"),
            new ParserDefinition("railway-ticket", "1.0", 400, new[] { "pdf" }, "fixture", "RAILWAY_PARSER_FAILED"),
            new ParserDefinition("accommodation-folio", "1.0", 390, new[] { "pdf" }, "fixture", "FOLIO_PARSER_FAILED"),
        });

        var ordered = new SpecialParserOrderingService().Order(registry);
        ordered.Select(p => p.ParserId).Should().Equal("railway-ticket", "accommodation-folio", "foreign-invoice");
    }

    [Fact]
    public void Registry_fingerprint_changes_when_a_parser_is_added()
    {
        var baseline = new ParserRegistry("1.0", "f", new[]
        {
            new ParserDefinition("railway-ticket", "1.0", 400, new[] { "pdf" }, "fx", "X"),
        });
        var extended = new ParserRegistry("1.0", "f", new[]
        {
            new ParserDefinition("railway-ticket", "1.0", 400, new[] { "pdf" }, "fx", "X"),
            new ParserDefinition("accommodation-folio", "1.0", 390, new[] { "pdf" }, "fx", "Y"),
        });

        var fingerprint = new SpecialParserFingerprintService();
        fingerprint.Compute(baseline).Should().NotBe(fingerprint.Compute(extended));
    }
}