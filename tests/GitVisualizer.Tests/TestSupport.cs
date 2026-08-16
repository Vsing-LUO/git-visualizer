using GitVisualizer.Core;

namespace GitVisualizer.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GitVisualizer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Path, true);
    }
}

internal sealed class MemoryOperationLogStore : IOperationLogStore
{
    public List<OperationLogEntry> Entries { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AddAsync(OperationLogEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(
        string? repositoryPath, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries
            .Where(entry => repositoryPath is null ||
                            entry.RepositoryPath.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase))
            .TakeLast(count)
            .Reverse()
            .ToArray());
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Settings { get; private set; } = AppSettings.Default;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        values[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(values.GetValueOrDefault(key));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        values.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

internal sealed class NoOpRepositoryWatcherFactory : IRepositoryWatcherFactory
{
    public IRepositoryWatcher Create(string repositoryPath) =>
        new NoOpRepositoryWatcher(repositoryPath);

    private sealed class NoOpRepositoryWatcher(string repositoryPath) : IRepositoryWatcher
    {
        public event EventHandler? RepositoryChanged
        {
            add { }
            remove { }
        }

        public string RepositoryPath { get; } = repositoryPath;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
