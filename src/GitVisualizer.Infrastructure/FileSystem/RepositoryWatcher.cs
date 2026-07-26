using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.FileSystem;

public sealed class RepositoryWatcherFactory : IRepositoryWatcherFactory
{
    public IRepositoryWatcher Create(string repositoryPath) => new RepositoryWatcher(repositoryPath);
}

public sealed class RepositoryWatcher : IRepositoryWatcher
{
    private readonly FileSystemWatcher watcher;
    private readonly System.Timers.Timer debounceTimer;
    private bool disposed;

    public RepositoryWatcher(string repositoryPath)
    {
        RepositoryPath = Path.GetFullPath(repositoryPath);
        watcher = new FileSystemWatcher(RepositoryPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.Error += (_, _) => Restart();

        debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        debounceTimer.Elapsed += (_, _) => RepositoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? RepositoryChanged;
    public string RepositoryPath { get; }

    public void Start() => watcher.EnableRaisingEvents = true;
    public void Stop() => watcher.EnableRaisingEvents = false;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var relative = Path.GetRelativePath(RepositoryPath, e.FullPath);
        if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    private void Restart()
    {
        if (disposed)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.EnableRaisingEvents = true;
        debounceTimer.Stop();
        debounceTimer.Start();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        watcher.Dispose();
        debounceTimer.Dispose();
    }
}
