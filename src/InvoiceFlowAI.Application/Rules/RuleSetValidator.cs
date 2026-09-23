using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rules;

namespace InvoiceFlowAI.Application.Rules;

/// <summary>
/// Validates a <see cref="RuleSetDocument"/> against the schema rules
/// pinned by design §3. Throws <see cref="RuleSetValidationException"/>
/// with a stable reason code on the first failure; never returns a
/// partially-validated document.
/// </summary>
public sealed class RuleSetValidator
{
    private static readonly HashSet<string> SupportedSchemaVersions = new(StringComparer.Ordinal)
    {
        "1.0",
    };

    public void Validate(RuleSetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!SupportedSchemaVersions.Contains(document.SchemaVersion))
        {
            throw new RuleSetValidationException(
                RpcErrorCodes.RecipeSchemaUnsupported,
                $"Unsupported RuleSet schema version '{document.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.RuleSetId))
        {
            throw new RuleSetValidationException(
                RpcErrorCodes.RulesetInvalid,
                "RuleSet identifier is required.");
        }

        var seenRuleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in document.Rules)
        {
            if (!seenRuleIds.Add(rule.RuleId))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Duplicate rule identifier '{rule.RuleId}'.");
            }

            if (rule.Priority < 0 || rule.Priority > 1_000_000)
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' priority {rule.Priority} is out of range [0..1000000].");
            }

            if (string.IsNullOrWhiteSpace(rule.Then.ArchiveFolder))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' archive folder is required.");
            }
        }
    }
}