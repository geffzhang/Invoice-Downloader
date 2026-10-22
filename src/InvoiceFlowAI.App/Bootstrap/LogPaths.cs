namespace InvoiceFlowAI.App.Bootstrap;

public static class LogPaths
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InvoiceFlowAI",
        "logs");

    public static string ApplicationLogFile => Path.Combine(LogDirectory, "invoiceflow-.log");

    public static void EnsureLogDirectory() => Directory.CreateDirectory(LogDirectory);
}
