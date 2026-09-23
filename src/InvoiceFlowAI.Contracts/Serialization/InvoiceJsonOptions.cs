using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceFlowAI.Contracts.Serialization;

/// <summary>
/// Centralized <see cref="JsonSerializerOptions"/> for every envelope and
/// DTO in the Contracts assembly. The first release pins these defaults;
/// any change is breaking for the RPC wire format and must bump the
/// protocol version in lock-step.
/// </summary>
public static class InvoiceJsonOptions
{
    public const string ProtocolVersion = "invoiceflow.rpc.v1";

    public static JsonSerializerOptions Strict { get; } = BuildStrict();

    private static JsonSerializerOptions BuildStrict()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}