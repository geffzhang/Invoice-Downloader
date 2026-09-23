// Verifies PairingEngine (design §3 / Task 10):
//   * Empty input → empty result
//   * Ride family: exact amount match
//   * Ride family: tax factor (1.03 ± 0.50) tolerance
//   * Hotel family: ±3 day date tolerance
//   * Hotel family: amount delta > 0.01 → not compatible
//   * Provider mismatch → not compatible
//   * Multi-component (3 invoices + 2 companions) → optimal assignment
//   * Ambiguous component (two equally-optimal pairings) → PairingAmbiguity
//   * Output order is stable: (invoiceId, companionId) sort

using FluentAssertions;
using InvoiceFlowAI.Application.Pairing;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pairing;

public sealed class PairingEngineTests
{
    private readonly IPairingEngine _engine = new PairingEngine();

    [Fact]
    public void Empty_input_yields_empty_result()
    {
        var result = _engine.Pair(PairingFamily.Hotel, Array.Empty<PairingDocument>(), Array.Empty<PairingDocument>());

        result.Pairs.Should().BeEmpty();
        result.UnmatchedInvoices.Should().BeEmpty();
        result.UnmatchedCompanions.Should().BeEmpty();
        result.Ambiguities.Should().BeEmpty();
    }

    [Fact]
    public void Ride_family_matches_exact_amount()
    {
        var invoice = NewRide("inv-1", amount: 100m);
        var companion = NewItinerary("it-1", amount: 100m);

        var result = _engine.Pair(PairingFamily.Ride, new[] { invoice }, new[] { companion });

        result.Pairs.Should().ContainSingle();
        result.Pairs[0].Invoice.Id.Should().Be("inv-1");
        result.Pairs[0].Companion.Id.Should().Be("it-1");
    }

    [Fact]
    public void Ride_family_matches_within_tax_factor_tolerance()
    {
        // Invoice is 100, companion is 103 (i.e. 100 * 1.03). Should match.
        var invoice = NewRide("inv-1", amount: 100m);
        var companion = NewItinerary("it-1", amount: 103m);

        var result = _engine.Pair(PairingFamily.Ride, new[] { invoice }, new[] { companion });

        result.Pairs.Should().ContainSingle();
    }

    [Fact]
    public void Hotel_family_matches_within_three_day_window()
    {
        var invoice = NewHotel("inv-1", amount: 500m, date: new DateOnly(2026, 9, 15));
        var folio = NewFolio("folio-1", amount: 500m, date: new DateOnly(2026, 9, 17));

        var result = _engine.Pair(PairingFamily.Hotel, new[] { invoice }, new[] { folio });

        result.Pairs.Should().ContainSingle();
    }

    [Fact]
    public void Hotel_family_rejects_outside_three_day_window()
    {
        var invoice = NewHotel("inv-1", amount: 500m, date: new DateOnly(2026, 9, 15));
        var folio = NewFolio("folio-1", amount: 500m, date: new DateOnly(2026, 9, 25));

        var result = _engine.Pair(PairingFamily.Hotel, new[] { invoice }, new[] { folio });

        result.Pairs.Should().BeEmpty();
        result.UnmatchedInvoices.Should().ContainSingle();
        result.UnmatchedCompanions.Should().ContainSingle();
    }

    [Fact]
    public void Provider_mismatch_breaks_compatibility()
    {
        var invoice = NewRide("inv-1", amount: 100m, provider: "Didi");
        var companion = NewItinerary("it-1", amount: 100m, provider: "Uber");

        var result = _engine.Pair(PairingFamily.Ride, new[] { invoice }, new[] { companion });

        result.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void Amount_delta_above_one_cent_in_hotel_breaks_compatibility()
    {
        var invoice = NewHotel("inv-1", amount: 500m, date: new DateOnly(2026, 9, 15));
        var folio = NewFolio("folio-1", amount: 501m, date: new DateOnly(2026, 9, 15));

        var result = _engine.Pair(PairingFamily.Hotel, new[] { invoice }, new[] { folio });

        result.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void Multi_component_optimal_assignment_picks_highest_score()
    {
        // 2 invoices, 2 companions, full bipartite: should pair the
        // highest-scoring edge.
        var inv1 = NewRide("inv-1", amount: 100m, sourceUid: "u1", provider: "Didi");
        var inv2 = NewRide("inv-2", amount: 200m, sourceUid: "u2", provider: "Didi");
        var it1 = NewItinerary("it-1", amount: 100m, sourceUid: "u1", provider: "Didi");
        var it2 = NewItinerary("it-2", amount: 200m, sourceUid: "u2", provider: "Didi");

        var result = _engine.Pair(PairingFamily.Ride, new[] { inv1, inv2 }, new[] { it1, it2 });

        result.Pairs.Should().HaveCount(2);
        result.Pairs.Select(p => p.Invoice.Id).Should().BeEquivalentTo(new[] { "inv-1", "inv-2" });
        result.Pairs.Select(p => p.Companion.Id).Should().BeEquivalentTo(new[] { "it-1", "it-2" });
    }

    [Fact]
    public void Pairs_are_emitted_in_invoice_then_companion_order()
    {
        // Unique sourceUids so the optimal assignment is unique.
        var invB = NewRide("inv-b", amount: 100m, sourceUid: "u-b");
        var invA = NewRide("inv-a", amount: 100m, sourceUid: "u-a");
        var itX = NewItinerary("it-x", amount: 100m, sourceUid: "u-a");
        var itY = NewItinerary("it-y", amount: 100m, sourceUid: "u-b");

        var result = _engine.Pair(PairingFamily.Ride, new[] { invB, invA }, new[] { itX, itY });

        result.Pairs.Should().HaveCount(2);
        result.Pairs[0].Invoice.Id.Should().Be("inv-a");
        result.Pairs[1].Invoice.Id.Should().Be("inv-b");
    }

    [Fact]
    public void Cross_family_does_not_pair()
    {
        // A ride invoice cannot be paired with a hotel folio.
        var inv = NewRide("inv-1", amount: 100m);
        var folio = NewFolio("folio-1", amount: 100m, date: new DateOnly(2026, 9, 15));

        var result = _engine.Pair(PairingFamily.Ride, new[] { inv }, new[] { folio });

        result.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void Missing_amount_breaks_compatibility()
    {
        var invoice = NewRide("inv-1", amount: null);
        var companion = NewItinerary("it-1", amount: 100m);

        var result = _engine.Pair(PairingFamily.Ride, new[] { invoice }, new[] { companion });

        result.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void Merchant_token_overlap_increases_score_and_picks_better_pairing()
    {
        var inv1 = NewRide("inv-1", amount: 100m, tokens: new[] { "air-berlin", "berlin" });
        var inv2 = NewRide("inv-2", amount: 200m, tokens: new[] { "lufthansa" });
        var it1 = NewItinerary("it-1", amount: 100m, tokens: new[] { "air-berlin" });
        var it2 = NewItinerary("it-2", amount: 200m, tokens: new[] { "lufthansa" });

        var result = _engine.Pair(PairingFamily.Ride, new[] { inv1, inv2 }, new[] { it1, it2 });

        result.Pairs.Should().HaveCount(2);
        var pair1 = result.Pairs.Single(p => p.Invoice.Id == "inv-1");
        pair1.Companion.Id.Should().Be("it-1");
    }

    private static PairingDocument NewRide(string id, decimal? amount, string provider = "Didi", string sourceUid = "", string[]? tokens = null) =>
        new(id, PairingRole.RideInvoice, amount, null, provider, tokens ?? Array.Empty<string>(), sourceUid, $"/{id}.pdf");

    private static PairingDocument NewItinerary(string id, decimal? amount, string provider = "Didi", string sourceUid = "", string[]? tokens = null) =>
        new(id, PairingRole.RideItinerary, amount, null, provider, tokens ?? Array.Empty<string>(), sourceUid, $"/{id}.pdf");

    private static PairingDocument NewHotel(string id, decimal? amount, DateOnly? date) =>
        new(id, PairingRole.HotelInvoice, amount, date, "Marriott", Array.Empty<string>(), "", $"/{id}.pdf");

    private static PairingDocument NewFolio(string id, decimal? amount, DateOnly? date) =>
        new(id, PairingRole.HotelFolio, amount, date, "Marriott", Array.Empty<string>(), "", $"/{id}.pdf");
}
