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
            if (string.IsNullOrWhiteSpace(rule.RuleId))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    "Every rule requires a non-empty identifier.");
            }

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

            if (!HasEffectiveMatchCriteria(rule.When))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' must define at least one effective match criterion.");
            }

            if (!HasEffectiveAction(rule.Then))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' must define an effective archive destination and category.");
            }
        }
    }

    private static bool HasEffectiveMatchCriteria(RuleRuleWhen when)
    {
        return !string.IsNullOrWhiteSpace(when.DocumentType)
            || !string.IsNullOrWhiteSpace(when.SellerContains);
    }

    private static bool HasEffectiveAction(RuleRuleThen then)
    {
        return !string.IsNullOrWhiteSpace(then.ArchiveFolder)
            && !string.IsNullOrWhiteSpace(then.Category);
    }
}