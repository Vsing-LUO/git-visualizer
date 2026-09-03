using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Persistence;

public sealed class EditorDraftStore : IEditorDraftStore
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<EditorDraft?> LoadAsync(
        string repositoryPath,
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        var path = DraftPath(repositoryPath, documentPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var payload = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var draft = JsonSerializer.Deserialize<EditorDraft>(payload, JsonOptions);
            if (draft is null ||
                !PathsEqual(draft.RepositoryPath, repositoryPath) ||
                !PathsEqual(draft.DocumentPath, documentPath))
            {
                throw new InvalidDataException("编辑器草稿与请求的文件不匹配。");
            }
            return draft;
        }
        catch (CryptographicException)
        {
            File.Delete(path);
            return null;
        }
        catch (JsonException)
        {
            File.Delete(path);
            return null;
        }
        catch (InvalidDataException)
        {
            File.Delete(path);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(EditorDraft draft, CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = DraftPath(draft.RepositoryPath, draft.DocumentPath);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
                var encrypted = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(
        string repositoryPath,
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = DraftPath(repositoryPath, documentPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MoveAsync(
        string repositoryPath,
        string oldDocumentPath,
        string newDocumentPath,
        CancellationToken cancellationToken = default)
    {
        var draft = await LoadAsync(repositoryPath, oldDocumentPath, cancellationToken)
            .ConfigureAwait(false);
        if (draft is null)
        {
            return;
        }

        await SaveAsync(draft with
        {
            DocumentPath = Path.GetFullPath(newDocumentPath),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        await DeleteAsync(repositoryPath, oldDocumentPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in Directory.EnumerateFiles(LocalPaths.DraftDirectory, "*.draft"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge)
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string DraftKey(string repositoryPath, string documentPath)
    {
        var canonical = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + "\n" +
                        Path.GetFullPath(documentPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
    }

    private static string DraftPath(string repositoryPath, string documentPath) =>
        Path.Combine(LocalPaths.DraftDirectory, DraftKey(repositoryPath, documentPath) + ".draft");

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
