using System;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public sealed class BranchSelectedEventArgs : EventArgs
{
	public BranchInfo Branch { get; }

	public BranchSelectedEventArgs(BranchInfo branch)
	{
		Branch = branch;
	}
}
