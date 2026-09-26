// Tests for the canonical-JSON serializer + the lower-case SHA-256 fingerprint
// service. Per design §5 both RecipeFingerprint and ConfigurationFingerprint must
// remain stable across:
//   * JSON property reordering on the wire ({"a":1,"b":2} == {"b":2,"a":1})
//   * Invariant decimal formatting ("100.00" never "100,00")
//   * Absence of timestamps, machine names, or random IDs in the input
//
// The fingerprint service is also reused by ConfigurationFingerprint later; it is
// the single hashing entry point for everything that participates in the run
// identity.

using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Configuration;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Configuration;

public sealed class CanonicalJsonFingerprintTests
{
    private readonly CanonicalJsonSerializer _serializer = new();
    private readonly ConfigurationFingerprintService _fingerprint = new();

    [Fact]
    public void Canonical_serializer_sorts_object_keys_alphabetically()
    {
        var ordered = JsonSerializer.Serialize(new { B = 2, A = 1, C = 3 });
        var canonical = _serializer.SerializeToCanonicalJson(ordered);
        // Properties are emitted in alphabetical order regardless of input order.
        // Property name casing is preserved — only ordering changes.
        canonical.Should().StartWith("{\"A\":1,\"B\":2,\"C\":3}");
    }

    [Fact]
    public void Fingerprint_is_stable_under_property_reordering()
    {
        var a = "{\"a\":1,\"b\":2,\"c\":3}";
        var b = "{\"c\":3,\"a\":1,\"b\":2}";
        var fingerprintA = _fingerprint.ComputeFromJson(a);
        var fingerprintB = _fingerprint.ComputeFromJson(b);
        fingerprintA.Should().Be(fingerprintB);
    }

    [Fact]
    public void Fingerprint_changes_when_value_changes()
    {
        var fingerprintA = _fingerprint.ComputeFromJson("{\"a\":1}");
        var fingerprintB = _fingerprint.ComputeFromJson("{\"a\":2}");
        fingerprintA.Should().NotBe(fingerprintB);
    }

    [Fact]
    public void Fingerprint_is_lowercase_hex_of_expected_length()
    {
        var fingerprint = _fingerprint.ComputeFromJson("{\"any\":\"value\"}");
        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Fingerprint_is_decimal_invariant()
    {
        var fingerprintA = _fingerprint.ComputeFromJson("{\"amount\":100.00}");
        var fingerprintB = _fingerprint.ComputeFromJson("{\"amount\":100}");
        fingerprintA.Should().Be(fingerprintB);
    }

    [Fact]
    public void Fingerprint_ignores_inner_whitespace_changes()
    {
        var fingerprintA = _fingerprint.ComputeFromJson("{\"a\":1,\"b\":2}");
        var fingerprintB = _fingerprint.ComputeFromJson("{ \"a\": 1, \"b\": 2 }");
        fingerprintA.Should().Be(fingerprintB);
    }
}