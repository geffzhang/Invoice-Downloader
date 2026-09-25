using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Extraction;

public sealed class InvoiceFieldExtractor : IInvoiceFieldExtractor
{
    private readonly IChatCompletionService _chat;
    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrFallback _ocr;
    private readonly InvoiceNormalizer _normalizer;
    private readonly IInvoiceAcceptanceService _acceptance;
    private readonly AiAuthenticationFailureGate _authGate;

    public InvoiceFieldExtractor(
        IChatCompletionService chat,
        IPdfPageRenderer renderer,
        IOcrFallback ocr,
        InvoiceNormalizer normalizer,
        IInvoiceAcceptanceService acceptance,
        AiAuthenticationFailureGate authGate)
    {
        _chat = chat;
        _renderer = renderer;
        _ocr = ocr;
        _normalizer = normalizer;
        _acceptance = acceptance;
        _authGate = authGate;
    }

    public async Task<FieldExtractionResult> ExtractAsync(FieldExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (request.Candidate.DocumentId != request.Source.Identity
            || !string.Equals(request.Candidate.DocumentId.Value, request.Source.Identity.Value, StringComparison.Ordinal))
        {
            return Failure(request, ExtractionRoute.LocalFastPath, "IDENTITY_MISMATCH", AcceptanceDisposition.Rejected, stopwatch);
        }
        if (_authGate.IsBlocked)
        {
            return Failure(request, ExtractionRoute.OcrText, "AI_AUTHENTICATION_FAILED", AcceptanceDisposition.Rejected, stopwatch);
        }

        IReadOnlyList<RenderedPage> pages = Array.Empty<RenderedPage>();
        var text = request.EmbeddedText ?? string.Empty;
        var route = string.IsNullOrWhiteSpace(request.EmbeddedText) ? ExtractionRoute.OcrText : ExtractionRoute.OcrText;
        var trackAStatus = "NOT_RUN";
        var trackBStatus = "NOT_RUN";
        string? responseFingerprint = null;
        InvoiceAcceptanceResult? trackAResult = null;

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < request.Rules.MinimumOcrTextCharacters)
        {
            try
            {
                pages = await _renderer.RenderAsync(
                    request.Source,
                    request.Rules.RenderOptions ?? new PdfRenderOptions(),
                    cancellationToken).ConfigureAwait(false);
                var ocrOutcome = await _ocr.RecognizeAsync(request.Candidate.DocumentId, pages, cancellationToken).ConfigureAwait(false);
                text = string.Join("\n", ocrOutcome.Lines.OrderBy(line => line.PageNumber).ThenBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).Select(line => line.Text));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                trackAStatus = "OCR_FAILED";
            }
        }

        if (text.Trim().Length >= request.Rules.MinimumOcrTextCharacters)
        {
            try
            {
                var response = await _chat.CompleteTextAsync(
                    InvoiceExtractionPrompt.CreateTrackA(text, request.Rules), cancellationToken).ConfigureAwait(false);
                responseFingerprint = Fingerprint(response.Text);
                var fields = ApplyCorrections(InvoiceResponseSchema.Parse(response.Text), request.DeterministicCorrections);
                var document = ToDocument(fields, request, response.Model);
                trackAResult = _acceptance.Evaluate(new InvoiceAcceptanceRequest(
                    request.Candidate,
                    _normalizer.Normalize(document, request.Candidate.DocumentId),
                    request.Rules.AcceptancePolicy ?? new InvoiceAcceptancePolicy(),
                    request.Rules.CompanyName,
                    IsVisionFallback: false,
                    SourceParser: "deepseek-track-a"));
                trackAStatus = trackAResult.Disposition.ToString().ToUpperInvariant();
                if (trackAResult.Disposition == AcceptanceDisposition.Accepted)
                {
                    return Complete(request, trackAResult, ExtractionRoute.OcrText, trackAStatus, trackBStatus, stopwatch, response.Model, responseFingerprint);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ChatCompletionException exception) when (exception.Code == ChatCompletionErrorCode.Unauthorized)
            {
                _authGate.TryBlock();
                return Failure(request, ExtractionRoute.OcrText, "AI_AUTHENTICATION_FAILED", AcceptanceDisposition.Rejected, stopwatch);
            }
            catch
            {
                trackAStatus = "INVALID_RESPONSE";
            }
        }
        else if (trackAStatus == "NOT_RUN")
        {
            trackAStatus = "OCR_TEXT_TOO_SHORT";
        }

        if (!request.AllowVisionFallback)
        {
            return FromTrackAOrFailure(request, trackAResult, ExtractionRoute.OcrText, trackAStatus, trackBStatus, stopwatch, responseFingerprint);
        }
        if (_authGate.IsBlocked)
        {
            return Failure(request, ExtractionRoute.OcrText, "AI_AUTHENTICATION_FAILED", AcceptanceDisposition.Rejected, stopwatch);
        }

        if (pages.Count == 0)
        {
            try
            {
                pages = await _renderer.RenderAsync(
                    request.Source,
                    request.Rules.RenderOptions ?? new PdfRenderOptions(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                trackBStatus = "RENDER_FAILED";
                return Failure(request, ExtractionRoute.VisionFallback, "AI_VISION_FAILED", AcceptanceDisposition.ManualReview, stopwatch);
            }
        }
        if (pages.Count is < 1 or > 2)
        {
            trackBStatus = "INVALID_PAGE_COUNT";
            return Failure(request, ExtractionRoute.VisionFallback, "AI_VISION_FAILED", AcceptanceDisposition.ManualReview, stopwatch);
        }

        try
        {
            var images = pages.OrderBy(page => page.PageNumber)
                .Select(page => new ChatImagePart(page.MediaType, page.ImageBytes, page.Width, page.Height))
                .ToArray();
            var response = await _chat.CompleteVisionAsync(
                InvoiceExtractionPrompt.CreateTrackB(request.Rules), images, cancellationToken).ConfigureAwait(false);
            responseFingerprint = Fingerprint(response.Text);
            var fields = ApplyCorrections(InvoiceResponseSchema.Parse(response.Text), request.DeterministicCorrections);
            var document = ToDocument(fields, request, response.Model);
            var acceptance = _acceptance.Evaluate(new InvoiceAcceptanceRequest(
                request.Candidate,
                _normalizer.Normalize(document, request.Candidate.DocumentId),
                request.Rules.AcceptancePolicy ?? new InvoiceAcceptancePolicy(),
                request.Rules.CompanyName,
                IsVisionFallback: true,
                SourceParser: "deepseek-track-b"));
            trackBStatus = acceptance.Disposition.ToString().ToUpperInvariant();
            return Complete(request, acceptance, ExtractionRoute.VisionFallback, trackAStatus, trackBStatus, stopwatch, response.Model, responseFingerprint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChatCompletionException exception) when (exception.Code == ChatCompletionErrorCode.Unauthorized)
        {
            _authGate.TryBlock();
            return Failure(request, ExtractionRoute.VisionFallback, "AI_AUTHENTICATION_FAILED", AcceptanceDisposition.Rejected, stopwatch);
        }
        catch
        {
            return Failure(request, ExtractionRoute.VisionFallback, "AI_VISION_FAILED", AcceptanceDisposition.ManualReview, stopwatch);
        }
    }

    private static InvoiceResponseFields ApplyCorrections(InvoiceResponseFields fields, InvoiceResponseFields? corrections) => corrections is null
        ? fields
        : fields with
        {
            IsInvoice = corrections.IsInvoice ?? fields.IsInvoice,
            InvoiceDate = corrections.InvoiceDate ?? fields.InvoiceDate,
            Purchaser = corrections.Purchaser ?? fields.Purchaser,
            Seller = corrections.Seller ?? fields.Seller,
            Amount = corrections.Amount ?? fields.Amount,
            TaxAmount = corrections.TaxAmount ?? fields.TaxAmount,
            TotalAmount = corrections.TotalAmount ?? fields.TotalAmount,
            InvoiceCode = corrections.InvoiceCode ?? fields.InvoiceCode,
            InvoiceNumber = corrections.InvoiceNumber ?? fields.InvoiceNumber,
            DocumentType = corrections.DocumentType ?? fields.DocumentType,
            Category = corrections.Category ?? fields.Category,
            Confidence = corrections.Confidence ?? fields.Confidence,
            Flags = corrections.Flags ?? fields.Flags,
            Route = corrections.Route ?? fields.Route,
            Items = corrections.Items ?? fields.Items,
        };

    private static InvoiceDocument ToDocument(InvoiceResponseFields fields, FieldExtractionRequest request, string model)
    {
        var route = fields.Route is null
            ? null
            : new InvoiceRoute(
                fields.Route.Direction,
                fields.Route.DepartureDate,
                fields.Route.DepartureCity ?? string.Empty,
                fields.Route.DestinationCity ?? string.Empty);
        var items = fields.Items?.Select(item => new InvoiceItem(
            item.Name ?? string.Empty,
            item.Quantity ?? 0m,
            item.UnitPrice ?? 0m,
            item.Amount ?? 0m,
            item.TaxAmount,
            item.Unit,
            item.Specification)).ToArray() ?? Array.Empty<InvoiceItem>();
        var flags = fields.Flags?.Aggregate(InvoiceFlags.None, (current, value) => current | value) ?? InvoiceFlags.None;
        return new InvoiceDocument(
            request.Candidate.DocumentId.Value,
            fields.InvoiceDate,
            fields.Purchaser ?? string.Empty,
            fields.Seller ?? string.Empty,
            fields.Amount,
            fields.TaxAmount,
            fields.TotalAmount,
            fields.InvoiceCode,
            fields.InvoiceNumber,
            fields.DocumentType ?? InvoiceDocumentType.Unrecognized,
            fields.Category,
            route,
            items,
            request.Candidate.OriginalFileName,
            request.Source.ContentHash)
        {
            Identity = request.Candidate.DocumentId,
            IsInvoice = fields.IsInvoice ?? true,
            Flags = flags,
            Confidence = fields.Confidence ?? 0m,
            ParserName = "deepseek",
            ExtractionRevision = $"{InvoiceResponseSchema.Version}:{model}",
        };
    }

    private static FieldExtractionResult FromTrackAOrFailure(
        FieldExtractionRequest request,
        InvoiceAcceptanceResult? result,
        ExtractionRoute route,
        string trackAStatus,
        string trackBStatus,
        Stopwatch stopwatch,
        string? responseFingerprint)
    {
        if (result is not null)
        {
            return Complete(request, result, route, trackAStatus, trackBStatus, stopwatch, "deepseek-flash", responseFingerprint);
        }
        return Failure(request, route, trackAStatus == "OCR_TEXT_TOO_SHORT" ? "OCR_TEXT_TOO_SHORT" : "AI_TEXT_FAILED", AcceptanceDisposition.Rejected, stopwatch);
    }

    private static FieldExtractionResult Complete(
        FieldExtractionRequest request,
        InvoiceAcceptanceResult acceptance,
        ExtractionRoute route,
        string trackAStatus,
        string trackBStatus,
        Stopwatch stopwatch,
        string model,
        string? responseFingerprint) => new(
            request.Candidate.DocumentId,
            acceptance.Document,
            acceptance.Disposition,
            acceptance.Failures,
            acceptance.Warnings,
            route,
            acceptance.ReasonCode,
            new ExtractionTrace(route, trackAStatus, trackBStatus, acceptance.ReasonCode, stopwatch.Elapsed, model, "document", responseFingerprint));

    private static FieldExtractionResult Failure(
        FieldExtractionRequest request,
        ExtractionRoute route,
        string reasonCode,
        AcceptanceDisposition disposition,
        Stopwatch stopwatch) => new(
            request.Candidate.DocumentId,
            null,
            disposition,
            [new InvoiceAcceptanceFailure(reasonCode, InvoiceFlowAI.Domain.Candidates.FailureCategory.Document, false, "Invoice extraction could not be completed.")],
            Array.Empty<string>(),
            route,
            reasonCode,
            new ExtractionTrace(route, "FAILED", "FAILED", reasonCode, stopwatch.Elapsed, string.Empty, "document"));

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
