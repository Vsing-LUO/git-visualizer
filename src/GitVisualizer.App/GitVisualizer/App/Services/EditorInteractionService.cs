using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;

namespace GitVisualizer.App.Services;

public enum EditorSafetyAction
{
	Save,
	Restore,
	Discard,
	Cancel
}

public interface IEditorInteractionService
{
	Task<EditorSafetyAction> ResolveUnsavedChangesAsync(
		TextDocument document, string reason, CancellationToken cancellationToken = default);
	Task<EditorSafetyAction> ResolveDraftAsync(
		EditorDraft draft, CancellationToken cancellationToken = default);
	Task<EditorSafetyAction> ResolveExternalChangeAsync(
		TextDocument document, CancellationToken cancellationToken = default);
}

public sealed class EditorInteractionService : IEditorInteractionService
{
	public Task<EditorSafetyAction> ResolveUnsavedChangesAsync(
		TextDocument document, string reason, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var window = new EditorSafetyWindow(
			"未保存的编辑内容",
			$"{System.IO.Path.GetFileName(document.Path)} 尚未保存。\n\n继续{reason}前，请选择如何处理这些内容。",
			"保存", EditorSafetyAction.Save,
			"不保存", EditorSafetyAction.Discard)
		{
			Owner = Application.Current?.MainWindow
		};
		window.ShowDialog();
		return Task.FromResult(window.Action);
	}

	public Task<EditorSafetyAction> ResolveDraftAsync(
		EditorDraft draft, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var window = new EditorSafetyWindow(
			"发现可恢复的编辑草稿",
			$"{System.IO.Path.GetFileName(draft.DocumentPath)} 有一份 {draft.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 保存的加密草稿。",
			"恢复草稿", EditorSafetyAction.Restore,
			"放弃草稿", EditorSafetyAction.Discard)
		{
			Owner = Application.Current?.MainWindow
		};
		window.ShowDialog();
		return Task.FromResult(window.Action);
	}

	public Task<EditorSafetyAction> ResolveExternalChangeAsync(
		TextDocument document, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var window = new EditorSafetyWindow(
			"文件已在磁盘上更改",
			$"{System.IO.Path.GetFileName(document.Path)} 已被其他程序或 Git 操作修改。\n\n覆盖将保留编辑器内容；重新载入将采用磁盘内容。",
			"覆盖文件", EditorSafetyAction.Save,
			"重新载入", EditorSafetyAction.Discard)
		{
			Owner = Application.Current?.MainWindow
		};
		window.ShowDialog();
		return Task.FromResult(window.Action);
	}
}

internal sealed class CancelingEditorInteractionService : IEditorInteractionService
{
	public Task<EditorSafetyAction> ResolveUnsavedChangesAsync(
		TextDocument document, string reason, CancellationToken cancellationToken = default) =>
		Task.FromResult(EditorSafetyAction.Cancel);

	public Task<EditorSafetyAction> ResolveDraftAsync(
		EditorDraft draft, CancellationToken cancellationToken = default) =>
		Task.FromResult(EditorSafetyAction.Cancel);

	public Task<EditorSafetyAction> ResolveExternalChangeAsync(
		TextDocument document, CancellationToken cancellationToken = default) =>
		Task.FromResult(EditorSafetyAction.Cancel);
}

internal sealed class NullEditorDraftStore : IEditorDraftStore
{
	public Task<EditorDraft?> LoadAsync(string repositoryPath, string documentPath, CancellationToken cancellationToken = default) =>
		Task.FromResult<EditorDraft?>(null);
	public Task SaveAsync(EditorDraft draft, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task DeleteAsync(string repositoryPath, string documentPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task MoveAsync(string repositoryPath, string oldDocumentPath, string newDocumentPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
