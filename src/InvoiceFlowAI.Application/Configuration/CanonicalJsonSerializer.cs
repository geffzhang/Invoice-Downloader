using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Deterministic JSON serializer used as the input to
/// <see cref="ConfigurationFingerprintService"/>. Object keys are sorted
/// using ordinal string comparison, arrays preserve their order, and
/// numbers are parsed as <see cref="decimal"/> and re-emitted without
/// trailing zeros so <c>100</c> and <c>100.00</c> hash identically. NaN
/// and Infinity raise <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class CanonicalJsonSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    public string SerializeToCanonicalJson(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, document.RootElement);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public byte[] SerializeToCanonicalBytes(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, document.RootElement);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var ordered = new List<JsonProperty>();
                foreach (var prop in element.EnumerateObject())
                {
                    ordered.Add(prop);
                }
                ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var prop in ordered)
                {
                    writer.WritePropertyName(prop.Name);
                    Write(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static string NormalizeNumber(string raw)
    {
        // Try decimal first; it covers every integer and floating-point value
        // we use in this domain (counts, priorities, decimal money) with
        // 28-29 digits of precision. Strip trailing zeros so canonical
        // representations of 100 and 100.00 collapse to "100".
        if (decimal.TryParse(raw, NumberStyles.Float | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d))
        {
            return d.ToString("0.#############################", CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"Unsupported numeric literal '{raw}'.");
    }
}