using System.Reflection;

namespace InvoiceFlowAI.Application.Configuration;

public static class DefaultPipelineRecipeLoader
{
    private const string ResourceName = "InvoiceFlowAI.Application.Configuration.DefaultPipelineRecipe.json";

    public static Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded default pipeline recipe is missing.");
        using var reader = new StreamReader(stream);
        return Task.FromResult(reader.ReadToEnd());
    }
}