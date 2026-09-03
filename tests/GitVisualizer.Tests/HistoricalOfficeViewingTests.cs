using GitVisualizer.Core;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class HistoricalOfficeViewingTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public async Task HistoricalDocx_ExportsReadOnlyExactVersion_AndHidesOfficeLockFile()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "office-history");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        var firstVersion = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x11, 0x22 };
        var secondVersion = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x33, 0x44 };
        const string documentName = "版本文档.docx";
        const string lockFileName = "~$版本文档.docx";
        var documentPath = Path.Combine(repositoryPath, documentName);

        Assert.True((await git.InitializeAsync(repositoryPath, Identity)).Success);
        await File.WriteAllBytesAsync(documentPath, firstVersion);
        Assert.True((await git.StageFilesAsync(repositoryPath, [documentName])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "first document", Identity)).Success);

        await File.WriteAllBytesAsync(documentPath, secondVersion);
        Assert.True((await git.StageFilesAsync(repositoryPath, [documentName])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "second document", Identity)).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, lockFileName), "Word lock");

        var files = new RecordingFileWorkspaceService();
        using var viewModel = new GitVisualizer.App.ViewModels.MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            files,
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == lockFileName);
        var firstCommit = Assert.Single(
            viewModel.History, commit => commit.Message == "first document");
        await viewModel.SelectCommitAsync(firstCommit);
        var historicalDocument = Assert.Single(
            viewModel.FileTree, item => item.Name == documentName);
        await viewModel.SelectFileAsync(historicalDocument);

        Assert.True(viewModel.IsExternalOnlyDocument);
        Assert.True(viewModel.CanOpenCurrentDocumentExternally);
        Assert.False(viewModel.CanSaveCurrentDocument);
        Assert.True(await viewModel.OpenFileTreeItemExternallyAsync(historicalDocument));
        Assert.NotNull(files.OpenedExternalPath);
        Assert.NotEqual(documentPath, files.OpenedExternalPath);
        Assert.Equal(firstVersion, await File.ReadAllBytesAsync(files.OpenedExternalPath));
        Assert.True(new FileInfo(files.OpenedExternalPath).IsReadOnly);
    }

    private sealed class RecordingFileWorkspaceService : IFileWorkspaceService
    {
        private readonly FileWorkspaceService inner = new();

        public string? OpenedExternalPath { get; private set; }

        public Task<TextDocument> OpenTextAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.OpenTextAsync(path, cancellationToken);

        public Task OpenExternalAsync(
            string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedExternalPath = path;
            return Task.CompletedTask;
        }

        public Task SaveTextAsync(
            string repositoryRoot,
            TextDocument original,
            string text,
            bool allowExternalOverwrite,
            CancellationToken cancellationToken = default) =>
            inner.SaveTextAsync(repositoryRoot, original, text, allowExternalOverwrite, cancellationToken);

        public Task CreateFileAsync(
            string repositoryRoot, string path, CancellationToken cancellationToken = default) =>
            inner.CreateFileAsync(repositoryRoot, path, cancellationToken);

        public Task CreateDirectoryAsync(
            string repositoryRoot, string path, CancellationToken cancellationToken = default) =>
            inner.CreateDirectoryAsync(repositoryRoot, path, cancellationToken);

        public Task MoveAsync(
            string repositoryRoot, string source, string destination, CancellationToken cancellationToken = default) =>
            inner.MoveAsync(repositoryRoot, source, destination, cancellationToken);

        public Task DeleteAsync(
            string repositoryRoot, string path, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(repositoryRoot, path, cancellationToken);
    }
}
