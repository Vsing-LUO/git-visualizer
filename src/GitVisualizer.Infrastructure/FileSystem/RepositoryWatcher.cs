// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.FileSystem;

public sealed class RepositoryWatcherFactory : IRepositoryWatcherFactory
{
    public IRepositoryWatcher Create(string repositoryPath) => new RepositoryWatcher(repositoryPath);
}

public sealed class RepositoryWatcher : IRepositoryWatcher
{
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly System.Timers.Timer debounceTimer;
    private readonly object sync = new();
    private readonly string gitDirectory;
    private readonly string commonDirectory;
    private RepositoryChangeKind pending;
    private DateTime firstEvent;
    private long version;
    public long Version => Interlocked.Read(ref version);
    private bool disposed;
    private bool running;
    private string? fingerprint;

    public RepositoryWatcher(string repositoryPath)
    {
        RepositoryPath = Path.GetFullPath(repositoryPath);
        using var repository = new Repository(RepositoryPath);
        gitDirectory = Path.GetFullPath(repository.Info.Path);
        var commonFile = Path.Combine(gitDirectory, "commondir");
        commonDirectory = File.Exists(commonFile)
            ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim())) : gitDirectory;
        debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        debounceTimer.Elapsed += (_, _) => Flush();
        AddWatcher(RepositoryPath, false);
        AddWatcher(gitDirectory, true);
        if (!Path.TrimEndingDirectorySeparator(commonDirectory).Equals(
                Path.TrimEndingDirectorySeparator(gitDirectory), StringComparison.OrdinalIgnoreCase))
            AddWatcher(commonDirectory, true);
    }

    public event EventHandler? RepositoryChanged;
    public string RepositoryPath { get; }

    private void AddWatcher(string directory, bool metadata)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        void Changed(object sender, FileSystemEventArgs args)
        {
            var relative = Path.GetRelativePath(directory, args.FullPath).Replace('\\', '/');
            if (!metadata && (relative == ".git" || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))) return;
            var kind = metadata ? Classify(relative) : RepositoryChangeKind.Worktree;
            if (args is RenamedEventArgs renamed && metadata)
                kind |= Classify(Path.GetRelativePath(directory, renamed.OldFullPath).Replace('\\', '/'));
            Schedule(kind);
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (sender, args) => Changed(sender, args);
        watcher.Error += (_, _) => Schedule(RepositoryChangeKind.All);
        watchers.Add(watcher);
    }

    internal static RepositoryChangeKind Classify(string relative)
    {
        if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return RepositoryChangeKind.None;
        if (relative.Equals("index", StringComparison.OrdinalIgnoreCase)) return RepositoryChangeKind.Index;
        if (relative.Equals("config", StringComparison.OrdinalIgnoreCase) || relative == "config.worktree")
            return RepositoryChangeKind.Configuration;
        if (relative == "HEAD" || relative == "packed-refs" || relative == "refs" || relative.StartsWith("refs/", StringComparison.Ordinal)
            || relative.EndsWith("_HEAD", StringComparison.Ordinal) || relative.StartsWith("rebase-", StringComparison.Ordinal)
            || relative.StartsWith("sequencer", StringComparison.Ordinal))
            return RepositoryChangeKind.References | RepositoryChangeKind.Index;
        return RepositoryChangeKind.None;
    }

    private void Schedule(RepositoryChangeKind kind)
    {
        if (kind == RepositoryChangeKind.None) return;
        lock (sync)
        {
            if (disposed || !running) return;
            Interlocked.Increment(ref version);
            if (pending == RepositoryChangeKind.None) firstEvent = DateTime.UtcNow;
            pending |= kind;
            // Preserve debounce, but an endless stream must not postpone notification indefinitely.
            debounceTimer.Stop();
            debounceTimer.Interval = Math.Max(1, Math.Min(300, 1000 - (DateTime.UtcNow - firstEvent).TotalMilliseconds));
            debounceTimer.Start();
        }
    }

    private void Flush()
    {
        RepositoryChangeKind changes;
        lock (sync)
        {
            if (disposed || !running) return;
            changes = pending;
            pending = RepositoryChangeKind.None;
        }
        if (changes != RepositoryChangeKind.None) RepositoryChanged?.Invoke(this, new RepositoryChangedEventArgs(changes));
    }

    public void Start()
    {
        lock (sync)
        {
            if (disposed) return;
            running = true;
            foreach (var watcher in watchers) watcher.EnableRaisingEvents = true;
        }
    }

    // Called on a worker when the window regains focus. Reads Git metadata only.
    public void Revalidate()
    {
        string current;
        try
        {
            var entries = new List<string>();
            foreach (var root in new[] { gitDirectory, commonDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var name in new[] { "HEAD", "index", "packed-refs", "config", "config.worktree" })
                {
                    var info = new FileInfo(Path.Combine(root, name));
                    entries.Add(info.Exists ? $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : name);
                }
                var refs = Path.Combine(root, "refs");
                if (Directory.Exists(refs))
                    foreach (var path in Directory.EnumerateFiles(refs, "*", SearchOption.AllDirectories))
                    {
                        var info = new FileInfo(path);
                        entries.Add($"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
                    }
            }
            current = string.Join("|", entries.Order(StringComparer.Ordinal));
        }
        catch (IOException) { Schedule(RepositoryChangeKind.All); return; }
        catch (UnauthorizedAccessException) { Schedule(RepositoryChangeKind.All); return; }
        bool changed;
        lock (sync)
        {
            // A first focus check has no fingerprint tied to the published snapshot.
            // Reconcile once: an external update may have happened after that snapshot
            // but before watchers started. Subsequent unchanged focus checks stay quiet.
            changed = !string.Equals(fingerprint, current, StringComparison.Ordinal);
            fingerprint = current;
        }
        if (changed) Schedule(RepositoryChangeKind.All);
    }

    public void Stop()
    {
        lock (sync)
        {
            if (disposed) return;
            running = false;
            pending = RepositoryChangeKind.None;
            debounceTimer.Stop();
            foreach (var watcher in watchers) watcher.EnableRaisingEvents = false;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var watcher in watchers) watcher.Dispose();
            debounceTimer.Dispose();
        }
    }
}
