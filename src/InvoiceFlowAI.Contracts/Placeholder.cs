// Placeholder for InvoiceFlowAI.Contracts. RPC DTOs and JSON serializer context land in
// Task 2 of the migration implementation plan. Per design §3 the layer carries cross-
// tier DTOs with stable JSON contract and unknown-field rejection.

namespace InvoiceFlowAI.Contracts;

internal static class ContractsPlaceholder
{
    internal static string Marker { get; } = "InvoiceFlowAI.Contracts scaffold " + System.DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
}
