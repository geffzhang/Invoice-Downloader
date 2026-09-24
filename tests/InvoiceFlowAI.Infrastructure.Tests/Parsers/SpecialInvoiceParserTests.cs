using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Parsers;
using InvoiceFlowAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class SpecialInvoiceParserTests
{
    [Fact]
    public async Task Railway_parser_claims_ticket_and_uses_departure_date()
    {
        const string text = "2026-01-23\nRailway Ticket\nInvoice Number: 26119110010001302959\nDeparture City: Beijing South\nDestination City: Jinan West\nIssue Date: 2026-02-10\nSeller: China Railway\nAmount: 202.00";
        var parser = new RailwayTicketParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.DocumentType.Should().Be(InvoiceDocumentType.TrainTicket);
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 1, 23));
        outcome.Invoice.Route!.DepartureDate.Should().Be(new DateOnly(2026, 1, 23));
        outcome.Invoice.Route.DepartureCity.Should().Be("Beijing South");
        outcome.Invoice.Route.DestinationCity.Should().Be("Jinan West");
    }

    [Fact]
    public void Railway_parser_does_not_claim_regular_vat_invoice()
    {
        var workItem = NewWorkItem(CreateTextPdf("VAT Invoice\nInvoice Number: 123456789012"), "pdf");

        new RailwayTicketParser().CanParse(workItem).Should().BeFalse();
    }

    [Fact]
    public async Task Railway_parser_precedes_generic_pdf_parser_in_dispatch()
    {
        var workItem = NewWorkItem(CreateTextPdf("Railway Ticket\nInvoice Number: 26119110010001302959"), "pdf");
        var registry = new ParserRegistry([new PdfInvoiceParser(), new RailwayTicketParser()]);

        var result = await new ParserPipeline(registry).RunAsync(workItem, CancellationToken.None);

        result.Selected!.ParserId.Should().Be("railway-ticket");
    }

    [Fact]
    public async Task Accommodation_folio_uses_checkout_date_guest_and_payment_total()
    {
        const string text = "Grand Hotel\nGuest Folio\nGuest Name: Jane Doe\nCheck-in Date: 2026-06-10\nCheck-out Date: 2026-06-11\nConsumption Total: 441.15\nPayment Total: 441.15";
        var parser = new AccommodationFolioParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.DocumentType.Should().Be(InvoiceDocumentType.HotelFolio);
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 6, 11));
        outcome.Invoice.Purchaser.Should().Be("Jane Doe");
        outcome.Invoice.Seller.Should().Be("Grand Hotel");
        outcome.Invoice.Amount.Should().Be(441.15m);
        outcome.Invoice.Flags.Should().HaveFlag(InvoiceFlags.Folio);
    }

    [Fact]
    public async Task Didi_parser_uses_tax_inclusive_total_and_classifies_ride_invoice()
    {
        const string text = "VAT Invoice\nInvoice Number: 26337000000257791609\nIssue Date: 2026-03-11\nPurchaser: Example Company\nSeller: Hangzhou Didi Chuxing Technology Co Ltd\nPassenger Transport Service\nAmount: 406.60\nTax Amount: 12.20\nTotal Amount: 418.80";
        var parser = new DidiInvoiceParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.DocumentType.Should().Be(InvoiceDocumentType.RideInvoice);
        outcome.Invoice.Amount.Should().Be(418.80m);
        outcome.Invoice.Seller.Should().Be("Hangzhou Didi Chuxing Technology Co Ltd");
    }

    [Fact]
    public async Task Didi_parser_precedes_generic_pdf_parser()
    {
        const string text = "VAT Invoice\nInvoice Number: 26337000000257791609\nIssue Date: 2026-03-11\nSeller: Didi Chuxing\nPassenger Transport Service\nAmount: 100.00\nTax Amount: 3.00\nTotal Amount: 103.00";
        var registry = new ParserRegistry([new PdfInvoiceParser(), new DidiInvoiceParser()]);

        var result = await new ParserPipeline(registry).RunAsync(
            NewWorkItem(CreateTextPdf(text), "pdf"), CancellationToken.None);

        result.Selected!.ParserId.Should().Be("didi-invoice");
    }

    [Fact]
    public async Task Cits_gbt_invoice_parses_scct_number_total_date_and_flight_type()
    {
        const string text = "INVOICE\nSCCT00921845\nDate:\n17/03/26\nPFIZER INVESTMENT CO LTD\nOnline Dom Air\nOrigin\nDestination\nAirline\nFlight No\nMU 5126\nTax Detail: CN CNY 50.00 + YQ CNY 20.00\nCITS - American Express Global Business Travel\nTotal:\nCNY\n1,885.82";
        var parser = new CitsGbtParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("SCCT00921845");
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 3, 17));
        outcome.Invoice.Amount.Should().Be(1885.82m);
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.FlightTicket);
        outcome.Invoice.Seller.Should().Be("CITS GBT");
    }

    [Fact]
    public async Task Cits_gbt_parser_precedes_generic_pdf_parser()
    {
        const string text = "INVOICE\nSCCT00921845\nDate: 17/03/26\nOnline Dom Air\nCITS - American Express Global Business Travel\nTotal: CNY 1,885.82";
        var registry = new ParserRegistry([new PdfInvoiceParser(), new CitsGbtParser()]);

        var result = await new ParserPipeline(registry).RunAsync(
            NewWorkItem(CreateTextPdf(text), "pdf"), CancellationToken.None);

        result.Selected!.ParserId.Should().Be("cits-gbt");
    }

    [Fact]
    public async Task Foreign_invoice_parser_reads_ordinal_date_parties_number_and_usd_total()
    {
        const string text = "UNPAID\nIT7 Networks Inc\n130-1959 152 St\nInvoice #23265242\nInvoice Date: Sunday, May 24th, 2026\nInvoiced To\nYong Qi\nDescription\nTotal\n$49.99 USD\nSub Total\n$49.99 USD\nTotal\n$49.99 USD";
        var parser = new ForeignInvoiceParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("23265242");
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 5, 24));
        outcome.Invoice.Seller.Should().Be("IT7 Networks Inc");
        outcome.Invoice.Purchaser.Should().Be("Yong Qi");
        outcome.Invoice.Amount.Should().Be(49.99m);
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.Other);
    }

    [Fact]
    public async Task Ride_itinerary_parser_returns_non_invoice_companion_with_trip_start_and_total()
    {
        const string text = "AMAP ITINERARY\nRide itinerary\nRequest Date: 2026-03-11\nTrip Time: 2025-12-20 15:10 to 2026-02-03 21:11\nTotal: 46.10 CNY\nTrip Count: 2";
        var parser = new RideItineraryParser();
        var workItem = NewWorkItem(CreateTextPdf(text), "pdf");

        parser.CanParse(workItem).Should().BeTrue();
        var outcome = await parser.ParseAsync(workItem, CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.DocumentType.Should().Be(InvoiceDocumentType.RideItinerary);
        outcome.Invoice.IsInvoice.Should().BeFalse();
        outcome.Invoice.Flags.Should().HaveFlag(InvoiceFlags.Itinerary);
        outcome.Invoice.Seller.Should().Be("高德地图");
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2025, 12, 20));
        outcome.Invoice.Amount.Should().Be(46.10m);
    }

    [Fact]
    public void Infrastructure_registers_production_parser_registry_and_special_parsers()
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowInfrastructure();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IParserRegistry>();

        registry.List().Select(parser => parser.ParserId).Should().Contain(new[]
        {
            "pdf-text-invoice",
            "ride-itinerary",
            "xml-invoice",
            "ofd-invoice",
            "railway-ticket",
            "accommodation-folio",
            "didi-invoice",
            "cits-gbt",
            "foreign-invoice",
        });
        registry.List().ToDictionary(parser => parser.ParserId, parser => parser.Priority).Should().Contain(new Dictionary<string, int>
        {
            ["ride-itinerary"] = 470,
            ["didi-invoice"] = 460,
            ["railway-ticket"] = 450,
            ["accommodation-folio"] = 440,
            ["cits-gbt"] = 430,
            ["foreign-invoice"] = 420,
            ["pdf-text-invoice"] = 400,
        });
    }

    private static ParserWorkItem NewWorkItem(byte[] bytes, string sourceKind)
    {
        var identity = DocumentIdentity.Create("railway-candidate");
        var candidate = new DocumentCandidate(identity, 1, "correlation", "uid-1", "ticket.pdf",
            "application/pdf", bytes.Length, 0, sourceKind);
        return new ParserWorkItem(candidate, identity.Value, sourceKind, bytes);
    }

    private static byte[] CreateTextPdf(string text)
    {
        var escapedText = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var streamContent = $"BT /F1 10 Tf 30 760 Td ({escapedText}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamContent)} >>\nstream\n{streamContent}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}