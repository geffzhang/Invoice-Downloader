using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Mail;

public sealed class AttachmentCandidatePolicyTests
{
    [Fact]
    public void Tracking_pixel_retains_calculated_signals_and_preempts_size_check()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "logo.png",
            ContentLength: 4 * 1024,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "inline",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(2, 2)));

        decision.Bucket.Should().Be("A");
        decision.Action.Should().Be("drop");
        decision.ReasonCode.Should().Be("A_EXTREME_NEGATIVE_SIGNAL");
        decision.StrongNegativeSignals.Should().Equal("decorative_filename", "inline_image", "tiny_image_under_10kb");
        decision.WeakNegativeSignals.Should().Equal("small_image_under_50kb");
        decision.ExtremeNegativeSignal.Should().Be("tracking_pixel");
    }

    [Fact]
    public void Large_inline_logo_is_dropped_before_size_check_and_signals_are_sorted()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "logo.png",
            ContentLength: 5 * 1024 * 1024 + 1,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "inline",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(160, 48)));

        decision.Bucket.Should().Be("A");
        decision.Action.Should().Be("drop");
        decision.ReasonCode.Should().Be("A_TWO_STRONG_NEGATIVE_SIGNALS");
        decision.StrongNegativeSignals.Should().Equal("decorative_filename", "inline_image");
        decision.WeakNegativeSignals.Should().BeEmpty();
    }

    [Fact]
    public void Tiny_image_uses_python_signal_names_and_sorting()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "photo.png",
            ContentLength: 9 * 1024,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "attachment",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(16, 16)));

        decision.Bucket.Should().Be("A");
        decision.Action.Should().Be("drop");
        decision.ReasonCode.Should().Be("A_TWO_STRONG_NEGATIVE_SIGNALS");
        decision.StrongNegativeSignals.Should().Equal("tiny_image_dimensions", "tiny_image_under_10kb");
        decision.WeakNegativeSignals.Should().Equal("small_image_under_50kb");
    }

    [Fact]
    public void Image_with_weak_signal_is_low_confidence_retain()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "photo.png",
            ContentLength: 20 * 1024,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "attachment",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(160, 160)));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_LOW_CONFIDENCE_IMAGE_RETAIN");
        decision.StrongNegativeSignals.Should().BeEmpty();
        decision.WeakNegativeSignals.Should().Equal("small_image_under_50kb");
    }

    [Fact]
    public void Inline_image_decision_uses_content_disposition_and_content_type_not_extension()
    {
        var policy = new AttachmentCandidatePolicy();

        var photoJpg = policy.Classify(new AttachmentCandidateInput(
            FileName: "photo.jpg",
            ContentLength: 20 * 1024,
            EmailTier: 2,
            ContentType: "application/octet-stream",
            ContentDisposition: "inline",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(160, 160)));

        photoJpg.Bucket.Should().Be("B");
        photoJpg.Action.Should().Be("retain_only");
        photoJpg.ReasonCode.Should().Be("B_LOW_CONFIDENCE_IMAGE_RETAIN");
        photoJpg.StrongNegativeSignals.Should().BeEmpty();
        photoJpg.WeakNegativeSignals.Should().Equal("small_image_under_50kb");

        var invoiceBin = policy.Classify(new AttachmentCandidateInput(
            FileName: "invoice.bin",
            ContentLength: 20 * 1024,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "inline",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(160, 160)));

        invoiceBin.Bucket.Should().Be("B");
        invoiceBin.Action.Should().Be("main_chain");
        invoiceBin.ReasonCode.Should().Be("B_ATTACHMENT_MAIN_CHAIN");
        invoiceBin.StrongNegativeSignals.Should().Equal("inline_image");
        invoiceBin.WeakNegativeSignals.Should().BeEmpty();
    }

    [Fact]
    public void Tiny_image_dimensions_are_preserved_as_a_strong_signal()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "photo.png",
            ContentLength: 24 * 1024,
            EmailTier: 2,
            ContentType: "image/png",
            ContentDisposition: "attachment",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(16, 16)));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_LOW_CONFIDENCE_IMAGE_RETAIN");
        decision.StrongNegativeSignals.Should().Equal("tiny_image_dimensions");
        decision.WeakNegativeSignals.Should().Equal("small_image_under_50kb");
    }

    [Fact]
    public void Payload_over_five_megabytes_adds_attachment_over_5mb_signal()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "invoice.pdf",
            ContentLength: 5 * 1024 * 1024 + 1,
            EmailTier: 2,
            ContentType: "application/pdf",
            ContentDisposition: "attachment",
            SourceKind: "attachment"));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_ATTACHMENT_OVER_5MB_RETAIN");
        decision.WeakNegativeSignals.Should().Equal("attachment_over_5mb");
    }

    [Fact]
    public void Zip_container_failure_uses_zip_unpack_failed_reason()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "archive.pdf",
            ContentLength: 180 * 1024,
            EmailTier: 2,
            ContentType: "application/pdf",
            ContentDisposition: "attachment",
            SourceKind: "zip_container_failed"));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_ZIP_UNPACK_FAILED_RETAIN");
        decision.WeakNegativeSignals.Should().Equal("zip_unpack_failed");
    }

    [Fact]
    public void Zip_container_filtered_uses_zip_members_filtered_reason()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "archive.pdf",
            ContentLength: 180 * 1024,
            EmailTier: 2,
            ContentType: "application/pdf",
            ContentDisposition: "attachment",
            SourceKind: "zip_container_filtered"));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_ZIP_FILTERED_TO_OUTER_CONTAINER");
        decision.WeakNegativeSignals.Should().Equal("zip_members_filtered");
    }

    [Fact]
    public void Tier_four_image_with_missing_qr_is_retain_only()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "scan.png",
            ContentLength: 64 * 1024,
            EmailTier: 4,
            ContentType: "image/png",
            ContentDisposition: "attachment",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(1200, 1200),
            HasQrCode: false));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_TIER4_IMAGE_NO_QR_RETAIN");
        decision.WeakNegativeSignals.Should().Equal("missing_qr");
    }

    [Fact]
    public void Tier_four_image_with_unavailable_qr_detection_is_retain_only()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "scan.png",
            ContentLength: 64 * 1024,
            EmailTier: 4,
            ContentType: "image/png",
            ContentDisposition: "attachment",
            SourceKind: "attachment",
            ImageInfo: new AttachmentImageInfo(1200, 1200),
            HasQrCode: null));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("retain_only");
        decision.ReasonCode.Should().Be("B_TIER4_IMAGE_QR_UNKNOWN_RETAIN");
        decision.WeakNegativeSignals.Should().Equal("qr_detection_unavailable");
    }

    [Fact]
    public void Ordinary_pdf_uses_main_chain()
    {
        var policy = new AttachmentCandidatePolicy();

        var decision = policy.Classify(new AttachmentCandidateInput(
            FileName: "invoice.pdf",
            ContentLength: 180 * 1024,
            EmailTier: 2,
            ContentType: "application/pdf",
            ContentDisposition: "attachment",
            SourceKind: "attachment"));

        decision.Bucket.Should().Be("B");
        decision.Action.Should().Be("main_chain");
        decision.ReasonCode.Should().Be("B_ATTACHMENT_MAIN_CHAIN");
    }
}