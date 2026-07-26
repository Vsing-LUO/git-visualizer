using System.IO.Compression;
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

        await service.SaveTextAsync(document, "一\n二\n三\n", false);
        Assert.Contains("\r\n三\r\n", await File.ReadAllTextAsync(path));

        var stale = await service.OpenTextAsync(path);
        await Task.Delay(20);
        await File.AppendAllTextAsync(path, "external");
        await Assert.ThrowsAsync<IOException>(
            () => service.SaveTextAsync(stale, "overwrite", false));
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

        await service.CreateFileAsync(path);

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
}
