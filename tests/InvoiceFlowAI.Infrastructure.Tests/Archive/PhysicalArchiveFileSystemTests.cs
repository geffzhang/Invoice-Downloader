using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Archive;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class PhysicalArchiveFileSystemTests
{
    [Fact]
    public async Task Direct_child_enumeration_excludes_directories_nested_files_and_reparse_points()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoiceflow-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var directPath = Path.Combine(root, "hotel.pdf");
            var nestedDirectory = Path.Combine(root, "nested");
            Directory.CreateDirectory(nestedDirectory);
            await File.WriteAllTextAsync(directPath, "direct");
            await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "nested.pdf"), "nested");

            var linkedPath = Path.Combine(root, "linked-directory");
            try
            {
                Directory.CreateSymbolicLink(linkedPath, nestedDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
            }

            var fileSystem = new PhysicalArchiveFileSystem();
            var files = await fileSystem.EnumerateDirectChildFilesAsync(root, CancellationToken.None);

            files.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(directPath));
            files.Should().OnlyContain(path =>
                string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(root),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            files.Should().NotContain(path => path.Contains("nested", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sibling_copy_can_be_moved_without_changing_source_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoiceflow-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.pdf");
            var finalPath = Path.Combine(root, "archive", "final.pdf");
            var sourceBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x01, 0x02 };
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var fileSystem = new PhysicalArchiveFileSystem();

            var tempPath = await fileSystem.CopyToSiblingTempAsync(sourcePath, finalPath, CancellationToken.None);

            Path.GetDirectoryName(tempPath).Should().Be(Path.GetDirectoryName(finalPath));
            (await File.ReadAllBytesAsync(sourcePath)).Should().Equal(sourceBytes);
            (await File.ReadAllBytesAsync(tempPath)).Should().Equal(sourceBytes);
            await fileSystem.AtomicMoveAsync(tempPath, finalPath, CancellationToken.None);

            (await File.ReadAllBytesAsync(sourcePath)).Should().Equal(sourceBytes);
            (await File.ReadAllBytesAsync(finalPath)).Should().Equal(sourceBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Atomic_move_refuses_to_replace_an_existing_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoiceflow-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.pdf");
            var finalPath = Path.Combine(root, "archive", "final.pdf");
            var sourceBytes = new byte[] { 1, 2, 3 };
            var existingBytes = new byte[] { 9, 8, 7 };
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            await File.WriteAllBytesAsync(finalPath, existingBytes);
            var fileSystem = new PhysicalArchiveFileSystem();
            var tempPath = await fileSystem.CopyToSiblingTempAsync(sourcePath, finalPath, CancellationToken.None);

            var act = () => fileSystem.AtomicMoveAsync(tempPath, finalPath, CancellationToken.None);

            await act.Should().ThrowAsync<IOException>();
            (await File.ReadAllBytesAsync(finalPath)).Should().Equal(existingBytes);
            (await File.ReadAllBytesAsync(sourcePath)).Should().Equal(sourceBytes);
            (await File.ReadAllBytesAsync(tempPath)).Should().Equal(sourceBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}