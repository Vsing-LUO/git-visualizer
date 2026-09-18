using System.Collections.ObjectModel;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

// Window-scoped state; the coordinator exposes notifications without keeping another copy.
internal sealed class EditorState
{
    internal CancellationTokenSource DraftSaveCancellation { get; set; } = new CancellationTokenSource();
    internal SemaphoreSlim EditorSaveGate { get; set; } = new SemaphoreSlim(1, 1);
    internal SemaphoreSlim DocumentTransitionGate { get; set; } = new SemaphoreSlim(1, 1);
    internal bool CurrentDocumentIsHistorical { get; set; }
    internal string? CurrentHistoricalCommitId { get; set; }
    internal string? CurrentHistoricalRelativePath { get; set; }
    internal string EditorText { get; set; } = string.Empty;
    internal bool IsExternalOnlyDocument { get; set; }
    internal bool CanSaveCurrentDocument { get; set; }
    internal bool HasUnsavedEditorChanges { get; set; }
    internal bool CanOpenCurrentDocumentExternally { get; set; }
    internal string ExternalDocumentHint { get; set; } = "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
    internal TextDocument? CurrentDocument { get; set; }

    internal bool IsModified(string text) => CurrentDocument is not null && CanSaveCurrentDocument &&
        !string.Equals(text, CurrentDocument.Text, StringComparison.Ordinal);

    internal void CancelDraftSave()
    {
        DraftSaveCancellation.Cancel();
        DraftSaveCancellation.Dispose();
        DraftSaveCancellation = new CancellationTokenSource();
    }
}
