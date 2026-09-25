using InvoiceFlowAI.Application.Ai;

namespace InvoiceFlowAI.Application.Extraction;

public static class InvoiceExtractionPrompt
{
    public const string Version = "invoice-extraction.v1";

    public const string SystemPrompt = """
        Extract invoice fields from the supplied document evidence. Return exactly one JSON object matching the requested schema, with no commentary. Use ISO 8601 dates (yyyy-MM-dd), JSON numbers for amounts, null for unknown values, and only declared enum values. Do not infer missing values. Confidence must be between 0 and 1.
        """;

    public static ChatCompletionRequest CreateTrackA(string text, InvoiceExtractionRules rules) => new(
        SystemPrompt,
        CreateUserPrompt(text),
        rules.TrackAModel,
        Temperature: 0.1,
        MaxOutputTokens: rules.MaximumOutputTokens);

    public static ChatCompletionRequest CreateTrackB(InvoiceExtractionRules rules) => new(
        SystemPrompt,
        CreateUserPrompt("The document is provided as ordered page images. Extract only fields visible in those images."),
        rules.TrackBModel,
        Temperature: 0.1,
        MaxOutputTokens: rules.MaximumOutputTokens);

    private static string CreateUserPrompt(string evidence) => $"""
        Schema version: {InvoiceResponseSchema.Version}
        Required JSON properties: isInvoice, invoiceDate, purchaser, seller, amount, taxAmount, totalAmount, invoiceCode, invoiceNumber, documentType, category, confidence, flags, route, items.
        route is null or an object with direction, departureDate, departureCity, destinationCity. items is an array of objects with name, quantity, unitPrice, amount, taxAmount, unit, specification.
        Document evidence:
        {evidence}
        """;
}
