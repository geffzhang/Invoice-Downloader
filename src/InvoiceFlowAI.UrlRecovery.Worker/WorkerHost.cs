using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;

namespace InvoiceFlowAI.UrlRecovery.Worker;

public sealed class WorkerHost
{
    private readonly IUrlRecoveryClient _recoveryClient;
    private readonly UrlRecoveryWorkerManifestStore _manifestStore;

    public WorkerHost(IUrlRecoveryClient recoveryClient, UrlRecoveryWorkerManifestStore manifestStore)
    {
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
    }

    public async Task<int> RunAsync(string requestPath, CancellationToken cancellationToken)
    {
        UrlRecoveryWorkerRequest request;
        try
        {
            request = await _manifestStore.ReadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (UrlRecoveryWorkerProtocolException)
        {
            return 65;
        }

        try
        {
            Directory.CreateDirectory(request.JobDirectory);
            var result = await _recoveryClient.RecoverAsync(request.Group, cancellationToken).ConfigureAwait(false);
            if (result.Artifacts.Count > request.MaxArtifacts)
            {
                await WriteFailureAsync(request, "URL_RECOVERY_WORKER_OUTPUT_INVALID", cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var manifests = new List<UrlRecoveryWorkerArtifactManifest>(result.Artifacts.Count);
            long totalBytes = 0;
            for (var index = 0; index < result.Artifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = result.Artifacts[index];
                if (artifact.Content.Length > request.MaxResponseBytes
                    || checked(totalBytes + artifact.Content.Length) > request.MaxResponseBytes)
                {
                    await WriteFailureAsync(request, "URL_RECOVERY_WORKER_OUTPUT_INVALID", cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                totalBytes += artifact.Content.Length;
                if (!HasValidSignature(artifact.Kind, artifact.Content.Span))
                {
                    await WriteFailureAsync(request, "URL_RECOVERY_WORKER_OUTPUT_INVALID", cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                var extension = artifact.Kind switch
                {
                    RecoveredArtifactKind.Pdf => "pdf",
                    RecoveredArtifactKind.Xml => "xml",
                    RecoveredArtifactKind.Ofd => "ofd",
                    _ => throw new InvalidOperationException(),
                };
                var relativePath = Path.Combine("artifacts", $"artifact-{index:D3}.{extension}");
                var fullPath = Path.GetFullPath(Path.Combine(request.JobDirectory, relativePath));
                if (!IsDescendant(request.JobDirectory, fullPath))
                {
                    await WriteFailureAsync(request, "URL_RECOVERY_WORKER_OUTPUT_INVALID", cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                await WriteArtifactAtomicAsync(fullPath, artifact.Content, cancellationToken).ConfigureAwait(false);
                manifests.Add(new UrlRecoveryWorkerArtifactManifest(
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    artifact.Kind,
                    artifact.ContentType,
                    artifact.Content.Length,
                    Convert.ToHexString(SHA256.HashData(artifact.Content.Span)).ToLowerInvariant(),
                    artifact.SourceUrlOrdinal,
                    artifact.SanitizedResolvedOrigin,
                    artifact.InvoiceFields,
                    artifact.ExpectedMatch,
                    artifact.MatchReasonCode));
            }

            var response = new UrlRecoveryWorkerResponse(
                UrlRecoveryWorkerManifestStore.CurrentSchemaVersion,
                null,
                result.SelectedArtifactIndex,
                manifests);
            await _manifestStore.WriteResponseAsync(request.ResultManifestPath, response, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (UrlRecoveryException exception)
        {
            var safeCode = IsSafeReasonCode(exception.ReasonCode) ? exception.ReasonCode : "URL_RECOVERY_FAILED";
            await WriteFailureAsync(request, safeCode, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await WriteFailureAsync(request, "URL_RECOVERY_WORKER_FAILED", cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private Task WriteFailureAsync(UrlRecoveryWorkerRequest request, string reasonCode, CancellationToken cancellationToken)
        => _manifestStore.WriteResponseAsync(request.ResultManifestPath,
            new UrlRecoveryWorkerResponse(UrlRecoveryWorkerManifestStore.CurrentSchemaVersion, reasonCode, null, []),
            cancellationToken);

    private static async Task WriteArtifactAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directory);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool HasValidSignature(RecoveredArtifactKind kind, ReadOnlySpan<byte> content)
        => kind switch
        {
            RecoveredArtifactKind.Pdf => content.StartsWith("%PDF-"u8),
            RecoveredArtifactKind.Xml => LooksLikeXml(content),
            RecoveredArtifactKind.Ofd => content.StartsWith("PK\u0003\u0004"u8),
            _ => false,
        };

    private static bool LooksLikeXml(ReadOnlySpan<byte> content)
    {
        var prefix = Encoding.UTF8.GetString(content[..Math.Min(content.Length, 256)]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return prefix.StartsWith('<');
    }

    private static bool IsSafeReasonCode(string value)
        => value.Length is > 0 and <= 80 && value.All(static character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_');

    private static bool IsDescendant(string parentPath, string childPath)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var relative = Path.GetRelativePath(parent, Path.GetFullPath(childPath));
        return !Path.IsPathRooted(relative) && relative != "." && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}