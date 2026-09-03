using System.Collections.Concurrent;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

internal static class GitServiceSupport
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> OfficeDocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm",
            ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm",
            ".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm",
            ".pps", ".ppsx", ".ppsm"
        };

    public static SemaphoreSlim LockFor(string repositoryPath) =>
        RepositoryLocks.GetOrAdd(Path.GetFullPath(repositoryPath), _ => new SemaphoreSlim(1, 1));

    public static Signature ResolveSignature(Repository repository, GitIdentity? identity)
    {
        if (identity is not null &&
            !string.IsNullOrWhiteSpace(identity.Name) &&
            !string.IsNullOrWhiteSpace(identity.Email))
        {
            return new Signature(identity.Name.Trim(), identity.Email.Trim(), DateTimeOffset.Now);
        }

        var name = repository.Config.Get<string>("user.name")?.Value;
        var email = repository.Config.Get<string>("user.email")?.Value;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("请先配置 Git 用户名和邮箱。");
        }

        return new Signature(name, email, DateTimeOffset.Now);
    }

    public static Credentials? CreateCredentials(
        string configuredRemoteUrl,
        string requestedUrl,
        RemoteCredential? credential)
    {
        if (credential is null || credential.Kind == CredentialKind.None)
        {
            return null;
        }

        if (credential.Kind == CredentialKind.HttpsToken &&
            !IsSameHttpsOrigin(configuredRemoteUrl, requestedUrl))
        {
            throw new InvalidOperationException(
                "为保护访问令牌，认证仅允许发送到原始 HTTPS 远程仓库的同源地址。");
        }

        return credential.Kind switch
        {
            CredentialKind.HttpsToken => new UsernamePasswordCredentials
            {
                Username = string.IsNullOrWhiteSpace(credential.UserName) ? "git" : credential.UserName,
                Password = credential.Secret
            },
            // LibGit2Sharp 0.32 delegates SSH authentication to the active SSH agent.
            CredentialKind.SshKey => new DefaultCredentials(),
            CredentialKind.SshAgent => new DefaultCredentials(),
            _ => null
        };
    }

    public static FetchOptions FetchOptions(string remoteUrl, RemoteCredential? credential)
    {
        EnsureTokenRemoteIsHttps(remoteUrl, credential);
        return new FetchOptions
        {
            CredentialsProvider = (requestedUrl, _, _) =>
                CreateCredentials(remoteUrl, requestedUrl, credential)
        };
    }

    public static PushOptions PushOptions(string remoteUrl, RemoteCredential? credential)
    {
        EnsureTokenRemoteIsHttps(remoteUrl, credential);
        return new PushOptions
        {
            CredentialsProvider = (requestedUrl, _, _) =>
                CreateCredentials(remoteUrl, requestedUrl, credential)
        };
    }

    public static CloneOptions CloneOptions(string remoteUrl, RemoteCredential? credential)
    {
        return new CloneOptions(FetchOptions(remoteUrl, credential));
    }

    internal static bool IsSameHttpsOrigin(string configuredRemoteUrl, string requestedUrl)
    {
        if (!Uri.TryCreate(configuredRemoteUrl, UriKind.Absolute, out var configured) ||
            !Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested) ||
            !configured.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !requested.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(configured.UserInfo) ||
            !string.IsNullOrEmpty(requested.UserInfo))
        {
            return false;
        }

        return configured.IdnHost.Equals(requested.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               EffectivePort(configured) == EffectivePort(requested);
    }

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort ? 443 : uri.Port;

    internal static void EnsureTokenRemoteIsHttps(
        string remoteUrl, RemoteCredential? credential)
    {
        if (credential?.Kind == CredentialKind.HttpsToken &&
            (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrWhiteSpace(uri.Host) ||
             !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new InvalidOperationException(
                "个人访问令牌只能用于绝对 HTTPS 远程仓库地址。");
        }
    }

    public static GitChangeState MapStatus(FileStatus status) =>
        MapStatus(status, IsStaged(status));

    public static GitChangeState MapStatus(FileStatus status, bool staged)
    {
        if (status.HasFlag(FileStatus.Conflicted))
        {
            return GitChangeState.Conflicted;
        }
        if (!staged && status.HasFlag(FileStatus.Ignored))
        {
            return GitChangeState.Ignored;
        }
        if (!staged && status.HasFlag(FileStatus.NewInWorkdir))
        {
            return GitChangeState.Untracked;
        }
        if (staged && status.HasFlag(FileStatus.NewInIndex))
        {
            return GitChangeState.Added;
        }
        if ((staged && status.HasFlag(FileStatus.RenamedInIndex)) ||
            (!staged && status.HasFlag(FileStatus.RenamedInWorkdir)))
        {
            return GitChangeState.Renamed;
        }
        if ((staged && status.HasFlag(FileStatus.DeletedFromIndex)) ||
            (!staged && status.HasFlag(FileStatus.DeletedFromWorkdir)))
        {
            return GitChangeState.Deleted;
        }
        if ((staged &&
             (status.HasFlag(FileStatus.ModifiedInIndex) ||
              status.HasFlag(FileStatus.TypeChangeInIndex))) ||
            (!staged &&
             (status.HasFlag(FileStatus.ModifiedInWorkdir) ||
              status.HasFlag(FileStatus.TypeChangeInWorkdir))))
        {
            return GitChangeState.Modified;
        }

        return GitChangeState.Unmodified;
    }

    public static bool IsStaged(FileStatus status) => HasStagedChanges(status);

    public static bool HasStagedChanges(FileStatus status) =>
        status.HasFlag(FileStatus.NewInIndex) ||
        status.HasFlag(FileStatus.ModifiedInIndex) ||
        status.HasFlag(FileStatus.DeletedFromIndex) ||
        status.HasFlag(FileStatus.RenamedInIndex) ||
        status.HasFlag(FileStatus.TypeChangeInIndex);

    public static bool HasUnstagedChanges(FileStatus status) =>
        status.HasFlag(FileStatus.NewInWorkdir) ||
        status.HasFlag(FileStatus.ModifiedInWorkdir) ||
        status.HasFlag(FileStatus.DeletedFromWorkdir) ||
        status.HasFlag(FileStatus.RenamedInWorkdir) ||
        status.HasFlag(FileStatus.TypeChangeInWorkdir) ||
        status.HasFlag(FileStatus.Conflicted) ||
        status.HasFlag(FileStatus.Ignored);

    public static bool IsTransientOfficeLockFile(StatusEntry entry)
    {
        if (!entry.State.HasFlag(FileStatus.NewInWorkdir) || HasStagedChanges(entry.State))
        {
            return false;
        }

        var fileName = Path.GetFileName(entry.FilePath);
        return fileName.StartsWith("~$", StringComparison.Ordinal) &&
               OfficeDocumentExtensions.Contains(Path.GetExtension(fileName));
    }

    public static bool IsMeaningfulChange(StatusEntry entry) =>
        entry.State != FileStatus.Unaltered &&
        !entry.State.HasFlag(FileStatus.Ignored) &&
        !IsTransientOfficeLockFile(entry);

    public static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}
