// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace GitVisualizer.Infrastructure.Git;

internal static class RepositoryWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static SemaphoreSlim For(string path) => Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), _ => new SemaphoreSlim(1, 1));
    internal static async Task RunAsync(string path, Func<Task> action, CancellationToken token)
    {
        var gate = For(path);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
