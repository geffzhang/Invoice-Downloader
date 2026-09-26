// Verifies InvoiceDeduplicator (Task 10):
//   * Same (number, code, total, purchaser, seller) → duplicate
//   * Different number → unique
//   * Different total → unique
//   * Empty input → empty result
//   * First occurrence is kept, later ones are reported as duplicates

using FluentAssertions;
using InvoiceFlowAI.Application.Deduplication;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Deduplication;

public sealed class InvoiceDeduplicatorTests
{
    private readonly IInvoiceDeduplicator _dedup = new InvoiceDeduplicator();

    [Fact]
    public void Empty_input_yields_empty_result()
    {
        var result = _dedup.Deduplicate(Array.Empty<InvoiceDocument>());

        result.Unique.Should().BeEmpty();
        result.DuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public void Same_number_code_total_purchaser_seller_are_duplicates()
    {
        var a = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Unique.Should().ContainSingle();
        result.DuplicateGroups.Should().ContainSingle();
    }

    [Fact]
    public void Different_number_remains_unique()
    {
        var a = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice("INV-2", "CODE-1", 100m, "Acme", "Air Berlin");

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Unique.Should().HaveCount(2);
        result.DuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public void Different_total_remains_unique()
    {
        var a = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice("INV-1", "CODE-1", 200m, "Acme", "Air Berlin");

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Unique.Should().HaveCount(2);
    }

    [Fact]
    public void First_occurrence_kept_later_ones_in_duplicate_group()
    {
        var a = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var c = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");

        var result = _dedup.Deduplicate(new[] { a, b, c });

        result.Unique.Should().ContainSingle();
        result.DuplicateGroups.Should().ContainSingle();
        result.DuplicateGroups[0].Members.Should().HaveCount(3);
    }

    [Fact]
    public void Null_invoice_number_treated_as_empty_string()
    {
        var a = NewInvoice(null, "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice(null, "CODE-1", 100m, "Acme", "Air Berlin");

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Unique.Should().ContainSingle();
    }

    [Fact]
    public void Coalesce_returns_unique_invoices()
    {
        var a = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");
        var b = NewInvoice("INV-1", "CODE-1", 100m, "Acme", "Air Berlin");

        _dedup.Coalesce(new[] { a, b }).Should().ContainSingle();
    }

    private static InvoiceDocument NewInvoice(string? number, string? code, decimal total, string purchaser, string seller) =>
        new(
            DocumentId: $"doc-{number ?? "null"}-{total}",
            InvoiceDate: new DateOnly(2026, 9, 15),
            Purchaser: purchaser,
            Seller: seller,
            Amount: total,
            TaxAmount: 13m,
            TotalAmount: total,
            InvoiceCode: code,
            InvoiceNumber: number,
            DocumentType: InvoiceDocumentType.Other,
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "",
            ContentHash: "");
}
