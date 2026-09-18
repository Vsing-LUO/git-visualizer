using System.Collections.ObjectModel;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

// Window-scoped state; the coordinator exposes notifications without keeping another copy.
internal sealed class ConflictState
{
    internal ConflictFile? SelectedConflict { get; set; }
    internal RepositoryOperationState OperationState { get; set; }
    internal bool HasConflicts { get; set; }
    internal bool HasSelectedConflict { get; set; }
    internal bool CanEditSelectedConflict { get; set; }
    internal bool CanContinueOperation { get; set; }
    internal bool CanAbortOperation { get; set; }
    internal string ConflictStatusText { get; set; } = "当前没有进行中的冲突操作。";
    internal string ConflictBaseText { get; set; } = string.Empty;
    internal string ConflictOursText { get; set; } = string.Empty;
    internal string ConflictTheirsText { get; set; } = string.Empty;
    internal string ConflictResultText { get; set; } = string.Empty;
    internal ObservableCollection<ConflictFile> Conflicts { get; } = new();

    internal static bool SupportsContinuation(RepositoryOperationState operation) => operation is
        RepositoryOperationState.Merge or RepositoryOperationState.Rebase or
        RepositoryOperationState.CherryPick or RepositoryOperationState.Revert;

    internal string SideText(ConflictSide side) => side switch
    {
        ConflictSide.Ours => ConflictOursText,
        ConflictSide.Theirs => ConflictTheirsText,
        ConflictSide.Both => ConflictOursText.TrimEnd() + Environment.NewLine + ConflictTheirsText.TrimStart(),
        _ => ConflictResultText
    };
}
