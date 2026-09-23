// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.Core;
namespace GitVisualizer.Infrastructure.Git;

// A session owns the traversal and bounded page cache; callers can replace it independently.
internal interface IHistoryTraversal : IHistorySessionService
{
    Task<IReadOnlyList<CommitNode>> GetHistoryAsync(string repositoryPath, int skip, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommitNode>> GetBranchHistoryAsync(string repositoryPath, string branchName, int skip, int take, CancellationToken cancellationToken = default);
}
