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

    private static InvoiceDocument NewInvoice(string? contentHash = null, string? number = "INV-1", DateOnly? date = null) =>
        new(
            DocumentId: "doc-1",
            InvoiceDate: date,
            Purchaser: "Acme",
            Seller: "Air Berlin",
            Amount: 100m,
            TaxAmount: 13m,
            TotalAmount: 113m,
            InvoiceCode: "CODE-1",
            InvoiceNumber: number,
            DocumentType: InvoiceDocumentType.AirTicket,
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "",
            ContentHash: contentHash ?? "");
}
