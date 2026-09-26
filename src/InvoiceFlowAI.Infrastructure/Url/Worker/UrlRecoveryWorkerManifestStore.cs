using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public sealed class UrlRecoveryWorkerManifestStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxCandidates = 128;
    public const int MaxArtifacts = 32;
    public const int MaxResponseBytes = 50 * 1024 * 1024;
    private const long MaxTotalArtifactBytes = 100L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
    };

    public async Task WriteRequestAsync(string path, UrlRecoveryWorkerRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await WriteAtomicAsync(path, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UrlRecoveryWorkerRequest> ReadRequestAsync(string path, CancellationToken cancellationToken)
    {
        var request = await ReadAsync<UrlRecoveryWorkerRequest>(path, cancellationToken).ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public async Task WriteResponseAsync(string path, UrlRecoveryWorkerResponse response, CancellationToken cancellationToken)
    {
        ValidateResponse(response);
        await WriteAtomicAsync(path, response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UrlRecoveryWorkerResponse> ReadResponseAsync(string path, CancellationToken cancellationToken)
    {
        var response = await ReadAsync<UrlRecoveryWorkerResponse>(path, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response);
        return response;
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new UrlRecoveryWorkerProtocolException();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new UrlRecoveryWorkerProtocolException();
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaxManifestBytes) throw new UrlRecoveryWorkerProtocolException();
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is <= 0 or > MaxManifestBytes) throw new UrlRecoveryWorkerProtocolException();
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return value ?? throw new UrlRecoveryWorkerProtocolException();
        }
        catch (UrlRecoveryWorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new UrlRecoveryWorkerProtocolException();
        }
    }

    private static void ValidateRequest(UrlRecoveryWorkerRequest? request)
    {
        if (request is null || request.SchemaVersion != CurrentSchemaVersion || request.Group is null
            || request.Group.Candidates is null || request.Group.Candidates.Count is < 1 or > MaxCandidates
            || request.MaxResponseBytes is < 1 or > MaxResponseBytes
            || request.MaxArtifacts is < 1 or > MaxArtifacts
            || !Path.IsPathFullyQualified(request.JobDirectory)
            || !Path.IsPathFullyQualified(request.ResultManifestPath)
            || !IsDescendant(request.JobDirectory, request.ResultManifestPath)
            || request.Group.Candidates.Any(static candidate => candidate is null || candidate.SourceUrl is null || !candidate.SourceUrl.IsAbsoluteUri))
        {
            throw new UrlRecoveryWorkerProtocolException();
        }
    }

    private static void ValidateResponse(UrlRecoveryWorkerResponse? response)
    {
        if (response is null || response.SchemaVersion != CurrentSchemaVersion || response.Artifacts is null
            || response.Artifacts.Count > MaxArtifacts)
        {
            throw new UrlRecoveryWorkerProtocolException();
        }

        if (response.FailureReasonCode is not null)
        {
            if (!Regex.IsMatch(response.FailureReasonCode, "^[A-Z0-9_]{1,80}$", RegexOptions.CultureInvariant)
                || response.Artifacts.Count != 0 || response.SelectedArtifactIndex is not null)
            {
                throw new UrlRecoveryWorkerProtocolException();
            }
            return;
        }

        if (response.Artifacts.Count == 0 ? response.SelectedArtifactIndex is not null
            : response.SelectedArtifactIndex is null || response.SelectedArtifactIndex < 0 || response.SelectedArtifactIndex >= response.Artifacts.Count)
        {
            throw new UrlRecoveryWorkerProtocolException();
        }

        long totalBytes = 0;
        foreach (var artifact in response.Artifacts)
        {
            if (artifact is null || !IsSafeRelativePath(artifact.RelativePath)
                || !Enum.IsDefined(artifact.Kind)
                || string.IsNullOrWhiteSpace(artifact.ContentType) || artifact.ContentType.Length > 128
                || artifact.ByteLength is < 1 or > MaxResponseBytes
                || artifact.SourceUrlOrdinal < 0
                || string.IsNullOrWhiteSpace(artifact.Sha256)
                || !Regex.IsMatch(artifact.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || artifact.MatchReasonCode is null || artifact.MatchReasonCode.Length > 80
                || artifact.InvoiceFields is null || artifact.InvoiceFields.Count > 64)
            {
                throw new UrlRecoveryWorkerProtocolException();
            }
            if (artifact.SanitizedResolvedOrigin is { } origin
                && (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin)
                    || parsedOrigin.UserInfo.Length > 0 || parsedOrigin.Query.Length > 0 || parsedOrigin.Fragment.Length > 0
                    || parsedOrigin.AbsolutePath != "/"))
            {
                throw new UrlRecoveryWorkerProtocolException();
            }
            totalBytes = checked(totalBytes + artifact.ByteLength);
            if (totalBytes > MaxTotalArtifactBytes) throw new UrlRecoveryWorkerProtocolException();
        }
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal)) return false;
        var segments = path.Replace('\\', '/').Split('/');
        return segments.All(static segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsDescendant(string parentPath, string childPath)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var child = Path.GetFullPath(childPath);
        var relative = Path.GetRelativePath(parent, child);
        return !Path.IsPathRooted(relative)
            && relative != "."
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}