using FluentAssertions;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pairing;

public sealed class ArtifactPairingStageTests
{
    [Fact]
    public void Application_registration_provides_pairing_stage()
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IArtifactPairingStage>()
            .Should()
            .BeOfType<ArtifactPairingStage>();
    }

    [Fact]
    public async Task Projects_all_supported_roles_and_pairs_unique_documents()
    {
        var stage = new ArtifactPairingStage(new PairingEngine());
        var rideInvoice = CreateResult("ride-invoice", InvoiceDocumentType.RideInvoice, 100m, "INBOX", "ride-uid");
        var rideItinerary = CreateResult("ride-itinerary", InvoiceDocumentType.RideItinerary, 100m, "INBOX", "ride-uid");
        var hotelInvoice = CreateResult("hotel-invoice", InvoiceDocumentType.HotelInvoice, 500m, "INBOX", "hotel-uid");
        var hotelFolio = CreateResult("hotel-folio", InvoiceDocumentType.HotelFolio, 500m, "INBOX", "hotel-uid");

        var batch = await stage.ExecuteAsync(
            new ExtractionBatch(new[] { rideInvoice, rideItinerary, hotelInvoice, hotelFolio }),
            CancellationToken.None);

        batch.Results.Should().HaveCount(2);
        batch.Results.Should().OnlyContain(result => result.Pairs.Count == 1);
        batch.CandidateResults.Should().OnlyContain(result => result.Status == CandidateStatus.Resolved);
        batch.Results.SelectMany(result => result.Pairs)
            .SelectMany(pair => new[] { pair.Invoice.Role, pair.Companion.Role })
            .Should()
            .Contain(new[]
            {
                PairingRole.RideInvoice,
                PairingRole.RideItinerary,
                PairingRole.HotelInvoice,
                PairingRole.HotelFolio,
            });
    }

    [Fact]
    public async Task Unmatched_pairable_documents_become_manual_review()
    {
        var stage = new ArtifactPairingStage(new PairingEngine());
        var input = new ExtractionBatch(new[]
        {
            CreateResult("unmatched-ride", InvoiceDocumentType.RideInvoice, 100m, "INBOX", "uid-1"),
            CreateResult("unmatched-hotel", InvoiceDocumentType.HotelFolio, 500m, "INBOX", "uid-2"),
        });

        var batch = await stage.ExecuteAsync(input, CancellationToken.None);

        batch.CandidateResults.Should().OnlyContain(result => result.Status == CandidateStatus.ManualReview);
        batch.CandidateResults.Select(result => result.Failure!.ReasonCode)
            .Should()
            .OnlyContain(reason => reason == "PAIRING_UNMATCHED");
        batch.Results.Should().HaveCount(2);
        batch.Results.SelectMany(result => result.UnmatchedInvoices.Concat(result.UnmatchedCompanions))
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task Ambiguous_component_marks_all_ambiguous_candidates_for_manual_review()
    {
        var stage = new ArtifactPairingStage(new PairingEngine());
        var input = new ExtractionBatch(new[]
        {
            CreateResult("invoice-a", InvoiceDocumentType.RideInvoice, 100m, "INBOX", "same-uid"),
            CreateResult("invoice-b", InvoiceDocumentType.RideInvoice, 100m, "INBOX", "same-uid"),
            CreateResult("itinerary-a", InvoiceDocumentType.RideItinerary, 100m, "INBOX", "same-uid"),
            CreateResult("itinerary-b", InvoiceDocumentType.RideItinerary, 100m, "INBOX", "same-uid"),
        });

        var batch = await stage.ExecuteAsync(input, CancellationToken.None);

        batch.Results.Should().ContainSingle(result => result.Ambiguities.Count == 1);
        batch.CandidateResults.Should().OnlyContain(result => result.Status == CandidateStatus.ManualReview);
        batch.CandidateResults.Select(result => result.Failure!.ReasonCode)
            .Should()
            .OnlyContain(reason => reason == "PAIRING_AMBIGUOUS");
    }

    [Fact]
    public async Task Pairing_isolated_by_mailbox_and_non_pairable_or_unresolved_results_pass_through()
    {
        var stage = new ArtifactPairingStage(new PairingEngine());
        var rideInvoice = CreateResult("mailbox-a-invoice", InvoiceDocumentType.RideInvoice, 100m, "Mailbox-A", "uid-a");
        var rideItinerary = CreateResult("mailbox-b-itinerary", InvoiceDocumentType.RideItinerary, 100m, "Mailbox-B", "uid-b");
        var train = CreateResult("train-ticket", InvoiceDocumentType.TrainTicket, 100m, "Mailbox-A", "uid-c");
        var unresolved = CreateResult("unresolved", InvoiceDocumentType.RideInvoice, 100m, "Mailbox-A", "uid-d", CandidateStatus.Unresolved);
        var input = new ExtractionBatch(new[] { rideInvoice, rideItinerary, train, unresolved });

        var batch = await stage.ExecuteAsync(input, CancellationToken.None);

        batch.Results.Should().HaveCount(2);
        batch.Results.Should().OnlyContain(result => result.Pairs.Count == 0);
        batch.CandidateResults.Single(result => result.Candidate.DocumentId.Value == "train-ticket")
            .Should().BeSameAs(train);
        batch.CandidateResults.Single(result => result.Candidate.DocumentId.Value == "unresolved")
            .Should().BeSameAs(unresolved);
        batch.CandidateResults.Where(result => result.Candidate.DocumentId.Value.StartsWith("mailbox-", StringComparison.Ordinal))
            .Should().OnlyContain(result => result.Status == CandidateStatus.ManualReview);
    }

    [Fact]
    public async Task Pairing_preserves_preflight_terminal_results_in_candidate_output()
    {
        var stage = new ArtifactPairingStage(new PairingEngine());
        var resolved = CreateResult("resolved", InvoiceDocumentType.TrainTicket, 100m, "INBOX", "uid-1");
        var terminal = CreateResult("retained", InvoiceDocumentType.Unrecognized, 0m, "INBOX", "uid-2", CandidateStatus.Retained);
        var input = new ExtractionBatch(new[] { resolved })
        {
            PreflightResults = new[] { terminal },
        };

        var batch = await stage.ExecuteAsync(input, CancellationToken.None);

        batch.CandidateResults.Should().HaveCount(2);
        batch.CandidateResults.Should().Contain(terminal);
        batch.CandidateResults.Should().Contain(resolved);
        batch.CandidateResults.Single(result => result.Candidate.DocumentId.Value == "retained")
            .Status.Should().Be(CandidateStatus.Retained);
    }

    private static CandidateProcessResult CreateResult(
        string id,
        InvoiceDocumentType type,
        decimal amount,
        string mailbox,
        string sourceUid,
        CandidateStatus status = CandidateStatus.Resolved)
    {
        var candidate = new DocumentCandidate(
            DocumentIdentity.Create(id),
            0,
            $"correlation-{id}",
            sourceUid,
            $"{id}.pdf",
            "application/pdf",
            100,
            0,
            "mime_attachment",
            Metadata: new Dictionary<string, string>
            {
                ["mailbox"] = mailbox,
                ["provider"] = "synthetic-provider",
            });
        var invoice = new InvoiceDocument(
            id,
            new DateOnly(2026, 9, 24),
            "Synthetic Purchaser",
            "Synthetic Seller",
            amount,
            null,
            amount,
            null,
            null,
            type,
            null,
            InvoiceRoute.Inbound,
            Array.Empty<InvoiceItem>(),
            $"{id}.pdf",
            $"hash-{id}");
        return new CandidateProcessResult(candidate, status, invoice, $"artifacts/{id}.pdf");
    }
}