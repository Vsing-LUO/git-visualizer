using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.FileSystem;

public sealed class FileWorkspaceService : IFileWorkspaceService
{
    private const long MaxEditableSize = 5 * 1024 * 1024;

    public async Task<TextDocument> OpenTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var isBinary = IsBinary(bytes);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = isBinary ? string.Empty : encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        return new TextDocument(
            path,
            text,
            encoding.WebName,
            newLine,
            info.LastWriteTimeUtc,
            info.IsReadOnly || info.Length > MaxEditableSize || isBinary,
            isBinary,
            info.Length);
    }

    public Task OpenExternalAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("文件不存在。", fullPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task SaveTextAsync(
        TextDocument original,
        string text,
        bool allowExternalOverwrite,
        CancellationToken cancellationToken = default)
    {
        if (original.IsReadOnly)
        {
            throw new InvalidOperationException("此文件当前为只读。");
        }

        var info = new FileInfo(original.Path);
        if (info.Exists && !allowExternalOverwrite &&
            info.LastWriteTimeUtc != original.LastWriteTime.UtcDateTime)
        {
            throw new IOException("文件已被外部程序修改，请重新加载或确认覆盖。");
        }

        var encoding = Encoding.GetEncoding(original.EncodingName);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", original.NewLine, StringComparison.Ordinal);
        var directory = Path.GetDirectoryName(original.Path)
                        ?? throw new InvalidOperationException("无效的文件路径。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(original.Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, normalized, encoding, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, original.Path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task CreateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("目标已经存在。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)
                                  ?? throw new InvalidOperationException("无效的文件路径。"));
        if (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            await CreateEmptyWordDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("目标已经存在。");
        }

        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("目标已经存在，未覆盖任何内容。");
        }

        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            throw new FileNotFoundException("源路径不存在。", source);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        return Task.CompletedTask;
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            preambleLength = Encoding.UTF8.GetPreamble().Length;
            return new UTF8Encoding(true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            preambleLength = Encoding.Unicode.GetPreamble().Length;
            return Encoding.Unicode;
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return new UTF8Encoding(false);
    }

    private static bool IsBinary(byte[] bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sampleLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task CreateEmptyWordDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 4096, useAsync: true))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteEntryAsync(
                        archive, "[Content_Types].xml", ContentTypesXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "_rels/.rels", PackageRelationshipsXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "word/document.xml", DocumentXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "word/styles.xml", StylesXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "word/settings.xml", SettingsXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "word/_rels/document.xml.rels",
                        DocumentRelationshipsXml, cancellationToken).ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "docProps/core.xml", CorePropertiesXml, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive, "docProps/app.xml", AppPropertiesXml, cancellationToken)
                        .ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private const string ContentTypesXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
          <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
          <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
        </Types>
        """;

    private const string PackageRelationshipsXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
        </Relationships>
        """;

    private const string DocumentRelationshipsXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
        </Relationships>
        """;

    private const string DocumentXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:body>
            <w:p/>
            <w:sectPr>
              <w:pgSz w:w="12240" w:h="15840"/>
              <w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/>
              <w:cols w:space="720"/>
              <w:docGrid w:linePitch="360"/>
            </w:sectPr>
          </w:body>
        </w:document>
        """;

    private const string StylesXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault>
              <w:rPr>
                <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="等线" w:cs="Calibri"/>
                <w:sz w:val="22"/>
                <w:szCs w:val="22"/>
              </w:rPr>
            </w:rPrDefault>
            <w:pPrDefault>
              <w:pPr>
                <w:spacing w:after="120" w:line="264" w:lineRule="auto"/>
              </w:pPr>
            </w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
            <w:name w:val="Normal"/>
            <w:qFormat/>
          </w:style>
        </w:styles>
        """;

    private const string SettingsXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:zoom w:percent="100"/>
          <w:defaultTabStop w:val="720"/>
          <w:compat>
            <w:compatSetting w:name="compatibilityMode"
                             w:uri="http://schemas.microsoft.com/office/word"
                             w:val="15"/>
          </w:compat>
        </w:settings>
        """;

    private const string CorePropertiesXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                           xmlns:dc="http://purl.org/dc/elements/1.1/"
                           xmlns:dcterms="http://purl.org/dc/terms/"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <dc:creator>Git 可视化</dc:creator>
          <cp:lastModifiedBy>Git 可视化</cp:lastModifiedBy>
        </cp:coreProperties>
        """;

    private const string AppPropertiesXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
                    xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
          <Application>Git 可视化</Application>
          <AppVersion>1.0</AppVersion>
        </Properties>
        """;
}
