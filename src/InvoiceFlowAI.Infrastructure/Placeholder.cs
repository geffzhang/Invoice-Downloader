// Placeholder for InvoiceFlowAI.Infrastructure. EF Core persistence, DPAPI secret
// store, legacy importer, MailKit, OCR, DeepSeek, archive, and ClosedXML report
// exporter land in Tasks 4-10 per the migration implementation plan. Per design §3
// the layer implements application interfaces and never leaks supplier SDK types back
// to Application.

namespace InvoiceFlowAI.Infrastructure;

internal static class InfrastructurePlaceholder
{
    internal static string Marker { get; } = "InvoiceFlowAI.Infrastructure scaffold " + System.DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
}
