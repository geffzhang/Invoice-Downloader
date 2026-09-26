using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed record OfdXmlEntry(string Path, ReadOnlyMemory<byte> Content);

public sealed class OfdPackageReader
{
    public const int MaximumEntryCount = 256;
    public const int MaximumPageCount = 256;
    public const long MaximumEntryBytes = 10 * 1024 * 1024;
    public const long MaximumExpandedBytes = 25 * 1024 * 1024;

    public IReadOnlyList<OfdXmlEntry> ReadXmlEntries(ReadOnlyMemory<byte> packageBytes)
    {
        if (packageBytes.IsEmpty || packageBytes.Length > MaximumExpandedBytes)
        {
            throw new InvalidDataException("OFD package exceeds the allowed input size.");
        }

        using var input = new MemoryStream(packageBytes.ToArray(), writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("OFD package contains too many entries.");
        }

        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ZipArchiveEntry>();
        long declaredExpandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryPath(entry.FullName);
            if (!normalizedNames.Add(normalized))
            {
                throw new InvalidDataException("OFD package contains duplicate entry paths.");
            }
            if (entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException("OFD package entry exceeds the allowed size.");
            }
            declaredExpandedBytes = checked(declaredExpandedBytes + entry.Length);
            if (declaredExpandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("OFD package exceeds the expanded size limit.");
            }

            if (normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(entry);
            }
        }

        return candidates
            .OrderBy(entry => GetPriority(NormalizeEntryPath(entry.FullName)))
            .ThenBy(entry => NormalizeEntryPath(entry.FullName), StringComparer.OrdinalIgnoreCase)
            .Select(entry => new OfdXmlEntry(
                NormalizeEntryPath(entry.FullName),
                ReadBounded(entry)))
            .ToArray();
    }

    public IReadOnlyList<OfdXmlEntry> ReadInvoiceXmlEntries(ReadOnlyMemory<byte> packageBytes)
    {
        var entries = ReadXmlEntries(packageBytes);
        var byPath = entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documentQueue = new Queue<string>();
        var parsedDocuments = new Dictionary<string, XDocument?>(StringComparer.OrdinalIgnoreCase);
        var idOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var pageCount = 0;

        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry.Path).Equals("OFD.xml", StringComparison.OrdinalIgnoreCase))
            {
                documentQueue.Enqueue(entry.Path);
            }
            if (IsAlwaysCandidate(entry.Path))
            {
                selectedPaths.Add(entry.Path);
                if (IsTagEntry(entry.Path))
                {
                    documentQueue.Enqueue(entry.Path);
                }
            }
        }

        foreach (var entry in entries)
        {
            var document = LoadXml(entry.Content);
            parsedDocuments[entry.Path] = document;
            if (document is null) continue;
            foreach (var element in document.Root?.DescendantsAndSelf() ?? [])
            {
                if (element.Name.LocalName.Equals("Page", StringComparison.OrdinalIgnoreCase)
                    && ++pageCount > MaximumPageCount)
                {
                    throw new InvalidDataException("OFD package contains too many pages.");
                }
                var id = GetAttribute(element, "ID");
                if (!string.IsNullOrWhiteSpace(id) && !idOwners.TryAdd(id, entry.Path))
                {
                    throw new InvalidDataException("OFD package contains duplicate object IDs.");
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (documentQueue.TryDequeue(out var currentPath))
        {
            if (!visited.Add(currentPath) || !byPath.TryGetValue(currentPath, out var currentEntry)) continue;
            var document = parsedDocuments[currentPath];
            if (document is null) continue;

            foreach (var element in document.Root?.DescendantsAndSelf() ?? [])
            {
                var elementName = element.Name.LocalName;
                if (elementName.Equals("DocRoot", StringComparison.OrdinalIgnoreCase))
                {
                    EnqueuePath(currentPath, element.Value, referencedPaths, documentQueue);
                }

                if (elementName.Equals("Page", StringComparison.OrdinalIgnoreCase))
                {
                    var baseLocation = GetAttribute(element, "BaseLoc");
                    if (EnqueuePath(currentPath, baseLocation, referencedPaths, documentQueue, out var pagePath))
                    {
                        selectedPaths.Add(pagePath);
                    }
                }

                foreach (var objectReference in element.Attributes()
                    .Where(attribute => attribute.Name.LocalName.Equals("ObjectRef", StringComparison.OrdinalIgnoreCase))
                    .Select(attribute => attribute.Value)
                    .Concat(elementName.Equals("ObjectRef", StringComparison.OrdinalIgnoreCase) ? [element.Value] : []))
                {
                    if (idOwners.TryGetValue(objectReference.Trim(), out var ownerPath)
                        && referencedPaths.Add(ownerPath))
                    {
                        selectedPaths.Add(ownerPath);
                        documentQueue.Enqueue(ownerPath);
                    }
                }

                var fileLocation = GetAttribute(element, "FileLoc");
                if (!string.IsNullOrWhiteSpace(fileLocation)
                    && EnqueuePath(currentPath, fileLocation, referencedPaths, documentQueue, out var filePath))
                {
                    selectedPaths.Add(filePath);
                }
            }
        }

        if (visited.Count == 0)
        {
            foreach (var entry in entries.Where(entry => IsConventionalPageEntry(entry.Path)))
            {
                selectedPaths.Add(entry.Path);
            }
        }

        return selectedPaths
            .Where(byPath.ContainsKey)
            .Select(path => byPath[path])
            .OrderBy(entry => GetPriority(entry.Path))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ReadOnlyMemory<byte> ReadBounded(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumEntryBytes)
            {
                throw new InvalidDataException("OFD package entry exceeds the allowed expanded size.");
            }
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/')
            || path.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("OFD package contains an invalid entry path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment is ".." or "."))
        {
            throw new InvalidDataException("OFD package contains a traversal entry path.");
        }
        return string.Join('/', segments.Where(segment => segment.Length > 0));
    }

    private static XDocument? LoadXml(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var input = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumEntryBytes,
                MaxCharactersFromEntities = 0,
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string GetAttribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static bool EnqueuePath(
        string sourcePath,
        string reference,
        HashSet<string> referencedPaths,
        Queue<string> queue) =>
        EnqueuePath(sourcePath, reference, referencedPaths, queue, out _);

    private static bool EnqueuePath(
        string sourcePath,
        string reference,
        HashSet<string> referencedPaths,
        Queue<string> queue,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var sourceDirectory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(sourceDirectory) ? reference : $"{sourceDirectory}/{reference}";
        try
        {
            resolvedPath = NormalizeEntryPath(combined);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        if (!referencedPaths.Add(resolvedPath)) return true;
        queue.Enqueue(resolvedPath);
        return true;
    }

    private static bool IsAlwaysCandidate(string path) =>
        path.EndsWith("original_invoice.xml", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Tags/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/CustomTag", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Tag", StringComparison.OrdinalIgnoreCase);

    private static bool IsTagEntry(string path) =>
        path.Contains("/Tags/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/CustomTag", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Tag", StringComparison.OrdinalIgnoreCase);

    private static bool IsConventionalPageEntry(string path) =>
        path.Contains("/Content.xml", StringComparison.OrdinalIgnoreCase);

    private static int GetPriority(string path)
    {
        if (path.EndsWith("original_invoice.xml", StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.Contains("/Tags/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/CustomTag", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Tag", StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.Contains("/Content.xml", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}