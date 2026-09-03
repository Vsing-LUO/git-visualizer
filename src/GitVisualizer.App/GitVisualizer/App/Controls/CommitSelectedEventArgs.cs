using System;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public sealed class CommitSelectedEventArgs : EventArgs
{
	public CommitNode Commit { get; }

	public CommitSelectedEventArgs(CommitNode commit)
	{
		Commit = commit;
	}
}
