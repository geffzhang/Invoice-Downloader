// Shared JSON options for the bridge. Strict (no unmapped members), camelCase,
// enum strings, invariant culture. DateOnly is serialized as ISO 8601
// "yyyy-MM-dd" by .NET 8+ — no custom converter required. The same options
// must be used by the dispatcher, the bridge, and any handler that emits
// structured results so the page can rely on a single shape.

using InvoiceFlowAI.Contracts.Serialization;

namespace InvoiceFlowAI.App.Rpc;

public static class JsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = InvoiceJsonOptions.Strict;
}
