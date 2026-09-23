using System.Text.Json;

namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>
/// Stable error envelope returned in every failed RPC response. All fields
/// are required; <c>UserMessage</c> is safe to surface to the user,
/// <c>Details</c> carries machine-readable context (e.g. revision conflict
/// expected/actual, or a list of <see cref="RpcFieldError"/> entries).
/// </summary>
public sealed record RpcError(
    string Code,
    string Scope,
    bool Retryable,
    string UserMessage,
    bool DetailsAvailable,
    JsonElement? Details = null)
{
    public IReadOnlyList<RpcFieldError>? FieldErrors
    {
        get
        {
            if (Details is null) return null;
            if (Details.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<RpcFieldError>();
            foreach (var entry in Details.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "" : "";
                var code = entry.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? "" : "";
                var message = entry.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "" : "";
                list.Add(new RpcFieldError(path, code, message));
            }
            return list.Count == 0 ? null : list;
        }
    }
}