using System.IO.Compression;
using System.Diagnostics;
using System.Xml.Linq;
using GitVisualizer.Infrastructure.FileSystem;

namespace GitVisualizer.Tests;

public sealed class FileWorkspaceServiceTests
{
    [Fact]
    public async Task SavePreservesCrLfAndRejectsExternalOverwrite()
    {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "中文.txt");
        await File.WriteAllTextAsync(path, "一\r\n二\r\n", new System.Text.UTF8Encoding(true));
        var service = new FileWorkspaceService();
        var document = await service.OpenTextAsync(path);
        Assert.Equal("\r\n", document.NewLine);
        Assert.Equal("utf-8", document.EncodingName);

        await service.SaveTextAsync(temporary.Path, document, "一\n二\n三\n", false);
        Assert.Contains("\r\n三\r\n", await File.ReadAllTextAsync(path));

        var metadataOnlyChange = await service.OpenTextAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        await service.SaveTextAsync(temporary.Path, metadataOnlyChange, "metadata-only change is safe", false);
        Assert.Equal("metadata-only change is safe", await File.ReadAllTextAsync(path));

        var stale = await service.OpenTextAsync(path);
        await Task.Delay(20);
        await File.AppendAllTextAsync(path, "external");
        File.SetLastWriteTimeUtc(path, stale.LastWriteTime.UtcDateTime);
        var externalChange = await Assert.ThrowsAsync<GitVisualizer.Core.ExternalFileChangedException>(
            () => service.SaveTextAsync(temporary.Path, stale, "overwrite", false));
        Assert.Equal(Path.GetFullPath(path), externalChange.FilePath);

        var deleted = await service.OpenTextAsync(path);
        File.Delete(path);
        await Assert.ThrowsAsync<GitVisualizer.Core.ExternalFileChangedException>(
            () => service.SaveTextAsync(temporary.Path, deleted, "recreate", false));
    }

    [Fact]
    public async Task BinaryAndLargeFilesAreReadOnly()
    {
        using var temporary = new TemporaryDirectory();
        var binaryPath = System.IO.Path.Combine(temporary.Path, "asset.bin");
        await File.WriteAllBytesAsync(binaryPath, [1, 2, 0, 4]);
        var service = new FileWorkspaceService();
        var document = await service.OpenTextAsync(binaryPath);
        Assert.True(document.IsBinary);
        Assert.True(document.IsReadOnly);
    }

    [Fact]
    public async Task CreateDocx_WritesAValidOpenXmlPackage()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "空白文档.docx");
        var service = new FileWorkspaceService();

        await service.CreateFileAsync(temporary.Path, path);

        using var archive = ZipFile.OpenRead(path);
        var contentTypes = Assert.Single(
            archive.Entries, entry => entry.FullName == "[Content_Types].xml");
        var document = Assert.Single(
            archive.Entries, entry => entry.FullName == "word/document.xml");
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName == "word/styles.xml");
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName == "word/settings.xml");

        using var contentTypesStream = contentTypes.Open();
        using var documentStream = document.Open();
        Assert.Equal(
            "Types",
            XDocument.Load(contentTypesStream).Root?.Name.LocalName);
        var documentXml = XDocument.Load(documentStream);
        Assert.Equal("document", documentXml.Root?.Name.LocalName);
        Assert.Contains(
            documentXml.Descendants(),
            element => element.Name.LocalName == "sectPr");
        Assert.True(new FileInfo(path).Length > 1000);

        var openedDocument = await service.OpenTextAsync(path);
        Assert.True(openedDocument.IsBinary);
        Assert.True(openedDocument.IsReadOnly);
        Assert.Equal(string.Empty, openedDocument.Text);
    }

    [Fact]
    public async Task MutationsRejectRepositoryRootGitDirectoryAndOutsidePaths()
    {
        using var repository = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var service = new FileWorkspaceService();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateFileAsync(repository.Path, Path.Combine(outside.Path, "escape.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateFileAsync(repository.Path, Path.Combine(repository.Path, ".git", "config")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.DeleteAsync(repository.Path, repository.Path));

        Assert.False(File.Exists(Path.Combine(outside.Path, "escape.txt")));
        Assert.True(Directory.Exists(Path.Combine(repository.Path, ".git")));
    }

    [Fact]
    public async Task MutationsRejectDirectoryLinkEscapes()
    {
        using var repository = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var link = Path.Combine(repository.Path, "linked-outside");
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, outside.Path })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(startInfo)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var service = new FileWorkspaceService();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.CreateFileAsync(
                    repository.Path, Path.Combine(link, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(outside.Path, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task UnicodePathCanBeCreatedAndRenamedInsideRepository()
    {
        using var repository = new TemporaryDirectory();
        var service = new FileWorkspaceService();
        var source = Path.Combine(repository.Path, "中文文件.txt");
        var destination = Path.Combine(repository.Path, "重命名后.txt");

        await service.CreateFileAsync(repository.Path, source);
        await service.MoveAsync(repository.Path, source, destination);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
    }
}
