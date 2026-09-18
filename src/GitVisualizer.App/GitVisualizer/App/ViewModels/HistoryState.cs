using System.Collections.ObjectModel;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

// Window-scoped state; the coordinator exposes notifications without keeping another copy.
internal sealed class HistoryState
{
    internal int HistoryLoaded { get; set; }
    internal string SelectedHistoryBranchName { get; set; } = string.Empty;
    internal string HistoryContextText { get; set; } = "全部分支";
    internal bool HasLoadedHistory { get; set; }
    internal bool HasMoreHistory { get; set; }
    internal bool IsCommitGraphCollapsed { get; set; }
    internal CommitNode? SelectedCommit { get; set; }
    internal ObservableCollection<CommitNode> History { get; } = new();
    internal ObservableCollection<GitHistoryEvent> HistoryEvents { get; } = new();

    internal const int PageSize = 200;
    internal static (int VisibleCount, bool HasMore) Page(int fetchedCount) =>
        (Math.Clamp(fetchedCount, 0, PageSize), fetchedCount > PageSize);

    internal int AppendPage(IReadOnlyList<CommitNode> page)
    {
        var state = Page(page.Count);
        foreach (var commit in page.Take(state.VisibleCount)) History.Add(commit);
        HistoryLoaded += state.VisibleCount;
        return state.VisibleCount;
    }
}
