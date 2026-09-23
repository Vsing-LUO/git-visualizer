// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Diagnostics;
using GitVisualizer.Infrastructure.Persistence;
using GitVisualizer.Infrastructure.Recovery;
using GitVisualizer.Infrastructure.Security;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;

namespace GitVisualizer.Tests;

public sealed class BaselineInfrastructureTests
{
    [Fact]
    public void TestProcessAlwaysUsesAnIsolatedProfile()
    {
        Assert.True(LocalPaths.Default.IsIsolated);
        Assert.Contains("session-", LocalPaths.Root);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitVisualizer"), LocalPaths.ProductionRoot);
        Assert.Throws<ArgumentException>(() => new LocalDataPaths("relative"));
    }

    [Fact]
    public async Task NativeCredentialsFailBeforeReadingWritingOrDeletingInTestProfiles()
    {
        var vault = new WindowsCredentialVault();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetAsync("test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync("test", "secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.DeleteAsync("test"));
    }

    [Fact]
    public async Task InjectedStoresKeepSettingsDraftsDatabaseAndPruningSeparate()
    {
        using var temporary = new TemporaryDirectory();
        var first = new LocalDataPaths(Path.Combine(temporary.Path, "first"));
        var second = new LocalDataPaths(Path.Combine(temporary.Path, "second"));
        var settings = AppSettings.Default with { LastRepository = temporary.Path };
        await new SettingsStore(first).SaveAsync(settings);
        Assert.Equal(temporary.Path, (await new SettingsStore(first).LoadAsync()).LastRepository);
        Assert.Equal(AppSettings.Default.LastRepository, (await new SettingsStore(second).LoadAsync()).LastRepository);
        var draft = new EditorDraft(temporary.Path, Path.Combine(temporary.Path, "file.txt"), "private body",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await new EditorDraftStore(first).SaveAsync(draft);
        Assert.Null(await new EditorDraftStore(second).LoadAsync(draft.RepositoryPath, draft.DocumentPath));
        var draftFile = Assert.Single(Directory.GetFiles(first.DraftDirectory));
        File.SetLastWriteTimeUtc(draftFile, DateTime.UtcNow.AddDays(-31));
        await new EditorDraftStore(second).PruneAsync();
        Assert.True(File.Exists(draftFile));
        await new EditorDraftStore(first).PruneAsync();
        Assert.False(File.Exists(draftFile));
        try
        {
            await new OperationLogStore(first).InitializeAsync();
            Assert.True(File.Exists(first.DatabaseFile));
            Assert.False(File.Exists(second.DatabaseFile));
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task RecoveryListingIsScopedToInjectedArchiveDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "repo");
        Repository.Init(repositoryPath);
        var first = new LocalDataPaths(Path.Combine(temporary.Path, "first"));
        var second = new LocalDataPaths(Path.Combine(temporary.Path, "second"));
        var created = await new RecoveryService(first).CreateAsync(repositoryPath, "test");
        Assert.Single(Directory.GetFiles(first.RecoveryDirectory, "*.zip"));
        Assert.Empty(await new RecoveryService(second).ListAsync(repositoryPath));
        Assert.Contains(await new RecoveryService(first).ListAsync(repositoryPath), item => item.Id == created.Id);
    }

    [Fact]
    public void RecorderFlushesConcurrentScopesAndDoesNotAcceptSensitiveLabels()
    {
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "perf.jsonl");
        using (var recorder = new PerformanceRecorder(file))
        {
            Parallel.For(0, 40, _ =>
            {
                using var measurement = recorder.Measure(PerformanceOperation.Refresh);
                var bytes = new byte[8192];
                GC.KeepAlive(bytes);
            });
            recorder.Dispose();
            Assert.Equal(0, recorder.DroppedSamples);
            Assert.Null(recorder.WriteError);
        }
        var samples = File.ReadLines(file).Select(line => JsonSerializer.Deserialize<PerformanceSample>(line)!).ToArray();
        Assert.Equal(40, samples.Length);
        Assert.Equal(40, samples.Select(sample => sample.Sequence).Distinct().Count());
        Assert.All(samples, sample =>
        {
            Assert.True(sample.ElapsedMs >= 0);
            Assert.True(sample.SampledPeakWorkingSetBytes >= sample.WorkingSetStartBytes);
            Assert.Equal(PerformanceOperation.Refresh, sample.Operation);
        });
        Assert.DoesNotContain(temporary.Path, File.ReadAllText(file));
        Assert.Throws<IOException>(() => new PerformanceRecorder(file));
    }

    [Fact]
    public void DisabledMeasurementsDoNotCreateARecorder()
    {
        Assert.Null(PerformanceRecorder.Current);
        Assert.Null(PerformanceRecorder.Begin(PerformanceOperation.GraphRender));
    }
}
