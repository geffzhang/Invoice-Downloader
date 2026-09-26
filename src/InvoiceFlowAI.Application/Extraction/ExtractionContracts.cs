using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Extraction;

public enum ExtractionRoute
{
    LocalFastPath,
    OcrText,
    VisionFallback,
}

public sealed record ExtractionTrace(
    ExtractionRoute Route,
    string TrackAStatus,
    string TrackBStatus,
    string ReasonCode,
    TimeSpan Duration,
    string Model,
    string InputKind,
    string? ResponseFingerprint = null);

public sealed record DocumentSource(
    DocumentIdentity Identity,
    string LocalPath = "",
    string SourceUrl = "",
    string MimeType = "",
    string ContentHash = "",
    string AttachmentPartId = "",
    string Mailbox = "",
    string Subject = "",
    string Sender = "");

public sealed record BoundingBox(float X, float Y, float Width, float Height);

public sealed record OcrLine(
    string Text,
    decimal Confidence,
    BoundingBox Bounds,
    int PageNumber);

public sealed record RenderedPage(
    int PageNumber,
    int Width,
    int Height,
    string MediaType,
    ReadOnlyMemory<byte> ImageBytes);

public sealed record PdfRenderOptions(
    int Dpi = 200,
    int MaximumPages = 2,
    int MaximumWidth = 4096,
    int MaximumHeight = 8192,
    long MaximumImageBytes = 10 * 1024 * 1024,
    bool PreprocessForOcr = true);