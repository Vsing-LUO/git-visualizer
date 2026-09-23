// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

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

    [Theory]
    [InlineData("decrypt")]
    [InlineData("parse")]
    [InlineData("identity")]
    public async Task DamagedDraftIsQuarantinedWithOriginalEncryptedBytes(string fault)
    {
        using var directory = new TemporaryDirectory();
        var paths = new LocalDataPaths(Path.Combine(directory.Path, "data"));
        paths.EnsureCreated();
        var repository = Path.Combine(directory.Path, "repo");
        Directory.CreateDirectory(repository);
        var document = Path.Combine(repository, "file.txt");
        var store = new EditorDraftStore(paths);
        var key = EditorDraftStore.DraftKey(repository, document);
        var original = Path.Combine(paths.DraftDirectory, key + ".draft");
        if (fault == "identity")
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            { repositoryPath = repository, documentPath = Path.Combine(repository, "other.txt"), text = "important" });
            File.WriteAllBytes(original, System.Security.Cryptography.ProtectedData.Protect(payload, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        else File.WriteAllBytes(original, fault == "decrypt" ? new byte[] { 1, 2, 3 } :
            System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes("not json"), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var bytes = File.ReadAllBytes(original);
        Assert.Null(await store.LoadAsync(repository, document));
        var quarantine = Assert.Single(Directory.GetFiles(Path.Combine(paths.DraftDirectory, "quarantine")));
        Assert.Equal(bytes, File.ReadAllBytes(quarantine));
        Assert.False(File.Exists(original));
        File.SetLastWriteTimeUtc(quarantine, DateTime.UtcNow.AddDays(-60));
        await store.PruneAsync();
        await store.SaveAsync(new EditorDraft(repository, document, "new draft", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await store.DeleteAsync(repository, document);
        Assert.Equal(bytes, File.ReadAllBytes(quarantine));
    }

    private static string DraftFile(string repositoryPath, string documentPath) =>
        Path.Combine(
            LocalPaths.DraftDirectory,
            EditorDraftStore.DraftKey(repositoryPath, documentPath) + ".draft");
}
