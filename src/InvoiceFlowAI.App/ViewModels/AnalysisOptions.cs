namespace InvoiceFlowAI.App.ViewModels;

public sealed record AnalysisOptions(TimeSpan PollInterval)
{
    public static AnalysisOptions Production { get; } = new(TimeSpan.FromSeconds(3));

    public static AnalysisOptions Fast { get; } = new(TimeSpan.FromMilliseconds(10));
}
