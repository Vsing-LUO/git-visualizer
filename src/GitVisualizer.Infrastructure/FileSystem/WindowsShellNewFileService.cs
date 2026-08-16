using System.Runtime.InteropServices;
using System.Text;
using GitVisualizer.Core;
using Microsoft.Win32;

namespace GitVisualizer.Infrastructure.FileSystem;

public sealed class WindowsShellNewFileService : ISystemNewFileService
{
    private const string ExplorerShellNewCache =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew";

    private readonly object syncRoot = new();
    private IReadOnlyDictionary<string, ShellNewDescriptor> descriptors =
        new Dictionary<string, ShellNewDescriptor>(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<SystemNewFileType>> GetAvailableTypesAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SystemNewFileType>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = Discover(cancellationToken);
            lock (syncRoot)
            {
                descriptors = discovered.ToDictionary(
                    item => item.Type.Id,
                    StringComparer.OrdinalIgnoreCase);
            }

            return discovered.Select(item => item.Type).ToArray();
        }, cancellationToken);

    public async Task CreateAsync(
        string path,
        string typeId,
        CancellationToken cancellationToken = default)
    {
        ShellNewDescriptor? descriptor;
        lock (syncRoot)
        {
            descriptors.TryGetValue(typeId, out descriptor);
        }

        if (descriptor is null)
        {
            await GetAvailableTypesAsync(cancellationToken).ConfigureAwait(false);
            lock (syncRoot)
            {
                descriptors.TryGetValue(typeId, out descriptor);
            }
        }

        if (descriptor is null)
        {
            throw new InvalidOperationException("该系统文件类型已不可用，请重新打开新建菜单。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(
                descriptor.Type.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"文件名必须使用 {descriptor.Type.Extension} 扩展名。");
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException("目标已经存在。");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无效的文件路径。"));

        switch (descriptor.Kind)
        {
            case ShellNewCreationKind.Template:
                File.Copy(
                    descriptor.TemplatePath
                    ?? throw new InvalidOperationException("系统模板文件不存在。"),
                    fullPath,
                    false);
                break;
            case ShellNewCreationKind.Data:
                await File.WriteAllBytesAsync(
                        fullPath,
                        descriptor.Data ?? [],
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShellNewCreationKind.Empty:
                await using (var stream = new FileStream(
                                 fullPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 1,
                                 useAsync: true))
                {
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                throw new InvalidOperationException("不支持该系统文件创建方式。");
        }
    }

    private static IReadOnlyList<ShellNewDescriptor> Discover(
        CancellationToken cancellationToken)
    {
        using var cache = Registry.CurrentUser.OpenSubKey(ExplorerShellNewCache);
        var cachedClasses = cache?.GetValue("Classes") as string[];
        var extensions = cachedClasses is { Length: > 0 }
            ? cachedClasses
            : Registry.ClassesRoot.GetSubKeyNames()
                .Where(name => name.StartsWith(".", StringComparison.Ordinal))
                .ToArray();

        var results = new List<ShellNewDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = NormalizeExtension(candidate);
            if (extension is null || !seen.Add(extension))
            {
                continue;
            }

            try
            {
                var descriptor = TryReadDescriptor(extension);
                if (descriptor is not null)
                {
                    results.Add(descriptor);
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException or
                    ArgumentException)
            {
                // A broken third-party registration must not hide the other valid templates.
            }
        }

        return results;
    }

    private static ShellNewDescriptor? TryReadDescriptor(string extension)
    {
        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        if (extensionKey is null)
        {
            return null;
        }

        var defaultProgId = extensionKey.GetValue(null) as string;
        var candidates = new List<(string Path, string? ProgId)>();
        if (!string.IsNullOrWhiteSpace(defaultProgId))
        {
            candidates.Add(($@"{extension}\{defaultProgId}\ShellNew", defaultProgId));
        }
        candidates.Add(($@"{extension}\ShellNew", defaultProgId));
        if (!string.IsNullOrWhiteSpace(defaultProgId))
        {
            candidates.Add(($@"{defaultProgId}\ShellNew", defaultProgId));
        }

        foreach (var child in extensionKey.GetSubKeyNames()
                     .Where(name => !name.Equals("OpenWithProgids", StringComparison.OrdinalIgnoreCase) &&
                                    !name.Equals("PersistentHandler", StringComparison.OrdinalIgnoreCase) &&
                                    !name.Equals("ShellEx", StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(($@"{extension}\{child}\ShellNew", child));
        }

        foreach (var candidate in candidates.DistinctBy(
                     item => item.Path,
                     StringComparer.OrdinalIgnoreCase))
        {
            using var shellNew = Registry.ClassesRoot.OpenSubKey(candidate.Path);
            if (shellNew is null)
            {
                continue;
            }

            var descriptor = CreateDescriptor(
                extension,
                candidate.ProgId,
                shellNew);
            if (descriptor is not null)
            {
                return descriptor;
            }
        }

        return null;
    }

    private static ShellNewDescriptor? CreateDescriptor(
        string extension,
        string? progId,
        RegistryKey shellNew)
    {
        if (shellNew.GetSubKeyNames().Contains("Config", StringComparer.OrdinalIgnoreCase) ||
            shellNew.GetValueNames().Contains("Handler", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var valueNames = shellNew.GetValueNames();
        var fileName = shellNew.GetValue("FileName") as string;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var templatePath = ResolveTemplatePath(fileName);
            if (templatePath is not null)
            {
                return BuildDescriptor(
                    extension,
                    progId,
                    shellNew,
                    ShellNewCreationKind.Template,
                    templatePath,
                    null);
            }
        }

        if (valueNames.Contains("Data", StringComparer.OrdinalIgnoreCase) &&
            shellNew.GetValue("Data") is byte[] data)
        {
            return BuildDescriptor(
                extension,
                progId,
                shellNew,
                ShellNewCreationKind.Data,
                null,
                data);
        }

        if (valueNames.Contains("NullFile", StringComparer.OrdinalIgnoreCase))
        {
            return BuildDescriptor(
                extension,
                progId,
                shellNew,
                ShellNewCreationKind.Empty,
                null,
                null);
        }

        return null;
    }

    private static ShellNewDescriptor BuildDescriptor(
        string extension,
        string? progId,
        RegistryKey shellNew,
        ShellNewCreationKind kind,
        string? templatePath,
        byte[]? data)
    {
        var displayName = ResolveDisplayName(extension, progId, shellNew);
        var id = extension.ToLowerInvariant();
        var suggestedName = $"新建 {displayName}{extension}";
        return new ShellNewDescriptor(
            new SystemNewFileType(id, extension, displayName, suggestedName),
            kind,
            templatePath,
            data);
    }

    private static string ResolveDisplayName(
        string extension,
        string? progId,
        RegistryKey shellNew)
    {
        var itemName = ResolveIndirectString(shellNew.GetValue("ItemName") as string);
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            return itemName;
        }

        if (!string.IsNullOrWhiteSpace(progId))
        {
            using var progIdKey = Registry.ClassesRoot.OpenSubKey(progId);
            var friendlyTypeName = ResolveIndirectString(
                progIdKey?.GetValue("FriendlyTypeName") as string);
            if (!string.IsNullOrWhiteSpace(friendlyTypeName))
            {
                return friendlyTypeName;
            }

            var description = ResolveIndirectString(progIdKey?.GetValue(null) as string);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
    }

    private static string? ResolveIndirectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!value.StartsWith('@'))
        {
            return value.Trim();
        }

        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, nint.Zero) == 0
            ? buffer.ToString().Trim()
            : null;
    }

    private static string? ResolveTemplatePath(string fileName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName.Trim().Trim('"'));
        string[] candidates = Path.IsPathRooted(expanded)
            ? [expanded]
            :
            [
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "ShellNew",
                    expanded),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "ShellNew",
                    expanded),
                expanded
            ];

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private static string? NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (!extension.StartsWith(".", StringComparison.Ordinal) ||
            extension.Length is < 2 or > 32 ||
            extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            extension.Contains('\\') ||
            extension.Contains('/'))
        {
            return null;
        }
        return extension;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source,
        StringBuilder output,
        uint outputLength,
        nint reserved);

    private sealed record ShellNewDescriptor(
        SystemNewFileType Type,
        ShellNewCreationKind Kind,
        string? TemplatePath,
        byte[]? Data);

    private enum ShellNewCreationKind
    {
        Empty,
        Data,
        Template
    }
}
