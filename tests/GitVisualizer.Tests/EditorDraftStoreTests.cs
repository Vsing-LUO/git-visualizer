using System.Text;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Persistence;

namespace GitVisualizer.Tests;

public sealed class EditorDraftStoreTests
{
    [Fact]
    public async Task DraftRoundTripIsEncryptedAndIsolatedByRepository()
    {
        using var firstRepository = new TemporaryDirectory();
        using var secondRepository = new TemporaryDirectory();
        var firstPath = Path.Combine(firstRepository.Path, "same-name.txt");
        var secondPath = Path.Combine(secondRepository.Path, "same-name.txt");
        var store = new EditorDraftStore();
        var draft = new EditorDraft(
            firstRepository.Path, firstPath, "仅存在于加密草稿中的秘密文本",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var firstFile = DraftFile(firstRepository.Path, firstPath);
        var secondFile = DraftFile(secondRepository.Path, secondPath);
        try
        {
            await store.SaveAsync(draft);

            Assert.Equal(draft, await store.LoadAsync(firstRepository.Path, firstPath));
            Assert.Null(await store.LoadAsync(secondRepository.Path, secondPath));
            Assert.NotEqual(firstFile, secondFile);
            Assert.DoesNotContain(
                "秘密文本", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(firstFile)));
            Assert.Empty(Directory.EnumerateFiles(LocalPaths.DraftDirectory, "*.tmp"));
        }
        finally
        {
            await store.DeleteAsync(firstRepository.Path, firstPath);
            await store.DeleteAsync(secondRepository.Path, secondPath);
        }
    }

    [Fact]
    public async Task MoveDeleteAndThirtyDayPruneMaintainDraftLifecycle()
    {
        using var repository = new TemporaryDirectory();
        var oldPath = Path.Combine(repository.Path, "old.txt");
        var newPath = Path.Combine(repository.Path, "new.txt");
        var store = new EditorDraftStore();
        var oldFile = DraftFile(repository.Path, oldPath);
        var newFile = DraftFile(repository.Path, newPath);
        try
        {
            await store.SaveAsync(new EditorDraft(
                repository.Path, oldPath, "draft", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await store.MoveAsync(repository.Path, oldPath, newPath);

            Assert.False(File.Exists(oldFile));
            Assert.Equal("draft", (await store.LoadAsync(repository.Path, newPath))?.Text);

            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-31));
            await store.PruneAsync();
            Assert.False(File.Exists(newFile));
        }
        finally
        {
            await store.DeleteAsync(repository.Path, oldPath);
            await store.DeleteAsync(repository.Path, newPath);
        }
    }

    private static string DraftFile(string repositoryPath, string documentPath) =>
        Path.Combine(
            LocalPaths.DraftDirectory,
            EditorDraftStore.DraftKey(repositoryPath, documentPath) + ".draft");
}
