using System.Net;

namespace InvoiceFlowAI.Infrastructure.Url;

public interface IUrlRecoveryTransport
{
    Task<UrlTransportResponse> SendAsync(
        UrlTransportRequest request,
        int maxResponseBytes,
        CancellationToken cancellationToken);
}

public sealed record UrlTransportRequest(
    ValidatedPublicUrl Url,
    HttpMethod Method,
    IReadOnlyDictionary<string, string>? FormFields = null,
    ReadOnlyMemory<byte>? Body = null,
    string? ContentType = null);

public sealed record UrlTransportResponse(
    HttpStatusCode StatusCode,
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string? RedirectLocation);