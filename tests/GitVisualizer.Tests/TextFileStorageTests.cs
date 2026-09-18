using System.Text;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.FileSystem;

namespace GitVisualizer.Tests;

public sealed class TextFileStorageTests
{
    public static IEnumerable<object[]> TextCases()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        foreach (var text in new[] { "", "中文", "一\n二\n", "一\r\n二\r\n", "一\r二", "一\n二" })
            yield return new object[] { encoding, text };
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task RoundTripPreservesBytesAndEditedEncoding(Encoding encoding, string text)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var service = new FileWorkspaceService();
        var original = await service.OpenTextAsync(path);
        Assert.False(original.IsReadOnly);
        Assert.False(original.IsBinary);
        Assert.Equal(text, original.Text);
        Assert.Equal(encoding.GetPreamble().Length != 0, original.HasBom);
        Assert.Equal(text.EndsWith('\n') || text.EndsWith('\r'), original.HasTrailingNewLine);
        await service.SaveTextAsync(dir.Path, original, text, false);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        await service.SaveTextAsync(dir.Path, original, text + "末", false);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(text + "末")).ToArray(),
            await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(new byte[] { 0xc3, 0x28 })]
    [InlineData(new byte[] { 0xd6, 0xd0, 0xce, 0xc4 })] // GBK 中文
    [InlineData(new byte[] { 0xff, 0xfe, 0x00 })]
    [InlineData(new byte[] { 0xfe, 0xff, 0xd8, 0x00 })]
    [InlineData(new byte[] { 0xef, 0xbb, 0xbf, 0xff })]
    [InlineData(new byte[] { 0, 1, 2 })]
    public async Task UnsafeBytesCannotBeSaved(byte[] bytes)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllBytesAsync(path, bytes);
        var doc = await TextFileStorage.OpenAsync(path);
        Assert.True(doc.IsReadOnly);
        Assert.Empty(doc.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TextFileStorage.SaveAsync(dir.Path, doc, "replacement"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("a\r\nb\nc")]
    [InlineData("a\rb\nc")]
    public async Task MixedNewLinesAreReadOnly(string text)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, text);
        var doc = await TextFileStorage.OpenAsync(path);
        Assert.True(doc.HasMixedNewLines);
        Assert.True(doc.IsReadOnly);
        Assert.Equal(text, doc.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TextFileStorage.SaveAsync(dir.Path, doc, "edit"));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task BomOnlyExternalChangeAndLegacyOverwriteAreRejected()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "same", new UTF8Encoding(false));
        var service = new FileWorkspaceService();
        var doc = await service.OpenTextAsync(path);
        await File.WriteAllTextAsync(path, "same", new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(path, doc.LastWriteTime.UtcDateTime);
        foreach (var overwrite in new[] { false, true })
            await Assert.ThrowsAsync<ExternalFileChangedException>(() => service.SaveTextAsync(dir.Path, doc, "edit", overwrite));
        Assert.Equal("same", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadOnlyAtOpenOrAfterOpenCannotBeSaved()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.True((await TextFileStorage.OpenAsync(path)).IsReadOnly);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TextFileStorage.SaveAsync(dir.Path, doc, "edit"));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("race")]
    [InlineData("delete")]
    [InlineData("cancel")]
    [InlineData("metadata-race")]
    public async Task FailuresCleanTemporaryAndPreserveTarget(string fault)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        using var cancellation = new CancellationTokenSource();
        var operations = new FaultOperations(path, fault, cancellation);
        await Assert.ThrowsAnyAsync<Exception>(() => TextFileStorage.SaveCoreAsync(dir.Path, doc, "draft", operations, cancellation.Token));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        if (fault == "delete") Assert.False(File.Exists(path));
        else Assert.Equal(fault == "race" ? "external" : "original", await File.ReadAllTextAsync(path));
        Assert.Equal("original", doc.Text);
        Assert.False(operations.Replaced);
    }

    [Fact]
    public async Task InvalidEditorSurrogateCannotReplaceFile()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => TextFileStorage.SaveAsync(dir.Path, doc, "\ud800"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveHonorsOtherReadersDeletionSharing(bool allowDelete)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        using var otherReader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | (allowDelete ? FileShare.Delete : FileShare.None));
        if (allowDelete)
        {
            await TextFileStorage.SaveAsync(dir.Path, doc, "draft");
            Assert.Equal("draft", await File.ReadAllTextAsync(path));
            using var reader = new StreamReader(otherReader, leaveOpen: true);
            Assert.Equal("original", await reader.ReadToEndAsync());
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => TextFileStorage.SaveAsync(dir.Path, doc, "draft"));
            Assert.Equal(unchecked((int)0x80070020), error.HResult);
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    public async Task PartialWindowsReplacementFailureRetainsRecoverableDraft(int nativeError)
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        var operations = new ReplacementBoundaryOperations(nativeError);
        var error = await Assert.ThrowsAnyAsync<IOException>(() =>
            TextFileStorage.SaveCoreAsync(dir.Path, doc, "draft", operations));
        Assert.Equal(unchecked((int)0x80070000) | nativeError, error.HResult);
        var retained = Assert.Single(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Equal("draft", await File.ReadAllTextAsync(retained));
        Assert.Equal(retained, error.Data["RetainedTemporaryPath"]);
        Assert.Contains(retained, error.Message);
        Assert.False(File.Exists(path));
        if (nativeError == 1177) Assert.Equal("original", await File.ReadAllTextAsync(path + ".moved"));
    }

    [Fact]
    public async Task CleanupSharingFailureDoesNotReplaceOriginalNativeFailure()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "text.txt");
        await File.WriteAllTextAsync(path, "original");
        var doc = await TextFileStorage.OpenAsync(path);
        using var operations = new ReplacementBoundaryOperations(1175, lockTemporary: true);
        var error = await Assert.ThrowsAnyAsync<IOException>(() =>
            TextFileStorage.SaveCoreAsync(dir.Path, doc, "draft", operations));
        Assert.Equal(unchecked((int)0x80070497), error.HResult);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        var retained = Assert.Single(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Equal("draft", await File.ReadAllTextAsync(retained));
        Assert.Equal(retained, error.Data["RetainedTemporaryPath"]);
        Assert.IsAssignableFrom<IOException>(error.Data["TemporaryCleanupError"]);
    }

    private sealed class ReplacementBoundaryOperations(int nativeError, bool lockTemporary = false)
        : TextFileOperations, IDisposable
    {
        private FileStream? held;
        internal override void Replace(string temporary, string target)
        {
            // Emulate the documented post-failure filesystem states, not a successful write.
            if (nativeError == 1176) File.Delete(target);
            if (nativeError == 1177) File.Move(target, target + ".moved");
            if (lockTemporary) held = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("Native replacement failure", unchecked((int)0x80070000) | nativeError);
        }
        public void Dispose() => held?.Dispose();
    }

    private sealed class FaultOperations(string target, string fault, CancellationTokenSource cancellation) : TextFileOperations
    {
        public bool Replaced { get; private set; }
        internal override async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
        {
            if (fault == "write")
            {
                await File.WriteAllBytesAsync(path, bytes[..1], token);
                throw new IOException("Simulated disk full");
            }
            await base.WriteAsync(path, bytes, token);
            if (fault == "race") await File.WriteAllTextAsync(target, "external", token);
            if (fault == "delete") File.Delete(target);
            if (fault == "cancel") cancellation.Cancel();
            if (fault == "metadata-race") File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        }
        internal override void Replace(string temporary, string target)
        {
            if (fault == "replace") throw new IOException("Simulated replacement failure");
            Replaced = true;
            base.Replace(temporary, target);
        }
    }
}
