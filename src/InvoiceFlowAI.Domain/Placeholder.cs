// Placeholder for InvoiceFlowAI.Domain. Real Domain types land in Tasks 2-6 per the
// migration implementation plan. Per design §3 the layer must remain free of EF Core,
// HTTP, MailKit, PdfPig/PDFium, SkiaSharp, and ZeroPipeline. Domain does not know about
// WebView2 or supplier SDKs. This file keeps the project compiling for the initial
// `dotnet restore` / `dotnet build` baseline and will be replaced when the Domain records
// are added in Task 2 (ContractRoundTripTests, DocumentIdentity, CandidateProcessResult...).

namespace InvoiceFlowAI.Domain;

public static class DomainPlaceholder
{
    public static string Marker { get; } = "InvoiceFlowAI.Domain scaffold " + System.DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
}
