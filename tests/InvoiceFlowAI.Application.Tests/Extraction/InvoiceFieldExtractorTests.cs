using FluentAssertions;
using System.Text.Json;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Extraction;

public sealed class InvoiceFieldExtractorTests
{
    [Fact]
    public async Task Track_a_acceptance_does_not_call_vision()
    {
        var chat = new FakeChatClient(ValidResponse);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Disposition.Should().Be(AcceptanceDisposition.Accepted);
        result.Route.Should().Be(ExtractionRoute.OcrText);
        chat.TextCalls.Should().Be(1);
        chat.VisionCalls.Should().Be(0);
        chat.LastTextRequest!.Model.Should().Be("deepseek-flash");
        chat.LastTextRequest.Temperature.Should().Be(0.1);
    }

    [Fact]
    public async Task Track_b_sends_at_most_two_images_in_page_order()
    {
        var secondPage = new RenderedPage(2, 2, 1, "image/png", new byte[] { 2 });
        var firstPage = new RenderedPage(1, 1, 1, "image/png", new byte[] { 1 });
        var chat = new FakeChatClient(MissingNumberResponse, ValidResponse);
        var extractor = NewExtractor(chat, new FakeRenderer([secondPage, firstPage]));

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        chat.LastVisionRequest!.Model.Should().Be("deepseek-flash");
        chat.LastVisionImages.Select(image => image.Bytes.Span[0]).Should().Equal(1, 2);
        chat.LastVisionImages.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Missing_required_field_invokes_vision_when_allowed()
    {
        var chat = new FakeChatClient(MissingNumberResponse, ValidResponse);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("ACCEPTANCE_LOW_CONFIDENCE");
        chat.TextCalls.Should().Be(1);
        chat.VisionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Ocr_failure_skips_track_a_and_uses_vision_when_allowed()
    {
        var chat = new FakeChatClient(ValidResponse);
        var renderer = new FakeRenderer([TestPage]);
        var ocr = new FakeOcr(new InvalidOperationException("private OCR detail"));
        var extractor = NewExtractor(chat, renderer, ocr);

        var result = await extractor.ExtractAsync(NewRequest(), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        chat.TextCalls.Should().Be(0);
        chat.VisionCalls.Should().Be(1);
        ocr.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Too_short_ocr_text_uses_vision_without_track_a()
    {
        var chat = new FakeChatClient(ValidResponse);
        var extractor = NewExtractor(chat, new FakeRenderer([TestPage]), new FakeOcr("short"));

        var result = await extractor.ExtractAsync(NewRequest(), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        chat.TextCalls.Should().Be(0);
        chat.VisionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_json_from_track_a_uses_vision_and_returns_stable_result()
    {
        var chat = new FakeChatClient("private malformed response", ValidResponse);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("ACCEPTANCE_LOW_CONFIDENCE");
        result.Failures.Should().ContainSingle().Which.ReasonCode.Should().Be("ACCEPTANCE_LOW_CONFIDENCE");
        result.Failures[0].SafeMessage.Should().NotContain("private malformed response");
    }

    [Fact]
    public async Task Track_a_acceptance_failure_uses_vision_when_enabled()
    {
        var chat = new FakeChatClient(ValidResponse.Replace("INV-1", ""), ValidResponse);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        chat.VisionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_vision_returns_track_a_failure_without_calling_vision()
    {
        var chat = new FakeChatClient(MissingNumberResponse);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText, AllowVisionFallback: false), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.OcrText);
        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_NUMBER_MISSING");
        chat.VisionCalls.Should().Be(0);
    }

    [Fact]
    public async Task Track_b_failure_returns_one_stable_candidate_result()
    {
        var chat = new FakeChatClient(MissingNumberResponse, "private vision response detail");
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(EmbeddedText: LongOcrText), CancellationToken.None);

        result.Route.Should().Be(ExtractionRoute.VisionFallback);
        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("AI_VISION_FAILED");
        result.Failures.Should().ContainSingle().Which.SafeMessage.Should().NotContain("private");
    }

    [Fact]
    public async Task Deterministic_corrections_override_model_fields()
    {
        var chat = new FakeChatClient(ValidResponse);
        var corrections = new InvoiceResponseFields(
            null, null, null, "Deterministic Seller", null, null, null, null, "INV-CORRECTED",
            null, null, null, null, null, null);
        var extractor = NewExtractor(chat);

        var result = await extractor.ExtractAsync(NewRequest(
            EmbeddedText: LongOcrText,
            deterministicCorrections: corrections), CancellationToken.None);

        result.Disposition.Should().Be(AcceptanceDisposition.Accepted);
        result.Document!.Seller.Should().Be("Deterministic Seller");
        result.Document.InvoiceNumber.Should().Be("INV-CORRECTED");
    }

    [Fact]
    public async Task Shared_train_classification_fixtures_match_python_evidence_decisions()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "classification.json");
        using var json = JsonDocument.Parse(File.ReadAllText(fixturePath));

        foreach (var item in json.RootElement.GetProperty("trainClassificationCases").EnumerateArray())
        {
            var caseId = item.GetProperty("caseId").GetString()!;
            var seller = item.GetProperty("seller").GetString()!;
            var expectedType = item.GetProperty("expectedDocumentType").GetString()!;
            var candidate = NewCandidate(caseId) with
            {
                OriginalFileName = item.GetProperty("fileName").GetString()!,
                Metadata = new Dictionary<string, string>
                {
                    ["attachment_name"] = item.GetProperty("attachmentName").GetString()!,
                    ["original_filename"] = item.GetProperty("originalFileName").GetString()!,
                },
            };
            var source = new DocumentSource(
                candidate.DocumentId,
                MimeType: "application/pdf",
                Subject: item.GetProperty("subject").GetString()!);
            var response = JsonSerializer.Serialize(new
            {
                isInvoice = true,
                invoiceDate = "2026-09-24",
                purchaser = "Example Company",
                seller,
                amount = 100m,
                taxAmount = 0m,
                totalAmount = 100m,
                invoiceCode = (string?)null,
                invoiceNumber = "INV-1",
                documentType = "AirTicket",
                category = "Flight",
                confidence = 0.95m,
                flags = Array.Empty<string>(),
                route = new
                {
                    direction = "Unknown",
                    departureDate = "2026-09-24",
                    departureCity = item.GetProperty("departureCity").GetString(),
                    destinationCity = item.GetProperty("destinationCity").GetString(),
                },
                items = Array.Empty<object>(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var request = new FieldExtractionRequest(
                candidate,
                source,
                $"{LongOcrText}\n{item.GetProperty("previewText").GetString()}",
                new InvoiceExtractionRules("Example Company", MinimumOcrTextCharacters: 20),
                AllowVisionFallback: false);
            var result = await NewExtractor(new FakeChatClient(response)).ExtractAsync(request, CancellationToken.None);

            result.Disposition.Should().Be(AcceptanceDisposition.Accepted, caseId);
            result.Document!.DocumentType.ToString().Should().Be(expectedType, caseId);
        }
    }

    [Fact]
    public async Task Authentication_failure_latches_for_following_candidates_in_same_run()
    {
        var chat = new FakeChatClient(ChatFailure: new ChatCompletionException(ChatCompletionErrorCode.Unauthorized, "private auth detail"));
        var gate = new AiAuthenticationFailureGate();
        var extractor = NewExtractor(chat, authGate: gate);

        var first = await extractor.ExtractAsync(NewRequest("candidate-a", LongOcrText), CancellationToken.None);
        var second = await extractor.ExtractAsync(NewRequest("candidate-b", LongOcrText), CancellationToken.None);

        gate.IsBlocked.Should().BeTrue();
        first.ReasonCode.Should().Be("AI_AUTHENTICATION_FAILED");
        second.ReasonCode.Should().Be("AI_AUTHENTICATION_FAILED");
        chat.TextCalls.Should().Be(1);
        chat.VisionCalls.Should().Be(0);
    }

    [Fact]
    public async Task Request_source_identity_mismatch_is_rejected_before_external_work()
    {
        var chat = new FakeChatClient(ValidResponse);
        var extractor = NewExtractor(chat);
        var request = NewRequest(EmbeddedText: LongOcrText) with
        {
            Source = new DocumentSource(DocumentIdentity.Create("another-id")),
        };

        var result = await extractor.ExtractAsync(request, CancellationToken.None);

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("IDENTITY_MISMATCH");
        chat.TextCalls.Should().Be(0);
        chat.VisionCalls.Should().Be(0);
    }

    private static InvoiceFieldExtractor NewExtractor(
        FakeChatClient chat,
        FakeRenderer? renderer = null,
        FakeOcr? ocr = null,
        AiAuthenticationFailureGate? authGate = null) => new(
            chat,
            renderer ?? new FakeRenderer([TestPage]),
            ocr ?? new FakeOcr(LongOcrText),
            new InvoiceNormalizer(),
            new InvoiceAcceptanceService(),
            authGate ?? new AiAuthenticationFailureGate());

    private static FieldExtractionRequest NewRequest(
        string identity = "candidate-1",
        string? EmbeddedText = null,
        bool AllowVisionFallback = true,
        InvoiceResponseFields? deterministicCorrections = null) =>
        new(
            NewCandidate(identity),
            new DocumentSource(DocumentIdentity.Create(identity), MimeType: "application/pdf"),
            EmbeddedText,
            new InvoiceExtractionRules("Example Company", MinimumOcrTextCharacters: 20),
            AllowVisionFallback,
            deterministicCorrections);

    private static DocumentCandidate NewCandidate(string id) => new(
        DocumentIdentity.Create(id), 1, $"corr-{id}", "uid-1", "invoice.pdf",
        "application/pdf", 100, 0, "attachment");

    private const string LongOcrText = "Invoice date 2026-09-24 purchaser Example Company seller Example Seller total 100 invoice INV-1";
    private static readonly RenderedPage TestPage = new(1, 1, 1, "image/png", new byte[] { 1, 2, 3 });

    private const string ValidResponse = """
        {"isInvoice":true,"invoiceDate":"2026-09-24","purchaser":"Example Company","seller":"Example Seller","amount":100,"taxAmount":0,"totalAmount":100,"invoiceCode":null,"invoiceNumber":"INV-1","documentType":"Catering","category":"餐饮","confidence":0.95,"flags":[],"route":null,"items":[]}
        """;

    private const string MissingNumberResponse = """
        {"isInvoice":true,"invoiceDate":"2026-09-24","purchaser":"Example Company","seller":"Example Seller","amount":100,"taxAmount":0,"totalAmount":100,"invoiceCode":null,"invoiceNumber":null,"documentType":"Catering","category":"餐饮","confidence":0.95,"flags":[],"route":null,"items":[]}
        """;

    private sealed class FakeChatClient(params string[] responses) : IChatCompletionService
    {
        private readonly Queue<string> _responses = new(responses);
        public FakeChatClient(ChatCompletionException ChatFailure) : this() => Failure = ChatFailure;
        private FakeChatClient() : this(Array.Empty<string>()) { }
        public Exception? Failure { get; set; }
        public int TextCalls { get; private set; }
        public int VisionCalls { get; private set; }
        public ChatCompletionRequest? LastTextRequest { get; private set; }
        public ChatCompletionRequest? LastVisionRequest { get; private set; }
        public IReadOnlyList<ChatImagePart> LastVisionImages { get; private set; } = Array.Empty<ChatImagePart>();

        public Task<ChatCompletionResult> CompleteTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
        {
            TextCalls++;
            LastTextRequest = request;
            return GetResponse(cancellationToken);
        }

        public Task<ChatCompletionResult> CompleteVisionAsync(ChatCompletionRequest request, IReadOnlyList<ChatImagePart> images, CancellationToken cancellationToken)
        {
            VisionCalls++;
            LastVisionRequest = request;
            LastVisionImages = images;
            return GetResponse(cancellationToken);
        }

        private Task<ChatCompletionResult> GetResponse(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                return Task.FromException<ChatCompletionResult>(Failure);
            }
            return Task.FromResult(new ChatCompletionResult(_responses.Dequeue(), "deepseek-flash", 10, 5));
        }
    }

    private sealed class FakeRenderer(IReadOnlyList<RenderedPage> pages) : IPdfPageRenderer
    {
        public int CallCount { get; private set; }
        public Task<IReadOnlyList<RenderedPage>> RenderAsync(DocumentSource source, PdfRenderOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(pages);
        }
    }

    private sealed class FakeOcr(string text) : IOcrFallback
    {
        public FakeOcr(Exception failure) : this(string.Empty) => Failure = failure;
        public Exception? Failure { get; }
        public int CallCount { get; private set; }
        public Task<OcrFallbackOutcome> RecognizeAsync(DocumentIdentity identity, IReadOnlyList<RenderedPage> pages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (Failure is not null)
            {
                return Task.FromException<OcrFallbackOutcome>(Failure);
            }
            return Task.FromResult(new OcrFallbackOutcome(
                [new OcrLine(text, 0.95m, new BoundingBox(0, 0, 1, 1), 1)], 0.95m));
        }
    }
}
