using UglyToad.PdfPig;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed record PdfPageText(int PageNumber, string Text);

public sealed record PdfTextExtractionOptions(
    int MaximumPages = 256,
    int MaximumTextCharacters = 5_000_000,
    int MaximumInputBytes = 25 * 1024 * 1024);

public sealed class PdfTextLimitException(string message) : InvalidOperationException(message);

public sealed class PdfTextExtractor
{
    private readonly PdfTextExtractionOptions _options;

    public PdfTextExtractor() : this(new PdfTextExtractionOptions())
    {
    }

    public PdfTextExtractor(PdfTextExtractionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaximumPages < 1 || _options.MaximumTextCharacters < 1 || _options.MaximumInputBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PDF extraction limits must be positive.");
        }
    }

    public IReadOnlyList<PdfPageText> ExtractPages(ReadOnlyMemory<byte> pdfBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pdfBytes.Length > _options.MaximumInputBytes)
        {
            throw new PdfTextLimitException("PDF input exceeds the configured byte limit.");
        }
        using var document = PdfDocument.Open(pdfBytes.ToArray());
        if (document.NumberOfPages > _options.MaximumPages)
        {
            throw new PdfTextLimitException("PDF exceeds the configured page limit.");
        }
        var pages = new List<PdfPageText>(document.NumberOfPages);
        var totalCharacters = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = page.Text;
            totalCharacters = checked(totalCharacters + text.Length);
            if (totalCharacters > _options.MaximumTextCharacters)
            {
                throw new PdfTextLimitException("PDF extracted text exceeds the configured character limit.");
            }
            pages.Add(new PdfPageText(page.Number, text));
        }
        return pages;
    }
}