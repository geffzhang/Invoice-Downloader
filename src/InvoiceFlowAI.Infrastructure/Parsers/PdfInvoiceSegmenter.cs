using System.Text.RegularExpressions;
using System.Text;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed record PdfInvoiceSegment(string InvoiceNumber, int StartPage, string Text);

public sealed class PdfInvoiceSegmenter
{
    private static readonly Regex InvoiceNumberPattern = new(
        @"(?:发票号码|Invoice\s*Number|Invoice\s*No\.?)[\s:：#]*([A-Za-z0-9-]{6,24})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<PdfInvoiceSegment> Segment(IReadOnlyList<PdfPageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var segments = new List<PdfInvoiceSegment>();
        StringBuilder? current = null;
        var preamble = new StringBuilder();
        string currentNumber = string.Empty;
        var currentPage = 0;

        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            foreach (var line in page.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = InvoiceNumberPattern.Match(line);
                if (match.Success)
                {
                    if (current is not null)
                    {
                        segments.Add(new PdfInvoiceSegment(currentNumber, currentPage, current.ToString().Trim()));
                    }
                    current = current is null ? new StringBuilder(preamble.ToString()) : new StringBuilder();
                    currentNumber = match.Groups[1].Value.Trim();
                    currentPage = page.PageNumber;
                }
                else if (current is null)
                {
                    preamble.AppendLine(line.Trim());
                }
                if (current is not null)
                {
                    current.AppendLine(line.Trim());
                }
            }
        }

        if (current is not null)
        {
            segments.Add(new PdfInvoiceSegment(currentNumber, currentPage, current.ToString().Trim()));
        }
        return segments;
    }
}