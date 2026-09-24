using FluentAssertions;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryWorkerManifestStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invoiceflow-worker-protocol-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Round_trips_response_and_atomically_replaces_existing_manifest()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "result.json");
        var store = new UrlRecoveryWorkerManifestStore();
        await store.WriteResponseAsync(path, new UrlRecoveryWorkerResponse(1, null, 0,
        [
            new UrlRecoveryWorkerArtifactManifest("artifacts/invoice.pdf", RecoveredArtifactKind.Pdf,
                "application/pdf", 8, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 0,
                "https://files.example", new Dictionary<string, string>(), true, "matched_pdf"),
        ]), CancellationToken.None);
        await store.WriteResponseAsync(path, new UrlRecoveryWorkerResponse(1, "SAFE_FAILURE", null, []), CancellationToken.None);

        var result = await store.ReadResponseAsync(path, CancellationToken.None);

        result.FailureReasonCode.Should().Be("SAFE_FAILURE");
        result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_unsupported_response_schema_version()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "result.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99,\"artifacts\":[]}");
        var store = new UrlRecoveryWorkerManifestStore();

        var act = () => store.ReadResponseAsync(path, CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryWorkerProtocolException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_PROTOCOL_INVALID");
    }

    [Fact]
    public async Task Rejects_rooted_artifact_paths_in_worker_response()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "result.json");
        var rooted = OperatingSystem.IsWindows() ? "C:\\outside.pdf" : "/outside.pdf";
        await File.WriteAllTextAsync(path,
            $$"""{"schemaVersion":1,"failureReasonCode":null,"selectedArtifactIndex":0,"artifacts":[{"relativePath":"{{rooted}}","kind":0,"contentType":"application/pdf","byteLength":8,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","sourceUrlOrdinal":0,"sanitizedResolvedOrigin":"https://files.example","invoiceFields":{},"expectedMatch":true,"matchReasonCode":"matched_pdf"}]}""");
        var store = new UrlRecoveryWorkerManifestStore();

        var act = () => store.ReadResponseAsync(path, CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryWorkerProtocolException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_PROTOCOL_INVALID");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}