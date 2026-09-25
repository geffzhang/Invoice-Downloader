using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Extraction;

public sealed record InvoiceResponseFields(
    bool? IsInvoice,
    DateOnly? InvoiceDate,
    string? Purchaser,
    string? Seller,
    decimal? Amount,
    decimal? TaxAmount,
    decimal? TotalAmount,
    string? InvoiceCode,
    string? InvoiceNumber,
    InvoiceDocumentType? DocumentType,
    string? Category,
    decimal? Confidence,
    InvoiceFlags[]? Flags,
    InvoiceRouteResponse? Route,
    InvoiceItemResponse[]? Items);

public sealed record InvoiceRouteResponse(
    InvoiceRouteDirection Direction,
    DateOnly? DepartureDate,
    string? DepartureCity,
    string? DestinationCity);

public sealed record InvoiceItemResponse(
    string? Name,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    decimal? TaxAmount,
    string? Unit,
    string? Specification);

public sealed class InvoiceResponseSchemaException : Exception
{
    public InvoiceResponseSchemaException()
        : base("AI response does not match the invoice schema.")
    {
    }
}

public static class InvoiceResponseSchema
{
    public const string Version = "invoice-fields.v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static InvoiceResponseFields Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvoiceResponseSchemaException();
        }

        try
        {
            var json = StripSingleFence(response);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
            {
                throw new InvoiceResponseSchemaException();
            }
            RequireProperties(document.RootElement,
            [
                "isInvoice", "invoiceDate", "purchaser", "seller", "amount", "taxAmount", "totalAmount",
                "invoiceCode", "invoiceNumber", "documentType", "category", "confidence", "flags", "route", "items",
            ]);
            if (document.RootElement.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Object)
            {
                RequireProperties(route, ["direction", "departureDate", "departureCity", "destinationCity"]);
            }
            if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvoiceResponseSchemaException();
                    }
                    RequireProperties(item, ["name", "quantity", "unitPrice", "amount", "taxAmount", "unit", "specification"]);
                }
            }

            var fields = document.RootElement.Deserialize<InvoiceResponseFields>(SerializerOptions)
                ?? throw new InvoiceResponseSchemaException();
            Validate(fields);
            return fields;
        }
        catch (InvoiceResponseSchemaException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new InvoiceResponseSchemaException();
        }
        catch (FormatException)
        {
            throw new InvoiceResponseSchemaException();
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter<InvoiceDocumentType>(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<InvoiceRouteDirection>(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<InvoiceFlags>(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static string StripSingleFence(string response)
    {
        var trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0 || !trimmed[..firstLineEnd].TrimEnd('\r').Equals("```json", StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            throw new InvoiceResponseSchemaException();
        }

        var content = trimmed[(firstLineEnd + 1)..^3].Trim();
        if (content.Contains("```", StringComparison.Ordinal))
        {
            throw new InvoiceResponseSchemaException();
        }

        return content;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }

    private static void RequireProperties(JsonElement element, IReadOnlyList<string> requiredProperties)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (requiredProperties.Any(property => !names.Contains(property)))
        {
            throw new InvoiceResponseSchemaException();
        }
    }

    private static void Validate(InvoiceResponseFields fields)
    {
        if (fields.IsInvoice is null || fields.Flags is null || fields.Items is null)
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.DocumentType is { } type && (!Enum.IsDefined(type) || type == InvoiceDocumentType.Unrecognized))
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.Confidence is < 0m or > 1m)
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.Items is { Length: > 200 })
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.Flags is not null && fields.Flags.Any(flag => !Enum.IsDefined(flag)))
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.Route is { } route && !Enum.IsDefined(route.Direction))
        {
            throw new InvoiceResponseSchemaException();
        }
        if (fields.Items is not null && fields.Items.Any(item => item is null))
        {
            throw new InvoiceResponseSchemaException();
        }
    }
}
