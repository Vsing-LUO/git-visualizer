using System.Text.Json;
using GitVisualizer.App.Services;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class RemoteCredentialResolverTests
{
    private const string RemoteUrl =
        "https://github.com/Vsing-LUO/pingjutest.git";

    [Fact]
    public async Task ResolveAsync_PrefersRepositoryCredentialOverSystemCredential()
    {
        var vault = new MemoryCredentialVault();
        var repositoryCredential = new RemoteCredential(
            CredentialKind.HttpsToken,
            "repository-user",
            "repository-token",
            Remember: true);
        await vault.SaveAsync(
            RemoteCredentialKey.Create(RemoteUrl),
            JsonSerializer.Serialize(repositoryCredential));
        var systemProviderCalled = false;

        var resolved = await RemoteCredentialResolver.ResolveAsync(
            RemoteUrl,
            vault,
            (_, _) =>
            {
                systemProviderCalled = true;
                return Task.FromResult<RemoteCredential?>(new RemoteCredential(
                    CredentialKind.HttpsToken,
                    "system-user",
                    "system-token"));
            });

        Assert.Equal(repositoryCredential, resolved);
        Assert.False(systemProviderCalled);
    }

    [Fact]
    public async Task ResolveAsync_UsesSystemCredentialWhenRepositoryHasNone()
    {
        var vault = new MemoryCredentialVault();
        var systemCredential = new RemoteCredential(
            CredentialKind.HttpsToken,
            "system-user",
            "system-token");

        var resolved = await RemoteCredentialResolver.ResolveAsync(
            RemoteUrl,
            vault,
            (_, _) => Task.FromResult<RemoteCredential?>(systemCredential));

        Assert.Equal(systemCredential, resolved);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenNeitherSourceHasCredential()
    {
        var resolved = await RemoteCredentialResolver.ResolveAsync(
            RemoteUrl,
            new MemoryCredentialVault(),
            (_, _) => Task.FromResult<RemoteCredential?>(null));

        Assert.Null(resolved);
    }
}
