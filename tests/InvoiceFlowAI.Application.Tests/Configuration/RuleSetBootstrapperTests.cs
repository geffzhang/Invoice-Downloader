// Tests for RuleSetBootstrapper. Per design §5 the bootstrapper must:
//   * Insert default RuleSet (version 1) when none exists
//   * Be idempotent — re-running does not insert a second version
//   * Validate the existing fingerprint — if it does not match the built-in
//     fixture, throw rather than silently re-insert
//   * Reject schema versions other than the supported set
//   * Return the inserted/existing RuleSetVersion

using FluentAssertions;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rules;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Configuration;

public sealed class RuleSetBootstrapperTests
{
    [Fact]
    public async Task First_run_inserts_default_version_one()
    {
        var store = new InMemoryRuleSetStore();
        var bootstrapper = new RuleSetBootstrapper(store, new RuleSetValidator());

        var result = await bootstrapper.EnsureDefaultAsync(CancellationToken.None);

        result.RuleSetId.Should().Be("default");
        result.Version.Should().Be(1);
        store.SavedVersions.Should().HaveCount(1);
        store.UserSettingsUpdates.Should().HaveCount(1);
        store.UserSettingsUpdates[0].RuleSetVersion.Should().Be(1);
    }

    [Fact]
    public async Task Second_run_is_idempotent_and_does_not_insert_again()
    {
        var store = new InMemoryRuleSetStore();
        var bootstrapper = new RuleSetBootstrapper(store, new RuleSetValidator());

        var first = await bootstrapper.EnsureDefaultAsync(CancellationToken.None);
        var second = await bootstrapper.EnsureDefaultAsync(CancellationToken.None);

        first.Version.Should().Be(second.Version);
        store.SavedVersions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Tampered_existing_default_version_throws()
    {
        var store = new InMemoryRuleSetStore();
        // Pre-populate with a tampered version whose fingerprint differs from
        // the built-in fixture.
        store.SeedTamperedDefault();
        var bootstrapper = new RuleSetBootstrapper(store, new RuleSetValidator());

        var act = () => bootstrapper.EnsureDefaultAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<RuleSetBootstrapException>();
        ex.Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetRevisionConflict);
    }

    [Fact]
    public void Validator_rejects_unknown_schema_version()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("99.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(DocumentType: null, SellerContains: null),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RecipeSchemaUnsupported);
    }

    [Fact]
    public void Validator_rejects_rule_with_no_match_criteria()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(DocumentType: null, SellerContains: "   "),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }

    [Fact]
    public void Validator_accepts_rule_with_only_subject_match_criterion()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(SubjectContains: "flight"),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validator_rejects_unknown_purchaser_relation()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(PurchaserRelation: "affiliate"),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }

    [Fact]
    public void Validator_rejects_unknown_document_type()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(DocumentType: "ImaginaryInvoice", SellerContains: "seller"),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("transport/../../outside")]
    [InlineData("/outside")]
    [InlineData("C:/outside")]
    [InlineData(@"transport\..\outside")]
    public void Validator_rejects_unsafe_archive_folder_paths(string archiveFolder)
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(DocumentType: "FlightInvoice"),
                new RuleRuleThen(archiveFolder, "travel", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("non_target")]
    [InlineData("unknown")]
    public void Validator_accepts_supported_purchaser_relations(string relation)
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(PurchaserRelation: relation),
                new RuleRuleThen("folder", "category", false, true)),
        });

        var act = () => validator.Validate(doc);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validator_rejects_empty_rules_array()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", Array.Empty<RuleRule>());

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }

    [Fact]
    public void Validator_rejects_rule_without_effective_action_or_category()
    {
        var validator = new RuleSetValidator();
        var doc = new RuleSetDocument("1.0", "default", new[]
        {
            new RuleRule("r", 1, true,
                new RuleRuleWhen(DocumentType: "FlightInvoice", SellerContains: "air"),
                new RuleRuleThen("folder", "  ", false, false)),
        });

        var act = () => validator.Validate(doc);
        act.Should().Throw<RuleSetValidationException>()
            .Which.ReasonCode.Should().Be(RpcErrorCodes.RulesetInvalid);
    }
}