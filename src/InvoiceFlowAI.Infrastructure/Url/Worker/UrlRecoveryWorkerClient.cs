using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public sealed class UrlRecoveryWorkerClient : IUrlRecoveryClient
{
    private readonly IUrlRecoveryWorkerProcessRunner _processRunner;
    private readonly UrlRecoveryWorkerManifestStore _manifestStore;
    private readonly string _jobRoot;
    private readonly TimeSpan _timeout;
    private readonly Func<string, bool> _cleanupJobDirectory;

    public UrlRecoveryWorkerClient(
        IUrlRecoveryWorkerProcessRunner processRunner,
        UrlRecoveryWorkerManifestStore manifestStore,
        string? jobRoot = null,
        TimeSpan? timeout = null)
        : this(processRunner, manifestStore, jobRoot, timeout, TryDeleteJobDirectory)
    {
    }

    internal UrlRecoveryWorkerClient(
        IUrlRecoveryWorkerProcessRunner processRunner,
        UrlRecoveryWorkerManifestStore manifestStore,
        string? jobRoot,
        TimeSpan? timeout,
        Func<string, bool> cleanupJobDirectory)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _cleanupJobDirectory = cleanupJobDirectory ?? throw new ArgumentNullException(nameof(cleanupJobDirectory));
        _jobRoot = Path.GetFullPath(jobRoot ?? Path.Combine(Path.GetTempPath(), "InvoiceFlowAI", "url-recovery"));
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        var candidate = new MailboxUrlCandidate("single-url", "INBOX", "single-url", "single-url", sourceUrl,
            string.Empty, string.Empty, new Dictionary<string, string>(), 0);
        return RecoverAsync(new UrlCandidateGroup(string.Empty, [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("single-url-recovery")),
            cancellationToken);
    }

    public Task<UrlRecoveryResult> RecoverAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return RecoverAsync(new UrlCandidateGroup(candidate.ProviderFamily, [candidate], candidate.ExpectedFields,
            candidate.ExpectedFieldEvidence, DocumentIdentity.Create(string.IsNullOrWhiteSpace(candidate.ProviderGroupId)
                ? "single-url-recovery"
                : candidate.ProviderGroupId)), cancellationToken);
    }

    public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        cancellationToken.ThrowIfCancellationRequested();
        var jobDirectory = Path.Combine(_jobRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        try
        {
            var requestPath = Path.Combine(jobDirectory, "request.json");
            var resultPath = Path.Combine(jobDirectory, "result.json");
            var request = new UrlRecoveryWorkerRequest(
                UrlRecoveryWorkerManifestStore.CurrentSchemaVersion,
                group,
                jobDirectory,
                UrlRecoveryWorkerManifestStore.MaxResponseBytes,
                UrlRecoveryWorkerManifestStore.MaxArtifacts,
                resultPath);
            await _manifestStore.WriteRequestAsync(requestPath, request, cancellationToken).ConfigureAwait(false);

            int exitCode;
            try
            {
                exitCode = await _processRunner.RunAsync(requestPath, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (UrlRecoveryWorkerTimeoutException)
            {
                throw new UrlRecoveryException("URL_RECOVERY_WORKER_TIMEOUT", "Invoice link recovery exceeded its time limit.", true, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new UrlRecoveryException("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false);
            }

            if (exitCode != 0)
            {
                throw new UrlRecoveryException("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false);
            }

            UrlRecoveryWorkerResponse response;
            try
            {
                response = await _manifestStore.ReadResponseAsync(resultPath, cancellationToken).ConfigureAwait(false);
            }
            catch (UrlRecoveryWorkerProtocolException)
            {
                throw OutputInvalid();
            }
            if (response.FailureReasonCode is { } failureCode)
            {
                throw new UrlRecoveryException(failureCode, "Invoice link could not be recovered.", false, false);
            }

            var artifacts = new List<CapturedUrlArtifact>(response.Artifacts.Count);
            long totalBytes = 0;
            foreach (var manifest in response.Artifacts)
            {
                if (manifest.SourceUrlOrdinal >= group.Candidates.Count
                    || checked(totalBytes + manifest.ByteLength) > UrlRecoveryWorkerManifestStore.MaxResponseBytes)
                {
                    throw OutputInvalid();
                }
                totalBytes += manifest.ByteLength;
                byte[] content;
                try
                {
                    var path = ResolveArtifactPath(jobDirectory, manifest.RelativePath);
                    RejectReparsePoints(jobDirectory, path);
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length != manifest.ByteLength || info.Length > UrlRecoveryWorkerManifestStore.MaxResponseBytes)
                    {
                        throw OutputInvalid();
                    }
                    content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (UrlRecoveryException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    throw OutputInvalid();
                }
                var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(manifest.Sha256))
                    || !HasValidSignature(manifest.Kind, content))
                {
                    throw OutputInvalid();
                }
                artifacts.Add(new CapturedUrlArtifact(manifest.Kind, manifest.ContentType, content,
                    manifest.SourceUrlOrdinal, digest, manifest.SanitizedResolvedOrigin, manifest.InvoiceFields,
                    manifest.ExpectedMatch, manifest.MatchReasonCode));
            }

            return new UrlRecoveryResult(artifacts, response.SelectedArtifactIndex);
        }
        catch (UrlRecoveryException)
        {
            throw;
        }
        catch (UrlRecoveryWorkerProtocolException)
        {
            throw OutputInvalid();
        }
        finally
        {
            if (!_cleanupJobDirectory(jobDirectory))
            {
                throw new UrlRecoveryException("URL_RECOVERY_WORKER_CLEANUP_FAILED", "Invoice recovery temporary data could not be removed.", false, false);
            }
        }
    }

    private static string ResolveArtifactPath(string jobDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal)) throw OutputInvalid();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)) throw OutputInvalid();
        return path;
    }

    private static void RejectReparsePoints(string jobDirectory, string artifactPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(jobDirectory), artifactPath);
        var current = Path.GetFullPath(jobDirectory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw OutputInvalid();
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

    private static UrlRecoveryException OutputInvalid()
        => new("URL_RECOVERY_WORKER_OUTPUT_INVALID", "Invoice recovery worker returned invalid output.", false, false);

    private static bool TryDeleteJobDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) DeleteDirectoryWithoutFollowingReparsePoints(path);
            return !Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, recursive: false);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }
}