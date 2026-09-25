// Verifies ArchiveNamingPolicy (Task 10):
//   * Same inputs produce the same path
//   * Different content hash yields different prefix
//   * Special characters in invoice number are sanitised
//   * Missing invoice number falls back to "no-number"
//   * Undated invoice falls back to "undated"
//   * Hash prefix is exactly 8 lowercase hex characters

using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Archive;

public sealed class ArchiveNamingPolicyTests
{
    private readonly IArchiveNamingPolicy _policy = new ArchiveNamingPolicy();

    [Fact]
    public void Same_inputs_produce_same_path()
    {
        var inv = NewInvoice();

        var a = _policy.BuildRelativePath(inv, "run-1");
        var b = _policy.BuildRelativePath(inv, "run-1");

        a.Should().Be(b);
    }

    [Fact]
    public void Different_content_hash_yields_different_prefix()
    {
        var a = _policy.BuildRelativePath(NewInvoice("hash-aaa"), "run-1");
        var b = _policy.BuildRelativePath(NewInvoice("hash-bbb"), "run-1");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Special_characters_in_invoice_number_are_sanitised()
    {
        var inv = NewInvoice("hash-1", number: "INV/2026 01 01*");

        var path = _policy.BuildRelativePath(inv, "run-1");

        // Take the last segment so we don't pick up the run-id "/".
        var lastSegment = path[(path.LastIndexOf('/') + 1)..];
        lastSegment.Should().Contain("INV20260101");
        lastSegment.Should().NotContain("/");
        lastSegment.Should().NotContain("*");
        lastSegment.Should().NotContain(" ");
    }

    [Fact]
    public void Missing_invoice_number_falls_back_to_no_number()
    {
        var inv = NewInvoice("hash-1", number: null);

        var path = _policy.BuildRelativePath(inv, "run-1");

        path.Should().Contain("no-number");
    }

    [Fact]
    public void Undated_invoice_falls_back_to_undated()
    {
        var inv = NewInvoice("hash-1", date: null);

        var path = _policy.BuildRelativePath(inv, "run-1");

        path.Should().Contain("undated");
    }

    [Fact]
    public void Hash_prefix_is_eight_lowercase_hex_characters()
    {
        var hash = _policy.BuildContentHash(new byte[] { 0x01, 0x02, 0x03 });
        hash.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public void Relative_path_starts_with_archive_run_id()
    {
        var path = _policy.BuildRelativePath(NewInvoice(), "run-42");
        path.Should().StartWith("archive/run-42/");
    }

    [Fact]
    public void Ride_pair_names_match_python_shape_and_preserve_each_source_extension()
    {
        var invoice = NewInvoice(type: InvoiceDocumentType.RideInvoice, amount: 100m, date: new DateOnly(2026, 9, 24));
        var itinerary = NewInvoice(type: InvoiceDocumentType.RideItinerary, amount: 103m, date: new DateOnly(2026, 9, 24));

        var paths = InvokeNaming(
            "BuildPairRelativePaths",
            invoice,
            "ride-高德发票.pdf",
            itinerary,
            "ride-行程单.ofd",
            "run-1",
            1,
            "ride");

        ReadPath(paths, "InvoiceRelativePath").Should().Be("archive/run-1/0924-高德-01-发票_103.00元.pdf");
        ReadPath(paths, "CompanionRelativePath").Should().Be("archive/run-1/0924-高德-01-行程单_103.00元.ofd");
    }

    [Fact]
    public void Hotel_pair_names_match_python_shape_and_use_invoice_amount_and_date()
    {
        var invoice = NewInvoice(type: InvoiceDocumentType.HotelInvoice, amount: 500m, date: new DateOnly(2026, 9, 24));
        var folio = NewInvoice(type: InvoiceDocumentType.HotelFolio, amount: 500m, date: new DateOnly(2026, 9, 25));

        var paths = InvokeNaming(
            "BuildPairRelativePaths",
            invoice,
            "hotel-invoice.pdf",
            folio,
            "hotel-folio.xlsx",
            "run-2",
            3,
            "hotel");

        ReadPath(paths, "InvoiceRelativePath").Should().Be("archive/run-2/20260924-住宿-03-发票_500.00元.pdf");
        ReadPath(paths, "CompanionRelativePath").Should().Be("archive/run-2/20260924-住宿-03-水单_500.00元.xlsx");
    }

    [Fact]
    public void Review_and_retained_names_sanitize_untrusted_components()
    {
        var review = InvokeNaming("BuildReviewRelativePath", "run-3", "../doc/1", "unsafe name.pdf", "PAIRING/ERROR");
        var retained = InvokeNaming("BuildRetainedRelativePath", "run-3", "../doc/1", "unsafe name.pdf");

        var reviewPath = (string)review;
        var retainedPath = (string)retained;
        reviewPath.Should().StartWith("archive/run-3/review/");
        retainedPath.Should().StartWith("archive/run-3/retained/");
        reviewPath.Should().NotContain("..").And.NotContain("PAIRING/ERROR");
        retainedPath.Should().NotContain("..");
        Path.GetExtension(reviewPath).Should().Be(".pdf");
        Path.GetExtension(retainedPath).Should().Be(".pdf");
    }

    private static object InvokeNaming(string methodName, params object?[] arguments)
    {
        var method = typeof(IArchiveNamingPolicy).GetMethod(methodName);
        method.Should().NotBeNull($"the naming contract includes {methodName}");
        return method!.Invoke(_policyStatic, arguments)!;
    }

    private static string ReadPath(object paths, string propertyName)
    {
        var property = paths.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"pair paths include {propertyName}");
        return (string)property!.GetValue(paths)!;
    }

    private static readonly IArchiveNamingPolicy _policyStatic = new ArchiveNamingPolicy();

    private static InvoiceDocument NewInvoice(
        string? contentHash = null,
        string? number = "INV-1",
        DateOnly? date = null,
        InvoiceDocumentType type = InvoiceDocumentType.AirTicket,
        decimal amount = 100m) =>
        new(
            DocumentId: "doc-1",
            InvoiceDate: date,
            Purchaser: "Acme",
            Seller: "Air Berlin",
            Amount: amount,
            TaxAmount: 13m,
            TotalAmount: amount,
            InvoiceCode: "CODE-1",
            InvoiceNumber: number,
            DocumentType: type,
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "",
            ContentHash: contentHash ?? "");
}
