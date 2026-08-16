using System.Collections.Concurrent;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

internal static class GitServiceSupport
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

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

    public static Credentials? CreateCredentials(RemoteCredential? credential)
    {
        if (credential is null || credential.Kind == CredentialKind.None)
        {
            return null;
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

    public static FetchOptions FetchOptions(RemoteCredential? credential)
    {
        return new FetchOptions
        {
            CredentialsProvider = (_, _, _) => CreateCredentials(credential)
        };
    }

    public static PushOptions PushOptions(RemoteCredential? credential)
    {
        return new PushOptions
        {
            CredentialsProvider = (_, _, _) => CreateCredentials(credential)
        };
    }

    public static CloneOptions CloneOptions(RemoteCredential? credential)
    {
        return new CloneOptions(FetchOptions(credential));
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

    public static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}
