using GitVisualizer.App.Services;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;

namespace GitVisualizer.Tests;

public sealed class CredentialSecurityTests
{
    [Theory]
    [InlineData("https://example.com/repo.git", "https://example.com/other.git", true)]
    [InlineData("https://example.com/repo.git", "https://EXAMPLE.com:443/other.git", true)]
    [InlineData("https://example.com/repo.git", "http://example.com/other.git", false)]
    [InlineData("https://example.com/repo.git", "https://other.example/other.git", false)]
    [InlineData("https://example.com/repo.git", "https://example.com:8443/other.git", false)]
    [InlineData("http://example.com/repo.git", "http://example.com/repo.git", false)]
	[InlineData("https://user:token@example.com/repo.git", "https://example.com/other.git", false)]
	[InlineData("https://example.com/repo.git", "https://user:token@example.com/other.git", false)]
    [InlineData("not-a-url", "https://example.com/repo.git", false)]
    public void SameOriginRequiresHttpsHostAndEffectivePort(
        string configured, string requested, bool expected)
    {
        Assert.Equal(expected, GitServiceSupport.IsSameHttpsOrigin(configured, requested));
    }

    [Fact]
    public void TokenCredentialIsRejectedForHttpAndCrossOriginCallbacks()
    {
        var token = new RemoteCredential(CredentialKind.HttpsToken, "user", "secret");

        Assert.Throws<InvalidOperationException>(() =>
            GitServiceSupport.CreateCredentials(
                "http://example.com/repo.git", "http://example.com/repo.git", token));
        Assert.Throws<InvalidOperationException>(() =>
            GitServiceSupport.CreateCredentials(
                "https://example.com/repo.git", "https://attacker.example/repo.git", token));
        Assert.Throws<InvalidOperationException>(() =>
            GitServiceSupport.CreateCredentials(
                "https://example.com/repo.git", "http://example.com/repo.git", token));
        Assert.Throws<InvalidOperationException>(() =>
            GitServiceSupport.FetchOptions("http://example.com/repo.git", token));
		Assert.Throws<InvalidOperationException>(() =>
			GitServiceSupport.FetchOptions(
				"https://user:embedded@example.com/repo.git", token));
		Assert.False(RemoteUrlSecurity.IsHttps(
			"https://user:embedded@example.com/repo.git"));
        Assert.NotNull(GitServiceSupport.CreateCredentials(
            "https://example.com/repo.git", "https://example.com/login", token));
    }

    [Fact]
    public async Task HttpCredentialResolutionDoesNotReadVaultOrInvokeSystemProvider()
    {
        var vault = new RecordingCredentialVault();
        var systemCalled = false;

        var credential = await RemoteCredentialResolver.ResolveAsync(
            "http://example.com/repo.git",
            vault,
            (_, _) =>
            {
                systemCalled = true;
                return Task.FromResult<RemoteCredential?>(
                    new RemoteCredential(CredentialKind.HttpsToken, "user", "secret"));
            });

        Assert.Null(credential);
        Assert.False(vault.Read);
        Assert.False(systemCalled);
    }

    private sealed class RecordingCredentialVault : ICredentialVault
    {
        public bool Read { get; private set; }
        public Task SaveAsync(string key, string secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            Read = true;
            return Task.FromResult<string?>(null);
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
