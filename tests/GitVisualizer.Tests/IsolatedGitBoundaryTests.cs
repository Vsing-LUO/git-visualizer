using GitVisualizer.App.Services;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class IsolatedGitBoundaryTests
{
    private static readonly GitIdentity Identity = new("隔离身份", "isolated@example.invalid");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothGlobalWriteEntrypointsRejectAndLeaveConfigurationUnchanged(bool repositoryEntry)
    {
        Assert.True(LocalPaths.Default.IsIsolated);
        using var temporary = new TemporaryDirectory();
        Repository.Init(temporary.Path);
        var localFile = Path.Combine(temporary.Path, ".git", "config");
        var globalBefore = File.ReadAllBytes(TestDataIsolation.GlobalConfigFile);
        var localBefore = File.ReadAllBytes(localFile);
        var log = new MemoryOperationLogStore();
        var service = new LibGitRepositoryService(new RecoveryService(), log);
        // Verify the sentinel really is the configuration consumed by this service.
        Assert.Equal(new GitIdentity("Isolation Sentinel", "sentinel@example.invalid"),
            await service.GetDefaultIdentityAsync());

        var result = repositoryEntry
            ? await service.SetIdentityAsync(temporary.Path, Identity, global: true)
            : await service.SetGlobalIdentityAsync(Identity);

        Assert.False(result.Success);
        Assert.Equal("IsolatedProfile", result.ErrorCode);
        Assert.Contains("隔离模式", result.ErrorMessage);
        Assert.Equal(globalBefore, File.ReadAllBytes(TestDataIsolation.GlobalConfigFile));
        Assert.Equal(localBefore, File.ReadAllBytes(localFile));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task GlobalWriteRejectsBeforeOpeningEvenANonexistentRepository()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "must-not-be-created");
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        var result = await service.SetIdentityAsync(path, Identity, global: true);
        Assert.Equal("IsolatedProfile", result.ErrorCode);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task LocalIdentityStillWorksWithoutChangingGlobalConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        Repository.Init(temporary.Path);
        var globalBefore = File.ReadAllBytes(TestDataIsolation.GlobalConfigFile);
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        var result = await service.SetIdentityAsync(temporary.Path, Identity, global: false);
        Assert.True(result.Success, result.ErrorMessage);
        using var repository = new Repository(temporary.Path);
        Assert.Equal(Identity.Name, repository.Config.Get<string>("user.name", ConfigurationLevel.Local).Value);
        Assert.Equal(Identity.Email, repository.Config.Get<string>("user.email", ConfigurationLevel.Local).Value);
        Assert.Equal(globalBefore, File.ReadAllBytes(TestDataIsolation.GlobalConfigFile));
    }

    [Theory]
    [InlineData("https://example.invalid/repo.git")]
    [InlineData("ssh://example.invalid/repo.git")]
    [InlineData("invalid")]
    public async Task CredentialBoundaryRejectsBeforeAnyProcessStart(string url)
    {
        var starts = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SystemGitCredentialProvider.GetAsync(url, _ => { starts++; return null; }));
        Assert.Contains("隔离模式", error.Message);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task PublicCredentialEntrypointCannotBypassIsolation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SystemGitCredentialProvider.GetAsync("https://example.invalid/repo.git"));
    }
}
