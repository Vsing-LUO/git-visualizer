using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.FileSystem;

// Caller must hold RepositoryWriteLock. Both ordinary saves and conflict resolution
// use this contract, including the extra index validation before atomic replacement.
internal interface ISafeFileWriter
{
    Task SaveUnderLockAsync(string repositoryRoot, TextDocument original, string text,
        CancellationToken cancellationToken = default, Action? validateAdditionalState = null);
}

internal sealed class SafeFileWriter : ISafeFileWriter
{
    public Task SaveUnderLockAsync(string repositoryRoot, TextDocument original, string text,
        CancellationToken cancellationToken = default, Action? validateAdditionalState = null) =>
        TextFileStorage.SaveCoreAsync(repositoryRoot, original, text, new TextFileOperations(),
            cancellationToken, validateAdditionalState);
}
