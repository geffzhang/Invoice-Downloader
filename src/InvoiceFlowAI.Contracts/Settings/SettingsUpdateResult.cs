namespace InvoiceFlowAI.Contracts.Settings;

public sealed record SettingsUpdateResult(
    int Revision,
    string ConfigurationFingerprint,
    IReadOnlyList<string> ChangedSections);