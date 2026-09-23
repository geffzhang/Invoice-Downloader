// Tests for the ProviderRuleRegistry. Per design §5 provider rules must be
// ordered (Priority DESC, ProviderId ASC) and the registry fingerprint must
// change when rule shape changes.

using FluentAssertions;
using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Providers;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Configuration;

public sealed class ProviderRuleRegistryTests
{
    [Fact]
    public void Rules_are_sorted_by_priority_desc_then_provider_id_asc()
    {
        var registry = new ProviderRegistry(
            SchemaVersion: "1.0",
            RegistryFingerprint: "fixture",
            Rules:
            [
                new ProviderRuleDefinition("chinatax-direct", "chinatax", 250, "DIRECT_URL_CHINATAX_HOST_PATH", "provider-special-layout"),
                new ProviderRuleDefinition("baiwang-email", "baiwang", 300, "BAIWANG_HOST_OR_MESSAGE_MARKER", "provider-special-layout"),
                new ProviderRuleDefinition("another-300-high", "other", 300, "OTHER", "provider-special-layout"),
                new ProviderRuleDefinition("jdcloud-direct", "jdcloud", 250, "DIRECT_URL_JDCLOUD_HOST_PATH", "provider-special-layout"),
            ]);

        var ordered = new ProviderRuleOrderingService().Order(registry);
        ordered.Select(r => r.ProviderId).Should().Equal("another-300-high", "baiwang-email", "chinatax-direct", "jdcloud-direct");
    }

    [Fact]
    public void Registry_fingerprint_changes_when_a_rule_priority_changes()
    {
        var baseline = new ProviderRegistry("1.0", "baseline", new[]
        {
            new ProviderRuleDefinition("baiwang-email", "baiwang", 300, "X", "provider-special-layout"),
        });
        var changed = new ProviderRegistry("1.0", "changed", new[]
        {
            new ProviderRuleDefinition("baiwang-email", "baiwang", 290, "X", "provider-special-layout"),
        });

        var fingerprint = new ProviderRuleFingerprintService();
        fingerprint.Compute(baseline).Should().NotBe(fingerprint.Compute(changed));
    }

    [Fact]
    public void Two_identical_registries_produce_the_same_fingerprint()
    {
        var first = new ProviderRegistry("1.0", "left", new[]
        {
            new ProviderRuleDefinition("baiwang-email", "baiwang", 300, "X", "provider-special-layout"),
        });
        var second = new ProviderRegistry("1.0", "right", new[]
        {
            new ProviderRuleDefinition("baiwang-email", "baiwang", 300, "X", "provider-special-layout"),
        });

        var fingerprint = new ProviderRuleFingerprintService();
        fingerprint.Compute(first).Should().Be(fingerprint.Compute(second));
    }
}