namespace InvoiceFlowAI.Contracts.Errors;

/// <summary>
/// Stable, server-emitted RPC error codes. Codes are part of the public
/// contract — adding a new value is non-breaking; renaming or repurposing
/// a value is breaking and must bump the protocol version.
/// </summary>
public static class RpcErrorCodes
{
    public const string RpcInvalidJson = "RPC_INVALID_JSON";
    public const string RpcProtocolUnsupported = "RPC_PROTOCOL_UNSUPPORTED";
    public const string RpcMethodNotFound = "RPC_METHOD_NOT_FOUND";
    public const string RpcInvalidParams = "RPC_INVALID_PARAMS";
    public const string RpcMessageTooLarge = "RPC_MESSAGE_TOO_LARGE";

    public const string RunAlreadyActive = "RUN_ALREADY_ACTIVE";
    public const string RunNotFound = "RUN_NOT_FOUND";
    public const string RunNotCancellable = "RUN_NOT_CANCELLABLE";
    public const string RunFailed = "RUN_FAILED";
    public const string RunConfigurationSnapshotMissing = "RUN_CONFIGURATION_SNAPSHOT_MISSING";
    public const string RunRetrySelectionInvalid = "RUN_RETRY_SELECTION_INVALID";

    public const string ReviewRevisionConflict = "REVIEW_REVISION_CONFLICT";
    public const string SettingsRevisionConflict = "SETTINGS_REVISION_CONFLICT";

    public const string CredentialsNotConfigured = "CREDENTIALS_NOT_CONFIGURED";
    public const string PersistenceUnavailable = "PERSISTENCE_UNAVAILABLE";
    public const string WebviewBridgeNotReady = "WEBVIEW_BRIDGE_NOT_READY";
    public const string WebviewRuntimeUnavailable = "WEBVIEW_RUNTIME_UNAVAILABLE";
    public const string WebAssetInvalid = "WEB_ASSET_INVALID";
    public const string RecipeSchemaUnsupported = "RECIPE_SCHEMA_UNSUPPORTED";
    public const string RecipeGraphInvalid = "RECIPE_GRAPH_INVALID";
    public const string DbMigrationFailed = "DB_MIGRATION_FAILED";
    public const string DbCorrupted = "DB_CORRUPTED";

    public const string ProviderRuleConflict = "PROVIDER_RULE_CONFLICT";
    public const string SpecialParserConflict = "SPECIAL_PARSER_CONFLICT";
    public const string RulesetRevisionConflict = "RULESET_REVISION_CONFLICT";
    public const string RulesetVersionNotFound = "RULESET_VERSION_NOT_FOUND";
    public const string RulesetInvalid = "RULESET_INVALID";

    public const string MailboxAccountRevisionConflict = "MAILBOX_ACCOUNT_REVISION_CONFLICT";
    public const string ReportExportFailed = "REPORT_EXPORT_FAILED";
}