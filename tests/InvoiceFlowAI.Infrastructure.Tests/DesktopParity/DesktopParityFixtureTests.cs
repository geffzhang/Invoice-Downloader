using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Pairing;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.DesktopParity;

public sealed class DesktopParityFixtureTests
{
    [Fact]
    public void Pairing_golden_fixtures_match_python_desktop_decisions()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "pairing.json");
        var fixtures = JsonSerializer.Deserialize<PairingFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Pairing parity fixtures are empty.");
        var engine = new PairingEngine();

        foreach (var fixture in fixtures.Cases)
        {
            var result = engine.Pair(
                Enum.Parse<PairingFamily>(fixture.Family, ignoreCase: true),
                fixture.Invoices.Select(document => document.ToDomain(PairingRoleFor(fixture.Family, invoice: true))).ToArray(),
                fixture.Companions.Select(document => document.ToDomain(PairingRoleFor(fixture.Family, invoice: false))).ToArray());

            result.Pairs.Select(pair => new[] { pair.Invoice.Id, pair.Companion.Id })
                .Should().BeEquivalentTo(fixture.ExpectedPairs, options => options.WithStrictOrdering(), fixture.CaseId);
            result.Ambiguities.Should().HaveCount(fixture.ExpectedAmbiguityCount, fixture.CaseId);
        }
    }

    private static PairingRole PairingRoleFor(string family, bool invoice) =>
        (family.ToLowerInvariant(), invoice) switch
        {
            ("ride", true) => PairingRole.RideInvoice,
            ("ride", false) => PairingRole.RideItinerary,
            ("hotel", true) => PairingRole.HotelInvoice,
            ("hotel", false) => PairingRole.HotelFolio,
            _ => throw new InvalidDataException($"Unsupported fixture family '{family}'."),
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PairingFixtureSet(IReadOnlyList<PairingCase> Cases);

    private sealed record PairingCase(
        string CaseId,
        string Family,
        IReadOnlyList<PairingDocumentFixture> Invoices,
        IReadOnlyList<PairingDocumentFixture> Companions,
        IReadOnlyList<string[]> ExpectedPairs,
        int ExpectedAmbiguityCount);

    private sealed record PairingDocumentFixture(
        string Id,
        decimal? Amount,
        DateOnly? BusinessDate,
        string Provider,
        IReadOnlyList<string> MerchantTokens,
        string SourceMessageUid)
    {
        public PairingDocument ToDomain(PairingRole role) => new(
            Id,
            role,
            Amount,
            BusinessDate,
            Provider,
            MerchantTokens,
            SourceMessageUid,
            string.Empty);
    }
}
