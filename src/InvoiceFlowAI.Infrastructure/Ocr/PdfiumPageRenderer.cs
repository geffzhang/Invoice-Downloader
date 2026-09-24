using InvoiceFlowAI.Application.Extraction;
using PDFiumCore;

namespace InvoiceFlowAI.Infrastructure.Ocr;

public sealed class PdfiumPageRenderer : IPdfPageRenderer
{
    private const int MaximumPages = 2;
    private const int MaximumWidth = 4096;
    private const int MaximumHeight = 8192;
    private static readonly object PdfiumGate = new();
    private static bool _initialized;

    public Task<IReadOnlyList<RenderedPage>> RenderAsync(
        DocumentSource source,
        PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(source.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(source.LocalPath)
            || !File.Exists(source.LocalPath))
        {
            throw new InvalidDataException("The document is not an available PDF source.");
        }

        if (options.Dpi <= 0 || options.MaximumPages <= 0 || options.MaximumWidth <= 0
            || options.MaximumHeight <= 0 || options.MaximumImageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PDF render limits must be positive.");
        }

        lock (PdfiumGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializePdfium();
            return Task.FromResult(RenderCore(source.LocalPath, options, cancellationToken));
        }
    }

    private static IReadOnlyList<RenderedPage> RenderCore(
        string path,
        PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null!);
        if (document is null || document.__Instance == IntPtr.Zero)
        {
            throw new InvalidDataException("The PDF document could not be opened.");
        }

        try
        {
            var pageCount = fpdfview.FPDF_GetPageCount(document);
            if (pageCount <= 0)
            {
                throw new InvalidDataException("The PDF document contains no pages.");
            }

            var count = Math.Min(pageCount, Math.Min(options.MaximumPages, MaximumPages));
            var maxWidth = Math.Min(options.MaximumWidth, MaximumWidth);
            var maxHeight = Math.Min(options.MaximumHeight, MaximumHeight);
            var output = new List<RenderedPage>(count);
            for (var pageIndex = 0; pageIndex < count; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = fpdfview.FPDF_LoadPage(document, pageIndex);
                if (page is null || page.__Instance == IntPtr.Zero)
                {
                    throw new InvalidDataException("A PDF page could not be opened.");
                }

                try
                {
                    var pointsWide = fpdfview.FPDF_GetPageWidth(page);
                    var pointsHigh = fpdfview.FPDF_GetPageHeight(page);
                    if (!double.IsFinite(pointsWide) || !double.IsFinite(pointsHigh) || pointsWide <= 0 || pointsHigh <= 0)
                    {
                        throw new InvalidDataException("The PDF page has invalid dimensions.");
                    }

                    var scale = Math.Min(options.Dpi / 72d,
                        Math.Min(maxWidth / pointsWide, maxHeight / pointsHigh));
                    var width = Math.Max(1, (int)Math.Floor(pointsWide * scale));
                    var height = Math.Max(1, (int)Math.Floor(pointsHigh * scale));
                    var estimatedRawBytes = checked((long)width * height * 4);
                    if (estimatedRawBytes > options.MaximumImageBytes)
                    {
                        throw new InvalidDataException("Rendered image exceeds the configured byte limit.");
                    }

                    var bitmap = fpdfview.FPDFBitmapCreate(width, height, (int)FPDFBitmapFormat.BGRA);
                    if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
                    {
                        throw new InvalidDataException("The PDF page could not be rendered.");
                    }

                    try
                    {
                        fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
                        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                        if (buffer == IntPtr.Zero || stride < checked(width * 4))
                        {
                            throw new InvalidDataException("The rendered PDF page is unavailable.");
                        }

                        var bytes = SkiaImagePreprocessor.EncodePng(buffer, width, height, stride, options.MaximumImageBytes);
                        output.Add(new RenderedPage(pageIndex + 1, width, height, "image/png", bytes));
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(bitmap);
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            return output;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static void InitializePdfium()
    {
        if (_initialized)
        {
            return;
        }

        fpdfview.FPDF_InitLibrary();
        _initialized = true;
    }
}