using System.Text.Json;
using GitVisualizer.Core;

namespace GitVisualizer.App.Services;

public static class RemoteCredentialResolver
{
    public static Task<RemoteCredential?> ResolveAsync(
        string remoteUrl,
        ICredentialVault credentialVault,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(
            remoteUrl,
            credentialVault,
            SystemGitCredentialProvider.GetAsync,
            cancellationToken);

    public static async Task<RemoteCredential?> ResolveAsync(
        string remoteUrl,
        ICredentialVault credentialVault,
        Func<string, CancellationToken, Task<RemoteCredential?>> systemProvider,
        CancellationToken cancellationToken = default)
    {
        if (IsSsh(remoteUrl))
        {
            return new RemoteCredential(CredentialKind.SshAgent);
        }

        var json = await credentialVault.GetAsync(
            RemoteCredentialKey.Create(remoteUrl),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var repositoryCredential =
                    JsonSerializer.Deserialize<RemoteCredential>(json);
                if (repositoryCredential is not null)
                {
                    return repositoryCredential;
                }
            }
            catch (JsonException)
            {
                // A damaged repository entry must not prevent the safe system
                // Git credential fallback from being attempted.
            }
        }

        return await systemProvider(remoteUrl, cancellationToken);
    }

    private static bool IsSsh(string url) =>
        url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        (url.Contains('@', StringComparison.Ordinal) &&
         url.Contains(':', StringComparison.Ordinal));
}
