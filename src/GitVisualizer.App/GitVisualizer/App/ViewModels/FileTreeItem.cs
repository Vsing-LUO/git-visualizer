using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace GitVisualizer.App.ViewModels;

public sealed class FileTreeItem
{
	private static readonly HashSet<string> OfficeDocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm",
		".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm",
		".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm",
		".pps", ".ppsx", ".ppsm"
	};

	public required string Name { get; init; }

	public required string FullPath { get; init; }

	public string RelativePath { get; init; } = string.Empty;

	public string? CommitId { get; init; }

	public required bool IsDirectory { get; init; }

	public ObservableCollection<FileTreeItem> Children { get; } = new ObservableCollection<FileTreeItem>();

	public static FileTreeItem Create(string path, int depth)
	{
		FileTreeItem fileTreeItem = new FileTreeItem
		{
			Name = Path.GetFileName(path),
			FullPath = path,
			RelativePath = path,
			IsDirectory = Directory.Exists(path)
		};
		if (fileTreeItem.IsDirectory && depth > 0)
		{
			try
			{
				foreach (string item in Directory.EnumerateFileSystemEntries(path).Where((string child) => !IsTransientOfficeLockFile(child)).OrderByDescending(Directory.Exists).ThenBy<string, string>(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
					.Take(500))
				{
					fileTreeItem.Children.Add(Create(item, depth - 1));
				}
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		return fileTreeItem;
	}

	public static bool IsTransientOfficeLockFile(string path)
	{
		if (Directory.Exists(path))
		{
			return false;
		}
		string fileName = Path.GetFileName(path);
		return fileName.StartsWith("~$", StringComparison.Ordinal) && OfficeDocumentExtensions.Contains(Path.GetExtension(fileName));
	}
}
