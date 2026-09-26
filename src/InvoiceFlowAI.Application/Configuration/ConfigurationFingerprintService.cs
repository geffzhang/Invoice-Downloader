using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Single hashing entry point for every fingerprint in the system
/// (RecipeFingerprint, ConfigurationFingerprint, registry fingerprints,
/// RuleSet source / AST fingerprints). Returns the lowercase hex SHA-256
/// of the canonical JSON representation of the input. Empty or null input
/// returns the SHA-256 of the empty byte sequence so callers can compare
/// against the well-known empty-string digest deterministically.
/// </summary>
public sealed class ConfigurationFingerprintService
{
    private readonly CanonicalJsonSerializer _canonical = new();

    public string ComputeFromJson(string json)
    {
        var bytes = _canonical.SerializeToCanonicalBytes(json ?? string.Empty);
        return ComputeSha256LowercaseHex(bytes);
    }

    public string ComputeFromObject<T>(T value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return ComputeFromJson(json);
    }

    public string ComputeFromBytes(ReadOnlySpan<byte> bytes) =>
        ComputeSha256LowercaseHex(bytes);

    private static string ComputeSha256LowercaseHex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}