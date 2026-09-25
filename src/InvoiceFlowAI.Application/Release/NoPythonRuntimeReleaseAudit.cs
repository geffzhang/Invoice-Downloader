namespace InvoiceFlowAI.Application.Release;

public sealed record NoPythonRuntimeAuditIssue(string RelativePath, string Code);

public sealed record NoPythonRuntimeAuditReport(IReadOnlyList<NoPythonRuntimeAuditIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;
}

public static class NoPythonRuntimeReleaseAudit
{
    private static readonly string[] RequiredExecutables =
    [
        "InvoiceFlowAI.exe",
        "InvoiceFlowAI.UrlRecovery.Worker.exe",
    ];

    public static NoPythonRuntimeAuditReport Verify(string publishRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishRoot);
        var root = Path.GetFullPath(publishRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Publish root does not exist.");
        }

        var issues = new List<NoPythonRuntimeAuditIssue>();
        foreach (var executable in RequiredExecutables)
        {
            if (!File.Exists(Path.Combine(root, executable)))
            {
                issues.Add(new NoPythonRuntimeAuditIssue(executable, "RequiredDotnetExecutableMissing"));
            }
        }

        foreach (var path in EnumerateEntries(root))
        {
            var relativePath = Path.GetRelativePath(root, path);
            if (IsPythonRuntimeArtifact(path, relativePath))
            {
                issues.Add(new NoPythonRuntimeAuditIssue(relativePath, "PythonRuntimeArtifact"));
            }
        }

        return new NoPythonRuntimeAuditReport(issues);
    }

    private static IEnumerable<string> EnumerateEntries(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                yield return entry;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool IsPythonRuntimeArtifact(string path, string relativePath)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pyo", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pyd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".whl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (name.Equals(".venv", StringComparison.OrdinalIgnoreCase)
                || name.Equals("venv", StringComparison.OrdinalIgnoreCase)
                || name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
                || name.Equals("pyinstaller", StringComparison.OrdinalIgnoreCase)
                || name.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("libpython", StringComparison.OrdinalIgnoreCase)
                && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("python3", StringComparison.OrdinalIgnoreCase)
                && (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            || name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                && extension.Equals("._pth", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("python.runtime", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("pythonnet", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("clr_loader", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pyinstaller", StringComparison.OrdinalIgnoreCase);
    }
}
