using System.Security.Cryptography;
using System.Text;

namespace InvoiceFlowAI.Application.Archive;

public static class ArchiveInventoryPath
{
    public static string CreateRootKey(string fullRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullRoot));
        if (OperatingSystem.IsWindows()) normalizedRoot = normalizedRoot.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot))).ToLowerInvariant();
    }

    public static bool TryResolveUnderRoot(string root, string persistedPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(persistedPath)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.IsPathRooted(persistedPath)
                ? Path.GetFullPath(persistedPath)
                : Path.GetFullPath(Path.Combine(fullRoot, persistedPath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(fullRoot, candidate);
            if (relative == "." || Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}