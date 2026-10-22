namespace InvoiceFlowAI.App.ViewModels;

public sealed record ProcessingOptions(TimeSpan PollInterval, TimeSpan TerminalNavigationDelay)
{
    public static ProcessingOptions Production { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1200));

    public static ProcessingOptions Fast { get; } = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
}
