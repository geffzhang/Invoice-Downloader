using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Extraction;

public sealed class InvoiceResponseSchemaTests
{
    [Fact]
    public void Parse_accepts_one_json_invoice_object()
    {
        var parsed = InvoiceResponseSchema.Parse(ValidResponse);

        parsed.IsInvoice.Should().BeTrue();
        parsed.InvoiceDate.Should().Be(new DateOnly(2026, 9, 24));
        parsed.Amount.Should().Be(100m);
        parsed.DocumentType.Should().Be(InvoiceDocumentType.Catering);
        parsed.Confidence.Should().Be(0.95m);
    }

    [Fact]
    public void Parse_strips_one_markdown_json_fence()
    {
        var parsed = InvoiceResponseSchema.Parse($"```json\n{ValidResponse}\n```");

        parsed.InvoiceNumber.Should().Be("INV-1");
    }

    [Theory]
    [InlineData("prefix " + ValidResponse)]
    [InlineData(ValidResponse + " trailing")]
    [InlineData(ValidResponse + " " + ValidResponse)]
    [InlineData("```json\n```json\n" + ValidResponse + "\n```\n```")]
    public void Parse_rejects_text_or_additional_fences_outside_the_single_object(string response)
    {
        var exception = Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(response));

        exception.Message.Should().Be("AI response does not match the invoice schema.");
    }

    [Fact]
    public void Parse_rejects_unknown_properties_and_invalid_nested_values()
    {
        var unknownProperty = ValidResponse.Replace("\"confidence\": 0.95", "\"confidence\": 0.95,\"debug\":\"private\"");
        var invalidType = ValidResponse.Replace("\"documentType\": \"Catering\"", "\"documentType\": \"NoSuchType\"");

        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(unknownProperty));
        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(invalidType));
    }

    [Fact]
    public void Parse_rejects_missing_required_properties()
    {
        var missingItems = ValidResponse.Replace("\"items\"", "\"notItems\"");

        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(missingItems));
    }

    [Fact]
    public void Parse_rejects_null_invoice_classification_or_collections()
    {
        var nullInvoiceFlag = ValidResponse.Replace("\"isInvoice\": true", "\"isInvoice\": null");
        var nullFlags = ValidResponse.Replace("\"flags\": []", "\"flags\": null");
        var nullItems = ValidResponse.Replace("\"items\": []", "\"items\": null");

        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(nullInvoiceFlag));
        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(nullFlags));
        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(nullItems));
    }

    [Fact]
    public void Parse_rejects_out_of_range_confidence_and_non_iso_date()
    {
        var invalidConfidence = ValidResponse.Replace("\"confidence\": 0.95", "\"confidence\": 1.5");
        var invalidDate = ValidResponse.Replace("2026-09-24", "24/09/2026");

        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(invalidConfidence));
        Assert.Throws<InvoiceResponseSchemaException>(() => InvoiceResponseSchema.Parse(invalidDate));
    }

    private const string ValidResponse = """
        {
          "isInvoice": true,
          "invoiceDate": "2026-09-24",
          "purchaser": "Example Company",
          "seller": "Example Seller",
          "amount": 100.00,
          "taxAmount": 0.00,
          "totalAmount": 100.00,
          "invoiceCode": null,
          "invoiceNumber": "INV-1",
          "documentType": "Catering",
          "category": "餐饮",
          "confidence": 0.95,
          "flags": [],
          "route": null,
          "items": []
        }
        """;
}
