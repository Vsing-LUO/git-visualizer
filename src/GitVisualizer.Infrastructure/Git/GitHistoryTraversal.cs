// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

using static GitVisualizer.Infrastructure.Git.GitRepositorySupport;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class GitHistoryTraversal : IHistoryTraversal
{
    private readonly object historyGate = new();

    private Repository? historyRepository;

    private IEnumerator<Commit>? historyEnumerator;

    private readonly SortedDictionary<int, CommitNode> historyCache = new();

    private string? historyKey;

    private int historyPosition;

    private bool historyEnded;

    private int historyEpoch;

    private int activeHistoryEpoch;

    private const int HistoryCacheCapacity = 8 * 1000;

    public void ResetHistorySession()
    {
        var epoch = Interlocked.Increment(ref historyEpoch);
        // A long native revwalk must never make a UI-side session switch wait on this lock.
        if (Monitor.TryEnter(historyGate))
        {
            try { ResetHistoryCore(); activeHistoryEpoch = epoch; }
            finally { Monitor.Exit(historyGate); }
        }
        else _ = Task.Run(() =>
        {
            lock (historyGate)
                if (Volatile.Read(ref historyEpoch) == epoch && activeHistoryEpoch != epoch)
                { ResetHistoryCore(); activeHistoryEpoch = epoch; }
        });
    }

    private void ResetHistoryCore()
    {
        historyEnumerator?.Dispose();
        historyEnumerator = null;
        historyRepository?.Dispose();
        historyRepository = null;
        historyCache.Clear();
        historyKey = null;
        historyPosition = 0;
        historyEnded = false;
    }

    public Task<IReadOnlyList<CommitNode>> GetHistoryAsync(string repositoryPath, int skip, int take,
        CancellationToken cancellationToken = default) => ReadHistoryPageAsync(repositoryPath, null, skip, take, cancellationToken);

    public Task<IReadOnlyList<CommitNode>> GetBranchHistoryAsync(string repositoryPath, string branchName,
        int skip, int take, CancellationToken cancellationToken = default) =>
        ReadHistoryPageAsync(repositoryPath, branchName, skip, take, cancellationToken);

    private Task<IReadOnlyList<CommitNode>> ReadHistoryPageAsync(string path, string? branchName,
        int skip, int take, CancellationToken token) => Task.Run<IReadOnlyList<CommitNode>>(() =>
    {
        lock (historyGate)
        {
            token.ThrowIfCancellationRequested();
            skip = Math.Max(0, skip);
            take = Math.Clamp(take, 1, 1000);
            var epoch = Volatile.Read(ref historyEpoch);
            if (activeHistoryEpoch != epoch) { ResetHistoryCore(); activeHistoryEpoch = epoch; }
            var key = Path.GetFullPath(path) + "\n" + branchName;
            if (historyKey != key || skip == 0 || (skip < historyPosition && !historyCache.ContainsKey(skip)))
            {
                ResetHistoryCore();
                try
                {
                    historyRepository = new Repository(path);
                    var roots = branchName is null ? ReadHistoryRoots(historyRepository)
                        : new[] { historyRepository.Branches[branchName]?.Tip
                            ?? throw new ArgumentException("分支不存在。", nameof(branchName)) };
                    historyEnumerator = historyRepository.Commits.QueryBy(new CommitFilter
                    {
                        IncludeReachableFrom = roots,
                        SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                    }).GetEnumerator();
                    historyKey = key;
                }
                catch { ResetHistoryCore(); throw; }
            }
            var result = new List<CommitNode>(take);
            // Cache supports the existing 201-fetch/200-display lookahead without losing a commit.
            while (historyPosition < (long)skip + take && !historyEnded)
            {
                token.ThrowIfCancellationRequested();
                if (!historyEnumerator!.MoveNext()) { historyEnded = true; break; }
                historyCache[historyPosition++] = MapCommit(historyEnumerator.Current);
                if (historyCache.Count > HistoryCacheCapacity) historyCache.Remove(historyCache.First().Key);
            }
            for (int index = skip; index < (long)skip + take; index++)
                if (historyCache.TryGetValue(index, out var node)) result.Add(node);
            return result;
        }
    }, token);
}
