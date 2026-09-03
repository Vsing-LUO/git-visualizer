using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitVisualizer.Core;

namespace GitVisualizer.App.Services;

public static class RemoteCredentialResolver
{
	public static Task<RemoteCredential?> ResolveAsync(string remoteUrl, ICredentialVault credentialVault, CancellationToken cancellationToken = default(CancellationToken))
	{
		return ResolveAsync(remoteUrl, credentialVault, SystemGitCredentialProvider.GetAsync, cancellationToken);
	}

	public static async Task<RemoteCredential?> ResolveAsync(string remoteUrl, ICredentialVault credentialVault, Func<string, CancellationToken, Task<RemoteCredential?>> systemProvider, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (IsSsh(remoteUrl))
		{
			return new RemoteCredential(CredentialKind.SshAgent);
		}
		if (!RemoteUrlSecurity.IsHttps(remoteUrl))
		{
			return null;
		}
		string text = await credentialVault.GetAsync(RemoteCredentialKey.Create(remoteUrl), cancellationToken);
		if (!string.IsNullOrWhiteSpace(text))
		{
			try
			{
				RemoteCredential remoteCredential = JsonSerializer.Deserialize<RemoteCredential>(text);
				if ((object)remoteCredential != null)
				{
					return remoteCredential;
				}
			}
			catch (JsonException)
			{
			}
		}
		return await systemProvider(remoteUrl, cancellationToken);
	}

	private static bool IsSsh(string url)
	{
		if (!url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
		{
			if (url.Contains('@', StringComparison.Ordinal))
			{
				return url.Contains(':', StringComparison.Ordinal);
			}
			return false;
		}
		return true;
	}
}
