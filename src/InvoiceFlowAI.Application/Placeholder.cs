// Placeholder for InvoiceFlowAI.Application. RecipeRegistry, RuleSetBootstrapper,
// RunCoordinator, and other orchestration services land in Tasks 3-5 per the migration
// implementation plan. Per design §3 the layer depends on ZeroPipeline Core/Recipe and
// owns the run lifecycle and pipeline execution model.

namespace InvoiceFlowAI.Application;

internal static class ApplicationPlaceholder
{
    internal static string Marker { get; } = "InvoiceFlowAI.Application scaffold " + System.DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
}
