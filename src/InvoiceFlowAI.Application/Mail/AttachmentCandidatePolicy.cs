namespace InvoiceFlowAI.Application.Mail;

public sealed class AttachmentCandidatePolicy : IAttachmentCandidatePolicy
{
    private const long FiveMiB = 5 * 1024 * 1024;
    private const long TenKiB = 10 * 1024;
    private const long FiftyKiB = 50 * 1024;
    private const long FourKiB = 4 * 1024;
    private const int TinyDimension = 2;
    private const int SmallDimension = 32;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];
    private static readonly string[] DecorativeNameKeywords =
    [
        "logo",
        "banner",
        "icon",
        "footer",
        "header",
        "signature",
        "avatar",
        "wechat",
        "weixin",
        "qrcode_logo",
        "thumb",
        "sprite"
    ];

    public AttachmentCandidateDecision Classify(AttachmentCandidateInput input)
    {
        var strongSignals = new List<string>();
        var weakSignals = new List<string>();
        string? extremeSignal = null;
        var image = IsImage(input);

        if (IsInlineImage(input))
        {
            AddSignal(strongSignals, "inline_image");
        }

        if (image && input.ContentLength < FiftyKiB)
        {
            AddSignal(weakSignals, "small_image_under_50kb");
        }

        if (image && input.ContentLength < TenKiB)
        {
            AddSignal(strongSignals, "tiny_image_under_10kb");
        }

        if (HasDecorativeName(input.FileName))
        {
            AddSignal(strongSignals, "decorative_filename");
        }

        if (image && input.ImageInfo is { Width: <= TinyDimension, Height: <= TinyDimension } && input.ContentLength <= FourKiB)
        {
            extremeSignal = "tracking_pixel";
        }
        else if (image && input.ImageInfo is { Width: <= SmallDimension, Height: <= SmallDimension })
        {
            AddSignal(strongSignals, "tiny_image_dimensions");
        }

        if (extremeSignal is not null || strongSignals.Distinct(StringComparer.Ordinal).Count() >= 2)
        {
            var reasonCode = extremeSignal is not null ? "A_EXTREME_NEGATIVE_SIGNAL" : "A_TWO_STRONG_NEGATIVE_SIGNALS";
            return BuildDecision("A", "drop", reasonCode, strongSignals, weakSignals, extremeSignal, input.ImageInfo);
        }

        if (input.ContentLength > FiveMiB)
        {
            AddSignal(weakSignals, "attachment_over_5mb");
            return BuildDecision("B", "retain_only", "B_ATTACHMENT_OVER_5MB_RETAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
        }

        if (input.SourceKind.Equals("zip_container_failed", StringComparison.OrdinalIgnoreCase))
        {
            AddSignal(weakSignals, "zip_unpack_failed");
            return BuildDecision("B", "retain_only", "B_ZIP_UNPACK_FAILED_RETAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
        }

        if (input.SourceKind.Equals("zip_container_filtered", StringComparison.OrdinalIgnoreCase))
        {
            AddSignal(weakSignals, "zip_members_filtered");
            return BuildDecision("B", "retain_only", "B_ZIP_FILTERED_TO_OUTER_CONTAINER", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
        }

        if (image && input.EmailTier == 4)
        {
            if (input.HasQrCode is false)
            {
                AddSignal(weakSignals, "missing_qr");
                return BuildDecision("B", "retain_only", "B_TIER4_IMAGE_NO_QR_RETAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
            }

            if (input.HasQrCode is null)
            {
                AddSignal(weakSignals, "qr_detection_unavailable");
                return BuildDecision("B", "retain_only", "B_TIER4_IMAGE_QR_UNKNOWN_RETAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
            }
        }

        if (image && weakSignals.Count > 0)
        {
            return BuildDecision("B", "retain_only", "B_LOW_CONFIDENCE_IMAGE_RETAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
        }

        return BuildDecision("B", "main_chain", "B_ATTACHMENT_MAIN_CHAIN", strongSignals, weakSignals, extremeSignal, input.ImageInfo);
    }

    private static AttachmentCandidateDecision BuildDecision(
        string bucket,
        string action,
        string reasonCode,
        List<string> strongSignals,
        List<string> weakSignals,
        string? extremeSignal,
        AttachmentImageInfo? imageInfo)
    {
        return new AttachmentCandidateDecision(
            Bucket: bucket,
            Action: action,
            ReasonCode: reasonCode,
            StrongNegativeSignals: strongSignals.Distinct(StringComparer.Ordinal).OrderBy(signal => signal, StringComparer.Ordinal).ToArray(),
            WeakNegativeSignals: weakSignals.Distinct(StringComparer.Ordinal).OrderBy(signal => signal, StringComparer.Ordinal).ToArray(),
            ExtremeNegativeSignal: extremeSignal,
            ImageInfo: imageInfo);
    }

    private static void AddSignal(List<string> signals, string signal)
    {
        if (!signals.Contains(signal, StringComparer.Ordinal))
        {
            signals.Add(signal);
        }
    }

    private static bool IsImage(AttachmentCandidateInput input)
    {
        var fileName = input.FileName.Trim();
        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex < 0)
        {
            return false;
        }

        var extension = fileName[dotIndex..];
        foreach (var imageExtension in ImageExtensions)
        {
            if (extension.Equals(imageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInlineImage(AttachmentCandidateInput input)
        => input.ContentDisposition.Contains("inline", StringComparison.OrdinalIgnoreCase)
           && input.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool HasDecorativeName(string fileName)
    {
        foreach (var keyword in DecorativeNameKeywords)
        {
            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}