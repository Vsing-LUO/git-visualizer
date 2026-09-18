using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitVisualizer.Infrastructure.Diagnostics;

namespace GitVisualizer.App.ViewModels;

public sealed class FileTreeItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool isExpanded;
    private bool isSelected;
    public bool IsExpanded { get => isExpanded; set => SetProperty(ref isExpanded, value); }
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
	private static readonly HashSet<string> OfficeDocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm",
		".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm",
		".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm",
		".pps", ".ppsx", ".ppsm"
	};

	public required string Name { get; init; }

	public required string FullPath { get; init; }

	public string RelativePath { get; init; } = string.Empty;

	public string? CommitId { get; init; }

	public required bool IsDirectory { get; init; }

	public ObservableCollection<FileTreeItem> Children { get; } = new ObservableCollection<FileTreeItem>();

    public Func<System.Threading.CancellationToken, Task<FileTreeItem[]>>? ChildLoader { get; init; }
    public System.Threading.CancellationToken LifetimeToken { get; init; }
    private bool loaded;
    private Task? loading;
    public bool IsPlaceholder { get; init; }

    internal IDirectoryQuery DirectorySource { get; init; } = DirectoryQuery.Default;

    public static FileTreeItem Create(string path, int depth = 0) => Create(path, DirectoryQuery.Default);

    internal static FileTreeItem Create(string path, IDirectoryQuery query)
    {
        var item = new FileTreeItem { Name = Path.GetFileName(path), FullPath = path,
            RelativePath = path, IsDirectory = Directory.Exists(path), DirectorySource = query };
        if (item.IsDirectory)
            item.Children.Add(new FileTreeItem { Name = "展开以加载…", FullPath = path,
                IsDirectory = false, IsPlaceholder = true });
        return item;
    }

    public Task LoadChildrenAsync(System.Threading.CancellationToken token = default, bool refresh = false)
    {
        if (!IsDirectory || (CommitId != null && ChildLoader is null) || (loaded && !refresh)) return Task.CompletedTask;
        return loading ??= LoadCoreAsync(token);
    }

    private async Task LoadCoreAsync(System.Threading.CancellationToken token)
    {
        // Yield once so loading is assigned even when enumeration immediately fails.
        await Task.Yield();
        try
        {
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token, LifetimeToken);
            if (ChildLoader is null) await UpdateDirectoryAsync(Children, FullPath, DirectorySource, linked.Token);
            else
            {
                FileTreeItem[] children;
                try { children = await ChildLoader(linked.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { children = [new FileTreeItem { Name = "读取失败：" + error.Message, FullPath = FullPath, IsDirectory = false, IsPlaceholder = true }]; }
                await ApplyEntriesAsync(Children, children, linked.Token);
            }
            loaded = true;
        }
        finally { loading = null; }
    }

    public static Task UpdateDirectoryAsync(ObservableCollection<FileTreeItem> destination,
        string directory, System.Threading.CancellationToken token = default) =>
        UpdateDirectoryAsync(destination, directory, DirectoryQuery.Default, token);

    internal static async Task UpdateDirectoryAsync(ObservableCollection<FileTreeItem> destination,
        string directory, IDirectoryQuery query, System.Threading.CancellationToken token = default)
    {
        FileTreeItem[] entries;
        try
        {
            entries = await query.ReadChildrenAsync(directory, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            entries = new[] { new FileTreeItem { Name = "读取失败：" + error.Message,
                FullPath = directory, IsDirectory = false, IsPlaceholder = true } };
        }
        await ApplyEntriesAsync(destination, entries, token);
    }

    public static async Task ApplyEntriesAsync(ObservableCollection<FileTreeItem> destination,
        FileTreeItem[] entries, System.Threading.CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var batch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        var retained = new Dictionary<string, FileTreeItem>(StringComparer.OrdinalIgnoreCase);
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in destination.ToArray())
        {
            if (!item.IsPlaceholder) retained[item.FullPath] = item;
            await YieldIfNeeded(); batch.Start();
        }
        foreach (var item in entries) { desired.Add(item.FullPath); await YieldIfNeeded(); batch.Start(); }
        for (int i = destination.Count - 1; i >= 0; i--)
        {
            if (destination[i].IsPlaceholder || !desired.Contains(destination[i].FullPath)) destination.RemoveAt(i);
            await YieldIfNeeded(); batch.Start();
        }
        for (int i = 0; i < entries.Length; i++)
        {
            var item = entries[i];
            if (!item.IsPlaceholder && retained.TryGetValue(item.FullPath, out var existing)
                && existing.IsDirectory == item.IsDirectory && existing.CommitId == item.CommitId) item = existing;
            if (i < destination.Count && ReferenceEquals(destination[i], item)) { }
            else if (retained.TryGetValue(item.FullPath, out var old) && ReferenceEquals(item, old) && destination.Contains(item)) destination.Move(destination.IndexOf(item), i);
            else destination.Insert(i, item);
            await YieldIfNeeded(); batch.Start();
        }
        while (destination.Count > entries.Length) { destination.RemoveAt(destination.Count - 1); await YieldIfNeeded(); batch.Start(); }
        }
        finally
        {
            PerformanceRecorder.RecordDuration(PerformanceOperation.DirectoryApplyBatch, batch.Elapsed.TotalMilliseconds);
        }
        // Child enumeration awaits are not time spent applying this directory's UI batch.
        foreach (var child in destination.ToArray())
            if (child.loaded && child.CommitId is null) await child.LoadChildrenAsync(token, refresh: true);

        async Task YieldIfNeeded()
        {
            token.ThrowIfCancellationRequested();
            // Reserve half of the 8 ms UI budget for the final collection notification,
            // allocation/GC and dispatcher overhead; checking only at 8 ms overshoots.
            if (batch.Elapsed.TotalMilliseconds < 4) return;
            batch.Stop();
            PerformanceRecorder.RecordDuration(PerformanceOperation.DirectoryApplyBatch, batch.Elapsed.TotalMilliseconds);
            // Resume timing in the caller, after its await resumes too. Restarting
            // here counts the caller's queued continuation as active UI work.
            batch.Reset();
            if (System.Threading.SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext)
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            else await Task.Yield();
            token.ThrowIfCancellationRequested();
        }
    }

	public static bool IsTransientOfficeLockFile(string path)
	{
		if (Directory.Exists(path))
		{
			return false;
		}
		string fileName = Path.GetFileName(path);
		return fileName.StartsWith("~$", StringComparison.Ordinal) && OfficeDocumentExtensions.Contains(Path.GetExtension(fileName));
	}
}
