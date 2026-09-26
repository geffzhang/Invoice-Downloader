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

    private static readonly HashSet<string> SupportedPurchaserRelations = new(StringComparer.Ordinal)
    {
        "target",
        "non_target",
        "unknown",
    };

    private static readonly HashSet<string> SupportedDocumentTypes = new(StringComparer.Ordinal)
    {
        "RailwayTicket",
        "TrainTicket",
        "AccommodationFolio",
        "HotelFolio",
        "HotelInvoice",
        "FlightInvoice",
        "AirTicket",
        "Catering",
        "TaxInvoice",
        "VatInvoice",
        "RideInvoice",
        "RideItinerary",
        "Other",
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

        if (document.Rules is null || document.Rules.Count == 0)
        {
            throw new RuleSetValidationException(
                RpcErrorCodes.RulesetInvalid,
                "At least one rule is required.");
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

            if (rule.When.PurchaserRelation is not null
                && !SupportedPurchaserRelations.Contains(rule.When.PurchaserRelation))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' has an unsupported purchaser relation.");
            }

            if (rule.When.DocumentType is not null
                && !SupportedDocumentTypes.Contains(rule.When.DocumentType))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' has an unsupported document type.");
            }

            if (!HasEffectiveMatchCriteria(rule.When))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' must define at least one effective match criterion.");
            }

            if (!IsSafeArchiveFolder(rule.Then.ArchiveFolder))
            {
                throw new RuleSetValidationException(
                    RpcErrorCodes.RulesetInvalid,
                    $"Rule '{rule.RuleId}' has an unsafe archive folder path.");
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
            || !string.IsNullOrWhiteSpace(when.SellerContains)
            || !string.IsNullOrWhiteSpace(when.ProviderFamily)
            || !string.IsNullOrWhiteSpace(when.PurchaserRelation)
            || !string.IsNullOrWhiteSpace(when.SubjectContains)
            || !string.IsNullOrWhiteSpace(when.InvoiceNumberPrefix);
    }

    private static bool HasEffectiveAction(RuleRuleThen then)
    {
        return !string.IsNullOrWhiteSpace(then.ArchiveFolder)
            && !string.IsNullOrWhiteSpace(then.Category);
    }

    private static bool IsSafeArchiveFolder(string? archiveFolder)
    {
        if (string.IsNullOrWhiteSpace(archiveFolder)
            || archiveFolder.StartsWith('/')
            || archiveFolder.Contains('\\'))
        {
            return false;
        }

        foreach (var segment in archiveFolder.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment != segment.Trim()
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.Any(character => char.IsControl(character) || "<>:\"|?*".IndexOf(character) >= 0))
            {
                return false;
            }
        }

        return true;
    }
}