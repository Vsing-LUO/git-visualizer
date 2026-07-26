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
