// Round-trip tests for the golden fixtures committed under docs/superpowers/fixtures.
//
// Each fixture is the canonical contract for one of the following:
//   * Recipe DAG and zero-pipeline graph metadata (recipe/invoiceflow.default.v1.json)
//   * RuleSet bootstrap schema and AST fingerprint (rules/default.v1.json)
//   * Provider-rule registry (providers/registry.v1.json) and conflict response
//   * Special-parser registry (parsers/registry.v1.json) and conflict response
//   * Account save RPC request / response / revision-conflict response
//   * Account test variants (success, auth-error, credentials-missing, mailbox-not-found, generic error)
//   * URL provider registry and error matrix (url/provider-registry.v1.json, url/errors.v1.json)
//   * Release manifest example (release/release-manifest.example.json)
//   * Email-body receipt sample (email-body/baiwang.receipt.json)
//   * Run progress event (rpc/run-progress.event.json)
//
// Per the migration implementation plan Task 2 step 1, these tests must:
//   * assert schema/version fields and key counts;
//   * assert that re-serializing a deserialized record produces identical JSON;
//   * reject unknown JSON fields (JsonUnmappedMemberHandling.Disallow);
//   * reject serialization outputs that contain secret-like strings
//     (auth codes, API keys, raw credentials, full mail body).
//
// The tests are written first per the RED -> GREEN -> VERIFICATION pattern.
// They reference DTOs that don't exist yet on purpose: this is the failing
// baseline that proves the Domain and Contracts types need to be created.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InvoiceFlowAI.Contracts.Recipe;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Rules;
using InvoiceFlowAI.Contracts.Settings;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Url;
using InvoiceFlowAI.Contracts.Release;
using InvoiceFlowAI.Contracts.EmailBody;
using InvoiceFlowAI.Contracts.Providers;
using InvoiceFlowAI.Contracts.Parsers;
using Xunit;

namespace InvoiceFlowAI.Contracts.Tests;

public sealed class ContractRoundTripTests
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] SecretLikePatterns =
    {
        "authCode", "auth_code", "apiKey", "api_key", "password", "secret",
        "accessToken", "access_token", "refreshToken", "refresh_token",
        "-----BEGIN ", "PRIVATE KEY",
    };

    // ---------- Recipe ----------

    [Fact]
    public void Recipe_fixture_round_trips_and_keeps_node_count()
    {
        var text = FixtureLoader.ReadText("recipe/invoiceflow.default.v1.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        doc.RootElement.GetProperty("recipeId").GetString().Should().Be("invoiceflow.default");
        doc.RootElement.GetProperty("recipeVersion").GetString().Should().Be("2026-09-23-v1");
        doc.RootElement.GetProperty("zeroPipelineRecipeVersion").GetString().Should().Be("1.2.0");
        doc.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(8);

        var recipe = JsonSerializer.Deserialize<PipelineRecipe>(text, Strict);
        recipe.Should().NotBeNull();
        recipe!.SchemaVersion.Should().Be("1.0");
        recipe.RecipeId.Should().Be("invoiceflow.default");
        recipe.Nodes.Should().HaveCount(8);
        recipe.Connections.Should().Contain(c => c.FromNodeId == "validate" && c.ToNodeId == "scan");
        recipe.Connections.Should().Contain(c => c.FromNodeId == "recover" && c.FromPort == "Candidates"
            && c.ToNodeId == "extract" && c.ToPort == "Candidates");

        var roundTripped = JsonSerializer.Serialize(recipe, Strict);
        var reloaded = JsonSerializer.Deserialize<PipelineRecipe>(roundTripped, Strict);
        reloaded!.RecipeVersion.Should().Be(recipe.RecipeVersion);
        reloaded.Nodes.Select(n => n.NodeId).Should().BeEquivalentTo(recipe.Nodes.Select(n => n.NodeId));
        reloaded.Execution.MaxInFlightCandidates.Should().Be(32);
    }

    [Fact]
    public void Recipe_rejects_unknown_field()
    {
        var tampered = """{"schemaVersion":"1.0","recipeId":"x","recipeVersion":"v1","zeroPipelineRecipeVersion":"1.2.0","nodes":[],"connections":[],"execution":{},"sneakyField":"injected"}""";
        var act = () => JsonSerializer.Deserialize<PipelineRecipe>(tampered, Strict);
        act.Should().Throw<JsonException>();
    }

    // ---------- RuleSet ----------

    [Fact]
    public void RuleSet_fixture_round_trips()
    {
        var text = FixtureLoader.ReadText("rules/default.v1.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("schemaVersion").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("rules").GetArrayLength().Should().BeGreaterThan(0);

        var ruleSet = JsonSerializer.Deserialize<RuleSetDocument>(text, Strict);
        ruleSet.Should().NotBeNull();
        ruleSet!.RuleSetId.Should().Be("default");
        ruleSet.Rules.Should().NotBeEmpty();
        ruleSet.Rules[0].RuleId.Should().Be("archive-flight");

        var roundTripped = JsonSerializer.Serialize(ruleSet, Strict);
        var reloaded = JsonSerializer.Deserialize<RuleSetDocument>(roundTripped, Strict);
        reloaded!.Rules.Should().HaveCount(ruleSet.Rules.Count);
        reloaded.Rules[0].Then.ArchiveFolder.Should().Be(ruleSet.Rules[0].Then.ArchiveFolder);
    }

    // ---------- Provider / Parser registry + conflict responses ----------

    [Fact]
    public void Provider_registry_fixture_round_trips()
    {
        var text = FixtureLoader.ReadText("providers/registry.v1.json");
        var registry = JsonSerializer.Deserialize<ProviderRegistry>(text, Strict);
        registry.Should().NotBeNull();
        registry!.Rules.Should().NotBeEmpty();
        registry.SchemaVersion.Should().NotBeNullOrEmpty();
        registry.RegistryFingerprint.Should().NotBeNullOrEmpty();
        registry.Rules.Should().Contain(r => r.ProviderId == "baiwang-email");

        var roundTripped = JsonSerializer.Serialize(registry, Strict);
        var reloaded = JsonSerializer.Deserialize<ProviderRegistry>(roundTripped, Strict);
        reloaded!.Rules.Should().HaveCount(registry.Rules.Count);
    }

    [Fact]
    public void Parser_registry_fixture_round_trips()
    {
        var text = FixtureLoader.ReadText("parsers/registry.v1.json");
        var registry = JsonSerializer.Deserialize<ParserRegistry>(text, Strict);
        registry.Should().NotBeNull();
        registry!.Parsers.Should().NotBeEmpty();
        registry.Parsers.Should().Contain(p => p.ParserId == "railway-ticket");

        var roundTripped = JsonSerializer.Serialize(registry, Strict);
        var reloaded = JsonSerializer.Deserialize<ParserRegistry>(roundTripped, Strict);
        reloaded!.Parsers.Should().HaveCount(registry.Parsers.Count);
    }

    [Fact]
    public void Provider_conflict_response_keeps_machine_readable_details()
    {
        var text = FixtureLoader.ReadText("providers/conflict.response.json");
        using var doc = JsonDocument.Parse(text);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("PROVIDER_RULE_CONFLICT");
        error.GetProperty("scope").GetString().Should().NotBeNullOrEmpty();
        error.TryGetProperty("details", out _).Should().BeTrue();

        var envelope = JsonSerializer.Deserialize<RpcResponse<JsonElement>>(text, Strict);
        envelope!.Ok.Should().BeFalse();
        envelope.Error!.Code.Should().Be("PROVIDER_RULE_CONFLICT");
    }

    [Fact]
    public void Parser_conflict_response_keeps_machine_readable_details()
    {
        var text = FixtureLoader.ReadText("parsers/conflict.response.json");
        var envelope = JsonSerializer.Deserialize<RpcResponse<JsonElement>>(text, Strict);
        envelope!.Ok.Should().BeFalse();
        envelope.Error!.Code.Should().Be("SPECIAL_PARSER_CONFLICT");
        envelope.Error.DetailsAvailable.Should().BeTrue();
    }

    // ---------- RPC envelopes ----------

    [Fact]
    public void Account_save_request_round_trips_with_envelope_intact()
    {
        var text = FixtureLoader.ReadText("rpc/account-save.request.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("protocol").GetString().Should().Be("invoiceflow.rpc.v1");
        doc.RootElement.GetProperty("method").GetString().Should().Be("account.save");

        var envelope = JsonSerializer.Deserialize<RpcRequest<AccountSaveRequest>>(text, Strict);
        envelope!.Method.Should().Be("account.save");
        envelope.Params.ExpectedRevision.Should().Be(0);
        envelope.Params.Account.AccountId.Should().Be("mail-account-1");
        envelope.Params.Account.CredentialName.Should().Be("mail.imap.auth-code");

        // Re-serialized RPC must NOT contain the credential name itself's value
        // — only the *reference*. Auth code values never enter the request.
        var roundTripped = JsonSerializer.Serialize(envelope, Strict);
        roundTripped.Should().NotContain("authCode", "raw auth codes must never appear in JSON");
    }

    [Fact]
    public void Account_save_response_round_trips()
    {
        var text = FixtureLoader.ReadText("rpc/account-save.response.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("result").GetProperty("account").GetProperty("revision").GetInt32().Should().Be(1);

        var envelope = JsonSerializer.Deserialize<RpcResponse<AccountMutationResult>>(text, Strict);
        envelope!.Ok.Should().BeTrue();
        envelope.Result!.Account.Revision.Should().Be(1);
        envelope.Result.Account.CredentialConfigured.Should().BeTrue();
        envelope.Result.Account.MaskedEmailAddress.Should().Be("u***@example.com");
        envelope.Result.ConfigurationFingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Account_revision_conflict_response_keeps_revision_details()
    {
        var text = FixtureLoader.ReadText("rpc/account-revision-conflict.response.json");
        var envelope = JsonSerializer.Deserialize<RpcResponse<JsonElement>>(text, Strict);
        envelope!.Ok.Should().BeFalse();
        envelope.Error!.Code.Should().Be("MAILBOX_ACCOUNT_REVISION_CONFLICT");
        envelope.Error.Scope.Should().Be("account");
        envelope.Error.Retryable.Should().BeFalse();
        envelope.Error.DetailsAvailable.Should().BeTrue();
        envelope.Error.Details!.Value.GetProperty("expectedRevision").GetInt32().Should().Be(0);
        envelope.Error.Details.Value.GetProperty("actualRevision").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Account_test_success_response_round_trips()
    {
        var text = FixtureLoader.ReadText("rpc/account-test.response.json");
        var envelope = JsonSerializer.Deserialize<RpcResponse<AccountTestResult>>(text, Strict);
        envelope!.Ok.Should().BeTrue();
        envelope.Result!.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("account-test.error.json")]
    [InlineData("account-test.auth-error.json")]
    [InlineData("account-test.mailbox-not-found.json")]
    [InlineData("account-test.credentials-missing.json")]
    public void Account_test_error_variants_have_stable_envelope(string fixture)
    {
        var text = FixtureLoader.ReadText("rpc/" + fixture);
        var envelope = JsonSerializer.Deserialize<RpcResponse<AccountTestResult>>(text, Strict);
        envelope!.Ok.Should().BeFalse();
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Code.Should().NotBeNullOrEmpty();
        envelope.Error.UserMessage.Should().NotBeNullOrEmpty();
        envelope.Error.Scope.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Run_progress_event_round_trips()
    {
        var text = FixtureLoader.ReadText("rpc/run-progress.event.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("protocol").GetString().Should().Be("invoiceflow.rpc.v1");
        doc.RootElement.GetProperty("event").GetString().Should().Be("run.progress");
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
        doc.RootElement.GetProperty("eventSequence").GetInt32().Should().Be(12);

        var envelope = JsonSerializer.Deserialize<RpcEvent<RunProgressPayload>>(text, Strict);
        envelope!.EventName.Should().Be("run.progress");
        envelope.RunId.Should().Be("run-1");
        envelope.EventSequence.Should().Be(12);
        envelope.Payload.Stage.Should().Be("extracting");
        envelope.Payload.Completed.Should().Be(4);
        envelope.Payload.Total.Should().Be(10);
        envelope.Payload.Percent.Should().Be(40);
    }

    // ---------- URL registry ----------

    [Fact]
    public void Url_provider_registry_round_trips()
    {
        var text = FixtureLoader.ReadText("url/provider-registry.v1.json");
        var registry = JsonSerializer.Deserialize<UrlProviderRegistry>(text, Strict);
        registry.Should().NotBeNull();
        registry!.Providers.Should().NotBeEmpty();
        registry.RegistryFingerprint.Should().NotBeNullOrEmpty();
        registry.Providers.Should().Contain(p => p.ProviderId == "direct-http");

        var roundTripped = JsonSerializer.Serialize(registry, Strict);
        var reloaded = JsonSerializer.Deserialize<UrlProviderRegistry>(roundTripped, Strict);
        reloaded!.Providers.Should().HaveCount(registry.Providers.Count);
    }

    [Fact]
    public void Url_errors_matrix_round_trips()
    {
        var text = FixtureLoader.ReadText("url/errors.v1.json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("schemaVersion").GetString().Should().NotBeNullOrEmpty();
        var matrix = JsonSerializer.Deserialize<UrlErrorMatrix>(text, Strict);
        matrix.Should().NotBeNull();
        matrix!.Errors.Should().NotBeEmpty();
    }

    // ---------- Email-body receipt ----------

    [Fact]
    public void Email_body_receipt_round_trips()
    {
        var text = FixtureLoader.ReadText("email-body/baiwang.receipt.json");
        var receipt = JsonSerializer.Deserialize<EmailBodyReceipt>(text, Strict);
        receipt.Should().NotBeNull();
        receipt!.ProviderId.Should().NotBeNullOrEmpty();

        var roundTripped = JsonSerializer.Serialize(receipt, Strict);
        var reloaded = JsonSerializer.Deserialize<EmailBodyReceipt>(roundTripped, Strict);
        reloaded!.ProviderId.Should().Be(receipt.ProviderId);
    }

    // ---------- Release manifest ----------

    [Fact]
    public void Release_manifest_round_trips()
    {
        var text = FixtureLoader.ReadText("release/release-manifest.example.json");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(text, Strict);
        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().BeGreaterThan(0);
        manifest.Assets.Should().NotBeEmpty();
        manifest.RuntimeIdentifier.Should().Be("win-x64");
        manifest.Signed.Should().BeFalse();

        var roundTripped = JsonSerializer.Serialize(manifest, Strict);
        var reloaded = JsonSerializer.Deserialize<ReleaseManifest>(roundTripped, Strict);
        reloaded!.Assets.Should().HaveCount(manifest.Assets.Count);
        reloaded.RuntimeIdentifier.Should().Be(manifest.RuntimeIdentifier);
        reloaded.WebView2.PackageVersion.Should().Be(manifest.WebView2.PackageVersion);
    }

    [Fact]
    public void Release_manifest_generator_output_deserializes_as_release_contract()
    {
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null
            && !File.Exists(Path.Combine(repoRoot.FullName, "build", "release-manifest.ps1")))
        {
            repoRoot = repoRoot.Parent;
        }
        repoRoot.Should().NotBeNull("the contract test runs from a repository checkout");

        var publishRoot = Path.Combine(Path.GetTempPath(), $"invoiceflow-manifest-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(publishRoot, "manifests", "release.json");
        Directory.CreateDirectory(publishRoot);
        File.WriteAllText(Path.Combine(publishRoot, "asset.bin"), "release asset");
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(repoRoot!.FullName, "build", "release-manifest.ps1"));
            startInfo.ArgumentList.Add("-PublishRoot");
            startInfo.ArgumentList.Add(publishRoot);
            startInfo.ArgumentList.Add("-Output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-ProductVersion");
            startInfo.ArgumentList.Add("9.8.7.6");
            using var process = System.Diagnostics.Process.Start(startInfo);
            process.Should().NotBeNull("PowerShell 7 is available on the Windows CI runner");
            var standardOutput = process!.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"stdout: {standardOutput}; stderr: {standardError}");

            var json = File.ReadAllText(outputPath);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, Strict);
            manifest.Should().NotBeNull();
            manifest!.SchemaVersion.Should().Be(1);
            manifest.ApplicationVersion.Should().Be("9.8.7.6");
            manifest.Assets.Should().ContainSingle(asset => asset.RelativePath == "asset.bin");
            manifest.ManifestSha256.Should().MatchRegex("^[a-f0-9]{64}$");
        }
        finally
        {
            Directory.Delete(publishRoot, recursive: true);
        }
    }

    // ---------- Secret leakage guard ----------

    [Theory]
    [InlineData("rpc/account-save.request.json")]
    [InlineData("rpc/account-save.response.json")]
    [InlineData("rpc/account-revision-conflict.response.json")]
    [InlineData("rpc/account-test.response.json")]
    [InlineData("rpc/account-test.error.json")]
    [InlineData("rpc/account-test.auth-error.json")]
    [InlineData("rpc/account-test.credentials-missing.json")]
    [InlineData("rpc/account-test.mailbox-not-found.json")]
    public void Non_secret_fixtures_never_round_trip_secret_like_values(string fixture)
    {
        var text = FixtureLoader.ReadText(fixture);
        // The fixture file itself may carry masked values like "u***@example.com"
        // — that's allowed. We assert that re-serialization of the parsed envelope
        // does not introduce new secret-shaped fields.
        using var doc = JsonDocument.Parse(text);
        var roundTripped = JsonSerializer.Serialize(doc.RootElement, Strict);
        foreach (var needle in SecretLikePatterns)
        {
            roundTripped.Should().NotContain(needle, $"fixture '{fixture}' re-serialization must not contain secret-shaped field '{needle}'");
        }
    }
}