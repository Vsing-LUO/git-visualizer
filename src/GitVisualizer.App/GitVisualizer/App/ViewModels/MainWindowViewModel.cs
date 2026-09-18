using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using GitVisualizer.App.Services;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Diagnostics;

namespace GitVisualizer.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
	private sealed record RepositoryMetadata(DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, long Size)
	{
		public static RepositoryMetadata Empty { get; } = new RepositoryMetadata(DateTime.MinValue, DateTime.MinValue, 0L);
	}

	private const int DiffTabIndex = 0;

	private const int EditorTabIndex = 1;

	private const int DetailsTabIndex = 2;

	private const int ConflictTabIndex = 3;

	internal const int HistoryPageSize = HistoryState.PageSize;

	private static readonly HashSet<string> ExternalDocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".rtf", ".odt", ".ods",
		".odp", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
	};

	private readonly EditorState editorState = new();

	private readonly HistoryState historyState = new();

	private readonly FileTreeState fileTreeState = new();

	private readonly ConflictState conflictState = new();

	private readonly IGitRepositoryService git;

	private readonly IDiffService diff;

	private readonly IIndexPatchService? indexPatch;

	private readonly IRepositoryWatcherFactory watcherFactory;

	private readonly IFileWorkspaceService files;

	private readonly ISystemNewFileService systemNewFiles;

	private readonly ISettingsStore settingsStore;

	private readonly IOperationLogStore logStore;

	private readonly IRecoveryService recoveryService;

	private readonly ICredentialVault credentialVault;

	private readonly IEditorDraftStore draftStore;

	private readonly IEditorInteractionService editorInteraction;

	private IRepositoryWatcher? watcher;

	private readonly IRepositorySession requests = new RepositoryRequests();
	private bool requestsDisposed;

	private CancellationTokenSource refreshCancellation = new CancellationTokenSource();


	private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);

	private AppSettings settings = AppSettings.Default;


	private int repositorySortVersion;

	private int nextRepositoryOrder;


	private readonly Dictionary<string, int> repositoryInsertionOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	private string activeRepositoryPath = string.Empty;
	private string? selectedRepository;
	private string repositorySortMode = "修改时间";
	private string currentBranch = "未打开仓库";
	private HeadInfo? head;
	private BranchInfo? selectedBranch;
	private RemoteInfo? selectedRemote;


	private string statusText = "拖入文件夹，或点击“打开仓库”开始";
	private string commitMessage = string.Empty;
	private string diffText = string.Empty;
	private string diffContextText = "工作区差异";
	private string diffSummaryText = "请选择一个有变化的文件。";
	private string diffRawText = string.Empty;
	private string rawDiffToggleText = "查看原始差异";
	private DiffFilePresentation? selectedDiffFile;
	private bool showRawDiff;
	private bool canShowRawDiff;
	private bool showWorkingDiffCards;
	private bool showCommitDiffCards;
	private bool showDiffEmptyState = true;


	private string detailsText = string.Empty;
	private string equivalentCommand = string.Empty;
	private int selectedRightTabIndex;
	private bool isBusy;
	private bool isCloning;
	private string cloneDestinationPath = string.Empty;
	private bool isPulling;
	private string pullSourceText = "正在连接上游远程仓库";
	private bool hasRepository;


	private FileChange? selectedChange;


	private OperationLogEntry? selectedOperationLog;


	private bool hasDiffHunks;


	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? commitCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? amendCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<FileChange?>? stageCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<FileChange?>? unstageCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? stageAllCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? unstageAllCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveEditorCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveAndStageEditorCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? openCurrentDocumentExternallyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? fetchCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? pushCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? loadMoreHistoryCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? showWorkingTreeCommand;

	public ObservableCollection<string> RecentRepositories { get; } = new ObservableCollection<string>();

	public ObservableCollection<BranchInfo> Branches { get; } = new ObservableCollection<BranchInfo>();

	public ObservableCollection<TagInfo> Tags { get; } = new ObservableCollection<TagInfo>();

	public ObservableCollection<GitHistoryEvent> HistoryEvents => historyState.HistoryEvents;

	public ObservableCollection<RemoteInfo> Remotes { get; } = new ObservableCollection<RemoteInfo>();

	public ObservableCollection<FileChange> UnstagedChanges { get; } = new ObservableCollection<FileChange>();

	public ObservableCollection<FileChange> StagedChanges { get; } = new ObservableCollection<FileChange>();

	public ObservableCollection<CommitNode> History => historyState.History;

	public ObservableCollection<FileTreeItem> FileTree => fileTreeState.FileTree;

	public ObservableCollection<OperationLogEntry> OperationLog { get; } = new ObservableCollection<OperationLogEntry>();

	public ObservableCollection<ConflictFile> Conflicts => conflictState.Conflicts;

	public ObservableCollection<DiffHunk> DiffHunks { get; } = new ObservableCollection<DiffHunk>();

	public ObservableCollection<DiffFilePresentation> DiffFiles { get; } = new ObservableCollection<DiffFilePresentation>();

	public ObservableCollection<DiffRegionPresentation> DiffRegions { get; } = new ObservableCollection<DiffRegionPresentation>();

	public ObservableCollection<string> Notices { get; } = new ObservableCollection<string>();

	public IReadOnlyList<string> RepositorySortModes { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "创建时间", "修改时间", "文件大小" });

	public bool IsHistoryComplete
	{
		get
		{
			if (HasLoadedHistory && History.Count > 0)
			{
				return !HasMoreHistory;
			}
			return false;
		}
	}

	public PullStrategy SavedPullStrategy
	{
		get
		{
			if (!settings.PullStrategies.TryGetValue(ActiveRepositoryPath, out var value))
			{
				return PullStrategy.Ask;
			}
			return value;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ActiveRepositoryPath
	{
		get
		{
			return activeRepositoryPath;
		}
		[MemberNotNull("activeRepositoryPath")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(activeRepositoryPath, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ActiveRepositoryPath);
				activeRepositoryPath = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ActiveRepositoryPath);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? SelectedRepository
	{
		get
		{
			return selectedRepository;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(selectedRepository, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedRepository);
				selectedRepository = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedRepository);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string RepositorySortMode
	{
		get
		{
			return repositorySortMode;
		}
		[MemberNotNull("repositorySortMode")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(repositorySortMode, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.RepositorySortMode);
				repositorySortMode = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.RepositorySortMode);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string CurrentBranch
	{
		get
		{
			return currentBranch;
		}
		[MemberNotNull("currentBranch")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(currentBranch, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CurrentBranch);
				currentBranch = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CurrentBranch);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public HeadInfo? Head
	{
		get
		{
			return head;
		}
		set
		{
			if (!EqualityComparer<HeadInfo>.Default.Equals(head, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Head);
				head = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Head);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public BranchInfo? SelectedBranch
	{
		get
		{
			return selectedBranch;
		}
		set
		{
			if (!EqualityComparer<BranchInfo>.Default.Equals(selectedBranch, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedBranch);
				selectedBranch = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedBranch);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public RemoteInfo? SelectedRemote
	{
		get
		{
			return selectedRemote;
		}
		set
		{
			if (!EqualityComparer<RemoteInfo>.Default.Equals(selectedRemote, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedRemote);
				selectedRemote = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedRemote);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedHistoryBranchName
	{
		get
		{
			return historyState.SelectedHistoryBranchName;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(historyState.SelectedHistoryBranchName, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedHistoryBranchName);
				historyState.SelectedHistoryBranchName = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedHistoryBranchName);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string HistoryContextText
	{
		get
		{
			return historyState.HistoryContextText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(historyState.HistoryContextText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HistoryContextText);
				historyState.HistoryContextText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HistoryContextText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasLoadedHistory
	{
		get
		{
			return historyState.HasLoadedHistory;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(historyState.HasLoadedHistory, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasLoadedHistory);
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHistoryComplete);
				historyState.HasLoadedHistory = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasLoadedHistory);
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHistoryComplete);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasMoreHistory
	{
		get
		{
			return historyState.HasMoreHistory;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(historyState.HasMoreHistory, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasMoreHistory);
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHistoryComplete);
				historyState.HasMoreHistory = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasMoreHistory);
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsHistoryComplete);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsCommitGraphCollapsed
	{
		get
		{
			return historyState.IsCommitGraphCollapsed;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(historyState.IsCommitGraphCollapsed, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsCommitGraphCollapsed);
				historyState.IsCommitGraphCollapsed = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsCommitGraphCollapsed);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StatusText
	{
		get
		{
			return statusText;
		}
		[MemberNotNull("statusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(statusText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StatusText);
				statusText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string CommitMessage
	{
		get
		{
			return commitMessage;
		}
		[MemberNotNull("commitMessage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(commitMessage, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CommitMessage);
				commitMessage = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CommitMessage);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiffText
	{
		get
		{
			return diffText;
		}
		[MemberNotNull("diffText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(diffText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiffText);
				diffText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiffText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiffContextText
	{
		get
		{
			return diffContextText;
		}
		[MemberNotNull("diffContextText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(diffContextText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiffContextText);
				diffContextText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiffContextText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiffSummaryText
	{
		get
		{
			return diffSummaryText;
		}
		[MemberNotNull("diffSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(diffSummaryText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiffSummaryText);
				diffSummaryText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiffSummaryText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiffRawText
	{
		get
		{
			return diffRawText;
		}
		[MemberNotNull("diffRawText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(diffRawText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiffRawText);
				diffRawText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiffRawText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string RawDiffToggleText
	{
		get
		{
			return rawDiffToggleText;
		}
		[MemberNotNull("rawDiffToggleText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(rawDiffToggleText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.RawDiffToggleText);
				rawDiffToggleText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.RawDiffToggleText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public DiffFilePresentation? SelectedDiffFile
	{
		get
		{
			return selectedDiffFile;
		}
		set
		{
			if (!EqualityComparer<DiffFilePresentation>.Default.Equals(selectedDiffFile, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedDiffFile);
				selectedDiffFile = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedDiffFile);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowRawDiff
	{
		get
		{
			return showRawDiff;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(showRawDiff, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowRawDiff);
				showRawDiff = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowRawDiff);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanShowRawDiff
	{
		get
		{
			return canShowRawDiff;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canShowRawDiff, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanShowRawDiff);
				canShowRawDiff = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanShowRawDiff);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowWorkingDiffCards
	{
		get
		{
			return showWorkingDiffCards;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(showWorkingDiffCards, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowWorkingDiffCards);
				showWorkingDiffCards = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowWorkingDiffCards);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowCommitDiffCards
	{
		get
		{
			return showCommitDiffCards;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(showCommitDiffCards, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowCommitDiffCards);
				showCommitDiffCards = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowCommitDiffCards);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool ShowDiffEmptyState
	{
		get
		{
			return showDiffEmptyState;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(showDiffEmptyState, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ShowDiffEmptyState);
				showDiffEmptyState = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ShowDiffEmptyState);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string EditorText
	{
		get
		{
			return editorState.EditorText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(editorState.EditorText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EditorText);
				editorState.EditorText = value;
				OnEditorTextChanged(value);
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.EditorText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DetailsText
	{
		get
		{
			return detailsText;
		}
		[MemberNotNull("detailsText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(detailsText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DetailsText);
				detailsText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DetailsText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string EquivalentCommand
	{
		get
		{
			return equivalentCommand;
		}
		[MemberNotNull("equivalentCommand")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(equivalentCommand, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EquivalentCommand);
				equivalentCommand = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.EquivalentCommand);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int SelectedRightTabIndex
	{
		get
		{
			return selectedRightTabIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(selectedRightTabIndex, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedRightTabIndex);
				selectedRightTabIndex = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedRightTabIndex);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsBusy
	{
		get
		{
			return isBusy;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isBusy, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsBusy);
				isBusy = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsBusy);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsCloning
	{
		get
		{
			return isCloning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isCloning, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsCloning);
				isCloning = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsCloning);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string CloneDestinationPath
	{
		get
		{
			return cloneDestinationPath;
		}
		[MemberNotNull("cloneDestinationPath")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(cloneDestinationPath, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CloneDestinationPath);
				cloneDestinationPath = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CloneDestinationPath);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsPulling
	{
		get
		{
			return isPulling;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isPulling, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsPulling);
				isPulling = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsPulling);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string PullSourceText
	{
		get
		{
			return pullSourceText;
		}
		[MemberNotNull("pullSourceText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(pullSourceText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.PullSourceText);
				pullSourceText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.PullSourceText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasRepository
	{
		get
		{
			return hasRepository;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasRepository, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasRepository);
				hasRepository = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasRepository);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsExternalOnlyDocument
	{
		get
		{
			return editorState.IsExternalOnlyDocument;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(editorState.IsExternalOnlyDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsExternalOnlyDocument);
				editorState.IsExternalOnlyDocument = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsExternalOnlyDocument);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanSaveCurrentDocument
	{
		get
		{
			return editorState.CanSaveCurrentDocument;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(editorState.CanSaveCurrentDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanSaveCurrentDocument);
				editorState.CanSaveCurrentDocument = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanSaveCurrentDocument);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasUnsavedEditorChanges
	{
		get
		{
			return editorState.HasUnsavedEditorChanges;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(editorState.HasUnsavedEditorChanges, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasUnsavedEditorChanges);
				editorState.HasUnsavedEditorChanges = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasUnsavedEditorChanges);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanOpenCurrentDocumentExternally
	{
		get
		{
			return editorState.CanOpenCurrentDocumentExternally;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(editorState.CanOpenCurrentDocumentExternally, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanOpenCurrentDocumentExternally);
				editorState.CanOpenCurrentDocumentExternally = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanOpenCurrentDocumentExternally);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsBrowsingHistoricalCommit
	{
		get
		{
			return fileTreeState.IsBrowsingHistoricalCommit;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(fileTreeState.IsBrowsingHistoricalCommit, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsBrowsingHistoricalCommit);
				fileTreeState.IsBrowsingHistoricalCommit = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsBrowsingHistoricalCommit);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanModifyFileTree
	{
		get
		{
			return fileTreeState.CanModifyFileTree;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(fileTreeState.CanModifyFileTree, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanModifyFileTree);
				fileTreeState.CanModifyFileTree = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanModifyFileTree);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string FileTreeContextText
	{
		get
		{
			return fileTreeState.FileTreeContextText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(fileTreeState.FileTreeContextText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FileTreeContextText);
				fileTreeState.FileTreeContextText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.FileTreeContextText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ExternalDocumentHint
	{
		get
		{
			return editorState.ExternalDocumentHint;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(editorState.ExternalDocumentHint, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ExternalDocumentHint);
				editorState.ExternalDocumentHint = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ExternalDocumentHint);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public TextDocument? CurrentDocument
	{
		get
		{
			return editorState.CurrentDocument;
		}
		set
		{
			if (!EqualityComparer<TextDocument>.Default.Equals(editorState.CurrentDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CurrentDocument);
				editorState.CurrentDocument = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CurrentDocument);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public FileChange? SelectedChange
	{
		get
		{
			return selectedChange;
		}
		set
		{
			if (!EqualityComparer<FileChange>.Default.Equals(selectedChange, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedChange);
				selectedChange = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedChange);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public CommitNode? SelectedCommit
	{
		get
		{
			return historyState.SelectedCommit;
		}
		set
		{
			if (!EqualityComparer<CommitNode>.Default.Equals(historyState.SelectedCommit, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedCommit);
				historyState.SelectedCommit = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedCommit);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public OperationLogEntry? SelectedOperationLog
	{
		get
		{
			return selectedOperationLog;
		}
		set
		{
			if (!EqualityComparer<OperationLogEntry>.Default.Equals(selectedOperationLog, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedOperationLog);
				selectedOperationLog = value;
				OnSelectedOperationLogChanged(value);
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedOperationLog);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ConflictFile? SelectedConflict
	{
		get
		{
			return conflictState.SelectedConflict;
		}
		set
		{
			if (!EqualityComparer<ConflictFile>.Default.Equals(conflictState.SelectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedConflict);
				conflictState.SelectedConflict = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedConflict);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public RepositoryOperationState OperationState
	{
		get
		{
			return conflictState.OperationState;
		}
		set
		{
			if (!EqualityComparer<RepositoryOperationState>.Default.Equals(conflictState.OperationState, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.OperationState);
				conflictState.OperationState = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.OperationState);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasConflicts
	{
		get
		{
			return conflictState.HasConflicts;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(conflictState.HasConflicts, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasConflicts);
				conflictState.HasConflicts = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasConflicts);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasSelectedConflict
	{
		get
		{
			return conflictState.HasSelectedConflict;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(conflictState.HasSelectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasSelectedConflict);
				conflictState.HasSelectedConflict = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasSelectedConflict);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanEditSelectedConflict
	{
		get
		{
			return conflictState.CanEditSelectedConflict;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(conflictState.CanEditSelectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanEditSelectedConflict);
				conflictState.CanEditSelectedConflict = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanEditSelectedConflict);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasDiffHunks
	{
		get
		{
			return hasDiffHunks;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasDiffHunks, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasDiffHunks);
				hasDiffHunks = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.HasDiffHunks);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanContinueOperation
	{
		get
		{
			return conflictState.CanContinueOperation;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(conflictState.CanContinueOperation, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanContinueOperation);
				conflictState.CanContinueOperation = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanContinueOperation);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool CanAbortOperation
	{
		get
		{
			return conflictState.CanAbortOperation;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(conflictState.CanAbortOperation, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanAbortOperation);
				conflictState.CanAbortOperation = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.CanAbortOperation);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConflictStatusText
	{
		get
		{
			return conflictState.ConflictStatusText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictState.ConflictStatusText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictStatusText);
				conflictState.ConflictStatusText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConflictStatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConflictBaseText
	{
		get
		{
			return conflictState.ConflictBaseText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictState.ConflictBaseText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictBaseText);
				conflictState.ConflictBaseText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConflictBaseText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConflictOursText
	{
		get
		{
			return conflictState.ConflictOursText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictState.ConflictOursText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictOursText);
				conflictState.ConflictOursText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConflictOursText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConflictTheirsText
	{
		get
		{
			return conflictState.ConflictTheirsText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictState.ConflictTheirsText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictTheirsText);
				conflictState.ConflictTheirsText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConflictTheirsText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConflictResultText
	{
		get
		{
			return conflictState.ConflictResultText;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictState.ConflictResultText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictResultText);
				conflictState.ConflictResultText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConflictResultText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshCommand => refreshCommand ?? (refreshCommand = new AsyncRelayCommand(RefreshAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand CommitCommand => commitCommand ?? (commitCommand = new AsyncRelayCommand(CommitAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand AmendCommand => amendCommand ?? (amendCommand = new AsyncRelayCommand(AmendAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<FileChange?> StageCommand => stageCommand ?? (stageCommand = new AsyncRelayCommand<FileChange?>(StageAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<FileChange?> UnstageCommand => unstageCommand ?? (unstageCommand = new AsyncRelayCommand<FileChange?>(UnstageAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand StageAllCommand => stageAllCommand ?? (stageAllCommand = new AsyncRelayCommand(StageAllAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand UnstageAllCommand => unstageAllCommand ?? (unstageAllCommand = new AsyncRelayCommand(UnstageAllAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveEditorCommand => saveEditorCommand ?? (saveEditorCommand = new AsyncRelayCommand(SaveEditorAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveAndStageEditorCommand => saveAndStageEditorCommand ?? (saveAndStageEditorCommand = new AsyncRelayCommand(SaveAndStageEditorAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand OpenCurrentDocumentExternallyCommand => openCurrentDocumentExternallyCommand ?? (openCurrentDocumentExternallyCommand = new AsyncRelayCommand(OpenCurrentDocumentExternallyAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand FetchCommand => fetchCommand ?? (fetchCommand = new AsyncRelayCommand(FetchAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PushCommand => pushCommand ?? (pushCommand = new AsyncRelayCommand(PushAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand LoadMoreHistoryCommand => loadMoreHistoryCommand ?? (loadMoreHistoryCommand = new AsyncRelayCommand(LoadMoreHistoryAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ShowWorkingTreeCommand => showWorkingTreeCommand ?? (showWorkingTreeCommand = new AsyncRelayCommand(ShowWorkingTreeAsync));

	public event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;

	public MainWindowViewModel(IGitRepositoryService git, IDiffService diff, IRepositoryWatcherFactory watcherFactory, IFileWorkspaceService files, ISystemNewFileService systemNewFiles, ISettingsStore settingsStore, IOperationLogStore logStore, IRecoveryService recoveryService, ICredentialVault credentialVault, IIndexPatchService? indexPatch = null, IEditorDraftStore? draftStore = null, IEditorInteractionService? editorInteraction = null)
	{
		this.git = git;
		this.diff = diff;
		this.watcherFactory = watcherFactory;
		this.files = files;
		this.systemNewFiles = systemNewFiles;
		this.settingsStore = settingsStore;
		this.logStore = logStore;
		this.recoveryService = recoveryService;
		this.credentialVault = credentialVault;
		this.indexPatch = indexPatch;
		this.draftStore = draftStore ?? new NullEditorDraftStore();
		this.editorInteraction = editorInteraction ?? new CancelingEditorInteractionService();
	}

	public async Task InitializeAsync()
	{
		await draftStore.PruneAsync();
		settings = await settingsStore.LoadAsync();
		foreach (string item in settings.RecentRepositories.Where(Directory.Exists))
		{
			RecentRepositories.Add(item);
			repositoryInsertionOrder[item] = nextRepositoryOrder++;
		}
		await SortRepositoriesAsync(RepositorySortMode);
		string last = settings.LastRepository;
		bool flag = last != null && Directory.Exists(last);
		if (flag)
		{
			flag = await git.IsRepositoryAsync(last!);
		}
		if (flag)
		{
			await OpenRepositoryAsync(last!);
		}
	}

	public Task<bool> IsRepositoryAsync(string path)
	{
		return git.IsRepositoryAsync(path);
	}

	public async Task SortRepositoriesAsync(string mode)
	{
		using var sortContext = requests.Capture();
		if (!RepositorySortModes.Contains<string>(mode, StringComparer.Ordinal))
		{
			return;
		}
		RepositorySortMode = mode;
		int version = ++repositorySortVersion;
		string[] paths = RecentRepositories.ToArray();
		Dictionary<string, RepositoryMetadata> metadata = await Task.Run(() => paths.ToDictionary<string, string, RepositoryMetadata>((string path) => path, (string path) => ReadRepositoryMetadata(path, mode == "文件大小"), StringComparer.OrdinalIgnoreCase));
		if (!sortContext.IsCurrent || version != repositorySortVersion)
		{
			return;
		}
		string[] array = (mode switch
		{
			"创建时间" => paths.OrderByDescending((string path) => metadata[path].CreationTimeUtc), 
			"修改时间" => paths.OrderByDescending((string path) => metadata[path].LastWriteTimeUtc), 
			"文件大小" => paths.OrderByDescending((string path) => metadata[path].Size), 
			_ => paths.OrderBy((string path) => repositoryInsertionOrder.GetValueOrDefault(path, int.MaxValue)), 
		}).ThenBy((string path) => repositoryInsertionOrder.GetValueOrDefault(path, int.MaxValue)).ToArray();
		for (int num = 0; num < array.Length; num++)
		{
			int num2 = RecentRepositories.IndexOf(array[num]);
			if (num2 >= 0 && num2 != num)
			{
				RecentRepositories.Move(num2, num);
			}
		}
		SelectedRepository = RecentRepositories.FirstOrDefault((string path) => path.Equals(ActiveRepositoryPath, StringComparison.OrdinalIgnoreCase));
		StatusText = "仓库已按" + mode + "排序。";
	}

	public async Task<bool> OpenRepositoryAsync(string path)
	{
		if (requestsDisposed) return false;
		string normalizedPath = Path.GetFullPath(path);
		using var opening = requests.Begin("open");
		try
		{
			if (HasRepository && normalizedPath.Equals(ActiveRepositoryPath, StringComparison.OrdinalIgnoreCase))
			{
				await opening.Await(RefreshAsync());
				return true;
			}
			if (!await opening.Await(PrepareForDocumentTransitionAsync("切换仓库"))) return false;
			ResetRepositoryView(normalizedPath);
			using var session = requests.Capture();
			using var request = requests.Begin("snapshot");
			IsBusy = true;
			try
			{
				var snapshot = await request.Await(git.GetSnapshotAsync(normalizedPath, request.Token));
				HasRepository = true;
				await ApplySnapshotAsync(snapshot, request);
				request.Check();
				await request.Await(RememberRepositoryAsync(normalizedPath));
				AttachWatcher(normalizedPath);
				await request.Await(recoveryService.PruneRepositoryReferencesAsync(normalizedPath, request.Token));
				return true;
			}
			catch (OperationCanceledException) { return false; }
			catch (Exception error) { if (session.IsCurrent) StatusText = "打开仓库失败：" + error.Message; return false; }
			finally { if (session.IsCurrent) IsBusy = false; }
		}
		catch (OperationCanceledException) { return false; }
	}

	public async Task<bool> RemoveRecentRepositoryAsync(string path)
	{
		string normalizedPath = Path.GetFullPath(path);
		string existing = RecentRepositories.FirstOrDefault((string item) => Path.GetFullPath(item).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
		if (existing == null)
		{
			return false;
		}
		int removedIndex = RecentRepositories.IndexOf(existing);
		bool removesActiveRepository = HasRepository && Path.GetFullPath(ActiveRepositoryPath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase);
		if (removesActiveRepository && !await PrepareForDocumentTransitionAsync("移除当前仓库"))
		{
			return false;
		}
		string? nextRepository = removesActiveRepository
			? SelectRepositoryAfterRemoval(RecentRepositories, removedIndex)
			: null;
		RecentRepositories.Remove(existing);
		repositoryInsertionOrder.Remove(existing);
		if (SelectedRepository != null && Path.GetFullPath(SelectedRepository).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
		{
			SelectedRepository = null;
		}
		settings = settings with
		{
			RecentRepositories = RecentRepositories.OrderBy((string item) => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue)).ToArray(),
			LastRepository = removesActiveRepository
				? nextRepository
				: ((settings.LastRepository != null && Path.GetFullPath(settings.LastRepository).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
					? (HasRepository ? ActiveRepositoryPath : null)
					: settings.LastRepository)
		};
		await settingsStore.SaveAsync(settings);
		if (!removesActiveRepository)
		{
			SelectedRepository = HasRepository ? ActiveRepositoryPath : null;
			StatusText = "已从仓库列表移除 " + existing + "；磁盘文件和 Git 数据未删除";
			return true;
		}

		if (nextRepository == null)
		{
			ResetToEmptyRepositoryView();
			return true;
		}

		if (await OpenRepositoryAsync(nextRepository))
		{
			StatusText = "已从仓库列表移除 " + existing + "；已切换到 " + nextRepository;
		}
		else
		{
			ResetToEmptyRepositoryView();
			settings = settings with { LastRepository = null };
			await settingsStore.SaveAsync(settings);
			StatusText = "已移除仓库，但无法自动打开上一仓库 " + nextRepository;
		}
		return true;
	}

	internal static string? SelectRepositoryAfterRemoval(IReadOnlyList<string> repositories, int removedIndex)
	{
		if (repositories.Count <= 1 || removedIndex < 0 || removedIndex >= repositories.Count)
		{
			return null;
		}
		return removedIndex > 0 ? repositories[removedIndex - 1] : repositories[1];
	}

	public Task<GitIdentity?> GetDefaultIdentityAsync() => git.GetDefaultIdentityAsync();

	public Task<GitIdentity?> GetCurrentIdentityAsync() =>
		HasRepository ? git.GetIdentityAsync(ActiveRepositoryPath) : Task.FromResult<GitIdentity?>(null);

	public async Task<GitOperationResult> ConfigureGlobalIdentityAsync(GitIdentity identity)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ConfigureGlobalIdentityAsync", string.Empty);
		GitOperationResult result = await git.SetGlobalIdentityAsync(identity, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		return result;
	}

	public async Task<GitOperationResult> InitializeRepositoryAsync(string path, GitIdentity? identity)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("InitializeRepositoryAsync", string.Empty);
		GitOperationResult result = await git.InitializeAsync(path, identity, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		if (result.Success)
		{
			await OpenRepositoryAsync(path);
		}
		return result;
	}

	public async Task<GitOperationResult> CloneRepositoryAsync(string url, string path, RemoteCredential? credential)
	{
		using var operationContext = requests.Capture();
		string normalizedPath = Path.GetFullPath(path);
		GitOperationResult result = null;
		CloneDestinationPath = normalizedPath;
		IsCloning = true;
		try
		{
			await RunBusyAsync(async delegate(CancellationToken token)
			{
				result = await git.CloneAsync(url, normalizedPath, credential, token);
				ShowResult(result);
			});
			if (!operationContext.IsCurrent) return result ?? GitOperationResult.Canceled("clone", "git clone");
			if (result?.Success ?? false)
			{
				await OpenRepositoryAsync(normalizedPath);
			}
			return result ?? GitOperationResult.Fail("clone", "git clone", new InvalidOperationException("当前有其他操作正在进行，未能开始克隆。"));
		}
		finally
		{
			if (operationContext.IsCurrent) IsCloning = false;
		}
	}

    private Task? refreshTask;
    private RepositoryChangeKind pendingChanges;
    private RepositorySnapshot? lastSnapshot;
    public Task RefreshAsync() => QueueRefreshAsync(RepositoryChangeKind.All);

    private Task QueueRefreshAsync(RepositoryChangeKind changes)
    {
        if (!HasRepository || requestsDisposed) return Task.CompletedTask;
        pendingChanges |= changes;
        if (refreshTask is { IsCompleted: false })
        {
            requests.Invalidate("snapshot");
            return refreshTask;
        }
        return refreshTask = DrainRefreshAsync();
    }

    private async Task DrainRefreshAsync()
    {
        await Task.Yield();
        using var session = requests.Capture();
        while (session.IsCurrent && pendingChanges != RepositoryChangeKind.None)
        {
            var changes = pendingChanges;
            pendingChanges = RepositoryChangeKind.None;
            using var request = requests.Begin("snapshot");
            var observedWatcher = watcher as GitVisualizer.Infrastructure.FileSystem.RepositoryWatcher;
            var observedVersion = observedWatcher?.Version;
            request.AdditionalValidity = () => observedWatcher?.Version == observedVersion;
            try
            {
                var snapshot = await request.Await(git is IIncrementalRepositoryService incremental
                    ? incremental.GetSnapshotAsync(request.RepositoryPath, lastSnapshot, changes, request.Token)
                    : git.GetSnapshotAsync(request.RepositoryPath, request.Token));
                await ApplySnapshotAsync(snapshot, request, changes);
                request.Check();
                lastSnapshot = snapshot;
                await SynchronizeCurrentDocumentWithDiskAsync();
            }
            catch (OperationCanceledException)
            {
                // A superseding event must retain every invalidated category.
                if (session.IsCurrent) pendingChanges |= observedWatcher?.Version != observedVersion ? RepositoryChangeKind.All : changes;
            }
            catch (Exception error) { if (request.IsCurrent) StatusText = "刷新失败：" + error.Message; }
        }
    }
	private async Task CommitAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		if (HasRepository)
		{
			GitOperationResult result = await writeContext.Await(git.CommitAsync(writeContext.RepositoryPath, CommitMessage, cancellationToken: writeContext.Token));
			ShowResult(result);
			if (result.Success)
			{
				await CompleteSuccessfulCommitAsync(result, writeContext);
			}
		}
		}
		catch (OperationCanceledException) { }
	}
	private async Task AmendAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		if (HasRepository)
		{
			GitOperationResult result = await writeContext.Await(git.CommitAsync(writeContext.RepositoryPath, CommitMessage, null, amend: true, cancellationToken: writeContext.Token));
			ShowResult(result);
			if (result.Success)
			{
				await CompleteSuccessfulCommitAsync(result, writeContext);
			}
		}
		}
		catch (OperationCanceledException) { }
	}

	private async Task CompleteSuccessfulCommitAsync(GitOperationResult result, RequestContext writeContext)
	{
		writeContext.Check();
		// These helpers handle cancellation themselves, so recheck the original
		// write session after each await before changing any more UI state.
		await writeContext.Await(ReloadAllAsync(writeContext));
		await writeContext.Await(ShowWorkingTreeCoreAsync(writeContext));
		SelectedCommit = null;
		SelectedRightTabIndex = 1;
		CommitMessage = string.Empty;
		StatusText = result.Summary;
	}
	private async Task StageAsync(FileChange? change)
	{
		using var writeContext = requests.Capture();
		try
		{
		if ((object)change != null)
		{
			bool flag = IsCurrentDocument(change.Path);
			if (flag)
			{
				flag = !(await writeContext.Await(SaveCurrentDocumentAsync(refreshAfterSave: false)));
			}
			if (!flag)
			{
				ShowResult(await writeContext.Await(git.StageFilesAsync(writeContext.RepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(change.Path), cancellationToken: writeContext.Token)));
				await writeContext.Await(RefreshAsync());
			}
		}
		}
		catch (OperationCanceledException) { }
	}
	private async Task UnstageAsync(FileChange? change)
	{
		using var writeContext = requests.Capture();
		try
		{
		if ((object)change != null)
		{
			ShowResult(await writeContext.Await(git.UnstageFilesAsync(writeContext.RepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(change.Path), cancellationToken: writeContext.Token)));
			await writeContext.Await(RefreshAsync());
		}
		}
		catch (OperationCanceledException) { }
	}
	private async Task StageAllAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		if (await writeContext.Await(SaveCurrentDocumentAsync(refreshAfterSave: false)))
		{
			RepositorySnapshot repositorySnapshot;
			try
			{
				repositorySnapshot = await writeContext.Await(git.GetSnapshotAsync(writeContext.RepositoryPath, cancellationToken: writeContext.Token));
			}
			catch (Exception ex)
			{
				StatusText = "读取待暂存文件失败：" + ex.Message;
				return;
			}
			string[] array = (from change in repositorySnapshot.Changes
				where !change.IsStaged && change.State != GitChangeState.Ignored
				select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
			if (array.Length == 0)
			{
				StatusText = "没有可暂存的修改。";
				await writeContext.Await(RefreshAsync());
			}
			else
			{
				ShowResult(await writeContext.Await(git.StageFilesAsync(writeContext.RepositoryPath, array, cancellationToken: writeContext.Token)));
				await writeContext.Await(RefreshAsync());
			}
		}
		}
		catch (OperationCanceledException) { }
	}
	private async Task UnstageAllAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		if (StagedChanges.Count != 0)
		{
			ShowResult(await writeContext.Await(git.UnstageFilesAsync(writeContext.RepositoryPath, StagedChanges.Select((FileChange change) => change.Path).ToArray(), cancellationToken: writeContext.Token)));
			await writeContext.Await(RefreshAsync());
		}
		}
		catch (OperationCanceledException) { }
	}
	private async Task SaveEditorAsync()
	{
		await SaveCurrentDocumentAsync(refreshAfterSave: true);
	}
	private async Task SaveAndStageEditorAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		if ((object)CurrentDocument == null || !CanSaveCurrentDocument)
		{
			return;
		}
		string documentPath = CurrentDocument.Path;
		if (await writeContext.Await(SaveCurrentDocumentAsync(refreshAfterSave: false)))
		{
			string relativePath = Path.GetRelativePath(writeContext.RepositoryPath, documentPath);
			if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal) || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			{
				StatusText = "当前文件不在已打开的仓库中，不能暂存。";
				return;
			}
			ShowResult(await writeContext.Await(git.StageFilesAsync(writeContext.RepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(relativePath), cancellationToken: writeContext.Token)));
			await writeContext.Await(RefreshAsync());
		}
		}
		catch (OperationCanceledException) { }
	}

	public async Task<GitOperationResult?> StageSelectedFilesAsync(IReadOnlyList<FileChange> changes)
	{
		using var operationContext = requests.Capture();
		string[] paths = (from change in changes
			where !change.IsStaged && change.State != GitChangeState.Ignored
			select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (paths.Length == 0)
		{
			StatusText = "请先选择至少一个未暂存文件。";
			return null;
		}
		bool flag = HasUnsavedEditorChanges && paths.Any(IsCurrentDocument);
		if (flag)
		{
			flag = !(await SaveCurrentDocumentAsync(refreshAfterSave: false));
		}
		if (flag)
		{
			return null;
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("StageSelectedFilesAsync", string.Empty);
		GitOperationResult result = await git.StageFilesAsync(operationContext.RepositoryPath, paths, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult?> UnstageSelectedFilesAsync(IReadOnlyList<FileChange> changes)
	{
		using var operationContext = requests.Capture();
		string[] array = (from change in changes
			where change.IsStaged
			select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			StatusText = "请先选择至少一个已暂存文件。";
			return null;
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("UnstageSelectedFilesAsync", string.Empty);
		GitOperationResult result = await git.UnstageFilesAsync(operationContext.RepositoryPath, array, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	private async Task<bool> SaveCurrentDocumentAsync(bool refreshAfterSave)
	{
		using var saveContext = requests.Capture();
		if ((object)CurrentDocument == null || !CanSaveCurrentDocument || !HasUnsavedEditorChanges)
		{
			return true;
		}
		await editorState.EditorSaveGate.WaitAsync();
		if (!saveContext.IsCurrent) { editorState.EditorSaveGate.Release(); return false; }
		try
		{
			TextDocument document = CurrentDocument;
			if ((object)document == null || !CanSaveCurrentDocument || !HasUnsavedEditorChanges)
			{
				return true;
			}
			CancelScheduledDraftSave();
			string text = EditorText;
			try
			{
				await files.SaveTextAsync(saveContext.RepositoryPath, document, text, allowExternalOverwrite: false, cancellationToken: saveContext.Token);
			}
			catch (ExternalFileChangedException)
			{
				EditorSafetyAction action = await editorInteraction.ResolveExternalChangeAsync(document);
				if (!saveContext.IsCurrent) return false;
				if (action == EditorSafetyAction.Cancel)
				{
					StatusText = "保存已取消，编辑器中的未保存内容仍然保留。";
					ScheduleDraftSave();
					return false;
				}
				if (action == EditorSafetyAction.Discard)
				{
					await ReloadCurrentDocumentFromDiskAsync(document.Path, deleteDraft: true);
					StatusText = "已重新载入 " + Path.GetFileName(document.Path);
					return true;
				}
				StatusText = "检测到外部修改，保存已中止；编辑器草稿仍然保留。请先重新载入并核对文件。";
				ScheduleDraftSave();
				return false;
			}
			if (!saveContext.IsCurrent) return true;
			TextDocument textDocument = await files.OpenTextAsync(document.Path);
			if (!saveContext.IsCurrent) return true;
			if ((object)CurrentDocument != null && CurrentDocument.Path.Equals(document.Path, StringComparison.OrdinalIgnoreCase))
			{
				CurrentDocument = textDocument;
				if (!string.Equals(EditorText, text, StringComparison.Ordinal))
				{
					HasUnsavedEditorChanges = true;
					ScheduleDraftSave();
					StatusText = "已保存先前内容；保存期间输入的新内容仍未保存。";
					return false;
				}
				EditorText = textDocument.Text;
				HasUnsavedEditorChanges = false;
			}
			await draftStore.DeleteAsync(saveContext.RepositoryPath, document.Path);
			StatusText = "已保存 " + Path.GetFileName(document.Path);
			if (refreshAfterSave)
			{
				await RefreshAsync();
			}
			return true;
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!saveContext.IsCurrent) return false;
			StatusText = "保存失败：" + ex.Message;
			ScheduleDraftSave();
			return false;
		}
		finally
		{
			editorState.EditorSaveGate.Release();
		}
	}
	private async Task OpenCurrentDocumentExternallyAsync()
	{
		if (editorState.CurrentDocumentIsHistorical && editorState.CurrentHistoricalCommitId != null && editorState.CurrentHistoricalRelativePath != null)
		{
			await OpenHistoricalFileExternallyAsync(editorState.CurrentHistoricalCommitId, editorState.CurrentHistoricalRelativePath);
			return;
		}
		if ((object)CurrentDocument != null && CanOpenCurrentDocumentExternally)
		{
			await OpenFileExternallyAsync(CurrentDocument.Path);
		}
		else if ((object)CurrentDocument != null)
		{
			StatusText = "历史版本文件为只读快照，不能直接交给外部程序打开。";
		}
	}

	public bool IsExternalDocumentPath(string path)
	{
		return ExternalDocumentExtensions.Contains(Path.GetExtension(path));
	}

	public Task<bool> OpenFileTreeItemExternallyAsync(FileTreeItem item)
	{
		return (item.CommitId != null) ? OpenHistoricalFileExternallyAsync(item.CommitId, item.RelativePath) : OpenFileExternallyAsync(item.FullPath);
	}

	public async Task<bool> OpenFileExternallyAsync(string path)
	{
		try
		{
			await files.OpenExternalAsync(path);
			StatusText = "已使用系统默认程序打开 " + Path.GetFileName(path);
			return true;
		}
		catch (Exception ex)
		{
			StatusText = "无法使用系统默认程序打开：" + ex.Message;
			return false;
		}
	}
	private async Task FetchAsync()
	{
		using var writeContext = requests.Capture();
		try
		{
		RemoteInfo remote = SelectedRemote;
		if ((object)remote == null)
		{
			StatusText = "仓库尚未配置远程地址。";
			return;
		}
		RemoteCredential credential = await writeContext.Await(GetRemoteCredentialAsync(remote));
		GitOperationResult result = await writeContext.Await(git.FetchAsync(writeContext.RepositoryPath, remote.Name, credential, cancellationToken: writeContext.Token));
		await writeContext.Await(ReloadAllAsync());
		ShowResult(result);
		}
		catch (OperationCanceledException) { }
	}

	public Task<GitOperationResult> PullAsync(PullStrategy strategy)
	{
		RemoteInfo remote = SelectedRemote;
		string remoteBranchName = Head?.BranchName ?? string.Empty;
		return PullAsync(remote, remoteBranchName, strategy);
	}

	public async Task<GitOperationResult> PullAsync(RemoteInfo? remote, string remoteBranchName, PullStrategy strategy)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("拉取远程更改"))
		{
			return CanceledOperation("pull");
		}
		if (!HasRepository || (object)remote == null || string.IsNullOrWhiteSpace(remoteBranchName))
		{
			StatusText = "当前仓库尚未配置可拉取的远程地址。";
			return GitOperationResult.Fail("pull", PullCommand(strategy), new InvalidOperationException("当前仓库尚未配置可拉取的远程地址。"));
		}
		if (IsBusy)
		{
			StatusText = "当前有其他操作正在执行，请稍后再试。";
			return GitOperationResult.Fail("pull", PullCommand(strategy), new InvalidOperationException("当前有其他操作正在执行，请稍后再试。"));
		}
		DateTime overlayStarted = DateTime.UtcNow;
		PullSourceText = remote.Name + " → " + (Head?.BranchName ?? CurrentBranch);
		IsBusy = true;
		IsPulling = true;
		GitOperationResult result2;
		try
		{
			GitOperationResult result;
			try
			{
				RemoteCredential credential = await GetRemoteCredentialAsync(remote);
				if (!operationContext.IsCurrent) return GitOperationResult.Canceled("PullAsync", string.Empty);
				result = await git.PullAsync(operationContext.RepositoryPath, remote.Name, remoteBranchName, strategy, credential, cancellationToken: operationContext.Token);
			}
			catch (Exception exception)
			{
				result = GitOperationResult.Fail("pull", PullCommand(strategy), exception);
			}
			Dictionary<string, PullStrategy> dictionary = settings.PullStrategies.ToDictionary<KeyValuePair<string, PullStrategy>, string, PullStrategy>((KeyValuePair<string, PullStrategy> pair) => pair.Key, (KeyValuePair<string, PullStrategy> pair) => pair.Value);
			dictionary[operationContext.RepositoryPath] = strategy;
			settings = settings with
			{
				PullStrategies = dictionary
			};
			await settingsStore.SaveAsync(settings);
			try
			{
				await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
			}
			catch (Exception ex)
			{
				result = result with
				{
					Warnings = result.Warnings.Append("拉取后刷新界面失败：" + ex.Message).ToArray()
				};
			}
			if (!operationContext.IsCurrent) return result;
		ShowResult(result);
			result2 = result;
		}
		finally
		{
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(700L) - (DateTime.UtcNow - overlayStarted);
			if (timeSpan > TimeSpan.Zero)
			{
				await Task.Delay(timeSpan);
			}
			if (operationContext.IsCurrent) IsPulling = false;
			if (operationContext.IsCurrent) IsBusy = false;
		}
		return result2;
	}

	private static string PullCommand(PullStrategy strategy)
	{
		return strategy switch
		{
			PullStrategy.Rebase => "git pull --rebase", 
			PullStrategy.FastForwardOnly => "git pull --ff-only", 
			_ => "git pull --no-rebase", 
		};
	}
	private async Task PushAsync()
	{
		await PushToRemoteAsync(SelectedRemote);
	}

	public async Task<GitOperationResult> PushToRemoteAsync(RemoteInfo? remote, IProgress<GitPushProgress>? progress = null, bool forceWithLease = false)
	{
		using var operationContext = requests.Capture();
		if ((object)remote == null)
		{
			StatusText = "仓库尚未配置远程地址。";
			return GitOperationResult.Fail("push", "git push", new InvalidOperationException(StatusText));
		}
		if (IsBusy)
		{
			StatusText = "当前有其他操作正在执行，请稍后再试。";
			return GitOperationResult.Fail("push", "git push " + remote.Name, new InvalidOperationException("当前有其他操作正在执行，请稍后再试。"));
		}
		IsBusy = true;
		try
		{
			progress?.Report(new GitPushProgress(GitPushProgressStage.Connecting, 0L, 0L, 0L, "正在准备凭据并连接 " + remote.Name));
			GitOperationResult result;
			try
			{
				RemoteCredential credential = await GetRemoteCredentialAsync(remote);
				if (!operationContext.IsCurrent) return GitOperationResult.Canceled("PushToRemoteAsync", string.Empty);
				result = await git.PushAsync(operationContext.RepositoryPath, remote.Name, forceWithLease, credential, progress, cancellationToken: operationContext.Token);
			}
			catch (Exception exception)
			{
				result = GitOperationResult.Fail("push", forceWithLease ? ("git push --force-with-lease " + remote.Name) : ("git push " + remote.Name), exception);
			}
			try
			{
				await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
			}
			catch (Exception ex)
			{
				result = result with
				{
					Warnings = result.Warnings.Append("推送后刷新界面失败：" + ex.Message).ToArray()
				};
			}
			if (!operationContext.IsCurrent) return result;
		ShowResult(result);
			return result;
		}
		finally
		{
			if (operationContext.IsCurrent) IsBusy = false;
		}
	}
	private async Task LoadMoreHistoryAsync()
	{
		using var request = requests.Begin("history");
		try
		{
		using var measurement = PerformanceRecorder.Begin(PerformanceOperation.HistoryPage);
		if (!HasRepository || (HasLoadedHistory && !HasMoreHistory))
		{
			return;
		}
		IReadOnlyList<CommitNode> readOnlyList = ((!string.IsNullOrEmpty(SelectedHistoryBranchName)) ? (await request.Await(git.GetBranchHistoryAsync(request.RepositoryPath, SelectedHistoryBranchName, historyState.HistoryLoaded, 201, cancellationToken: request.Token))) : (await request.Await(git.GetHistoryAsync(request.RepositoryPath, historyState.HistoryLoaded, 201, cancellationToken: request.Token))));
		(int, bool) tuple = CalculateHistoryPageState(readOnlyList.Count);
		historyState.AppendPage(readOnlyList);
		HasMoreHistory = tuple.Item2;
		HasLoadedHistory = true;
		StatusText = ((tuple.Item1 == 0) ? "已经显示全部提交。" : (string.IsNullOrEmpty(SelectedHistoryBranchName) ? $"已加载 {historyState.HistoryLoaded} 个提交 · 全部分支" : $"已加载 {historyState.HistoryLoaded} 个提交 · {SelectedHistoryBranchName} 分支"));
		}
		catch (OperationCanceledException) { return; }
	}

	internal static (int VisibleCount, bool HasMore) CalculateHistoryPageState(int fetchedCount)
	{
		return HistoryState.Page(fetchedCount);
	}

	public async Task<bool> SelectChangeAsync(FileChange? change)
	{
		using var request = requests.Begin("navigation");
		try
		{
		if (change != null && !IsCurrentDocumentPath(change.Path) &&
			!await request.Await(PrepareForDocumentTransitionAsync("切换文件")))
		{
			return false;
		}
		SelectedChange = change;
		ClearDiffPresentation();
		if ((object)change == null)
		{
			DiffContextText = "工作区差异";
			return true;
		}
		SelectedRightTabIndex = 0;
		try
		{
			LoadDiffPresentation(await request.Await(diff.GetWorkingDiffPresentationAsync(request.RepositoryPath, change.Path, change.IsStaged, cancellationToken: request.Token)), isCommitComparison: false);
			string path = Path.Combine(request.RepositoryPath, change.Path);
			if (File.Exists(path))
			{
				await request.Await(OpenFileAsync(path, request));
			}
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return false;
			DiffText = "无法显示差异：" + ex.Message;
			DiffSummaryText = DiffText;
			ShowDiffEmptyState = true;
		}
		return true;
		}
		catch (OperationCanceledException) { return false; }
	}

	public async Task<bool> SelectFileAsync(FileTreeItem? item)
	{
		using var request = requests.Begin("navigation");
		try
		{
		if (item != null && !item.IsDirectory)
		{
			if ((item.CommitId != null || !IsCurrentDocumentFullPath(item.FullPath)) &&
				!await request.Await(PrepareForDocumentTransitionAsync("切换文件")))
			{
				return false;
			}
			SelectedRightTabIndex = 1;
			string commitId = item.CommitId;
			if (commitId != null)
			{
				return await request.Await(OpenCommitFileAsync(commitId, item.RelativePath, request));
			}
			return await request.Await(OpenFileAsync(item.FullPath, request));
		}
		return true;
		}
		catch (OperationCanceledException) { return false; }
	}

	public Task<bool> SelectCommitAsync(CommitNode? commit) => SelectCommitCoreAsync(commit);

	private async Task<bool> SelectCommitCoreAsync(CommitNode? commit, RequestContext? parent = null)
	{
		using var ownedRequest = parent is null ? requests.Begin("navigation") : null;
		var request = parent ?? ownedRequest!;
		try
		{
		if (!await request.Await(PrepareForDocumentTransitionAsync("浏览提交历史")))
		{
			return false;
		}
		SelectedCommit = commit;
		if ((object)commit == null)
		{
			await request.Await(ShowWorkingTreeCoreAsync(request));
			return true;
		}
		SelectedRightTabIndex = 2;
		List<string> list = (from branch in Branches
			where string.Equals(branch.TipId, commit.Id, StringComparison.Ordinal)
			select branch.FriendlyName).Concat(from tag in Tags
			where string.Equals(tag.TargetId, commit.Id, StringComparison.Ordinal)
			select "tag:" + tag.Name).ToList();
		if ((object)Head != null && string.Equals(Head.CommitId, commit.Id, StringComparison.Ordinal))
		{
			list.Insert(0, Head.IsDetached ? "HEAD（游离）" : ("HEAD -> " + Head.BranchName));
		}
		List<string> list2 = (from historyEvent in HistoryEvents.Where((GitHistoryEvent historyEvent) => string.Equals(historyEvent.CommitId, commit.Id, StringComparison.Ordinal)).Where(delegate(GitHistoryEvent historyEvent)
			{
				GitHistoryEventKind kind = historyEvent.Kind;
				return (uint)kind <= 6u;
			})
			select historyEvent.Description).Distinct<string>(StringComparer.CurrentCulture).ToList();
		if (commit.ParentIds.Count > 1 && !list2.Any((string explanation) => explanation.Contains("merge commit", StringComparison.OrdinalIgnoreCase)))
		{
			list2.Add($"该节点为 merge commit，包含 {commit.ParentIds.Count} 个父提交。");
		}
		if (list2.Count == 0)
		{
			list2.Add("这是普通提交节点；连线仅表示 parent 关系，不表示提交归属于某个分支。");
		}
		DetailsText = $"{commit.ShortId}\n{commit.Message}\n\n作者：{commit.AuthorName} <{commit.AuthorEmail}>\n时间：{commit.AuthoredAt.LocalDateTime:G}\n父提交：{string.Join(", ", commit.ParentIds.Select((string id) => id.Substring(0, Math.Min(8, id.Length))))}\n引用：{string.Join(", ", list)}\n\n关系说明：\n" + string.Join("\n", list2.Select((string explanation) => "• " + explanation));
		int loadVersion = fileTreeState.BeginLoad();
		IsBrowsingHistoricalCommit = true;
		CanModifyFileTree = false;
		FileTreeContextText = "版本 " + commit.ShortId;
		try
		{
			IReadOnlyList<CommitTreeEntry> readOnlyList = await request.Await(git is ICommitDirectoryService directoryService
                ? directoryService.GetCommitDirectoryAsync(request.RepositoryPath, commit.Id, "", request.Token)
                : git.GetCommitTreeAsync(request.RepositoryPath, commit.Id, cancellationToken: request.Token));
			if (fileTreeState.IsCurrent(loadVersion) && string.Equals(SelectedCommit?.Id, commit.Id, StringComparison.Ordinal))
			{
				if (git is ICommitDirectoryService directories)
                {
                    var nodes = await request.Await(CreateHistoricalNodesAsync(directories, request.RepositoryPath,
                        commit.Id, readOnlyList, request.Token));
                    await request.Await(FileTreeItem.ApplyEntriesAsync(FileTree, nodes, request.Token));
                }
                else BuildCommitFileTree(commit.Id, readOnlyList);
				StatusText = $"正在查看版本 {commit.ShortId} 的 {readOnlyList.Count((CommitTreeEntry entry) => !entry.IsDirectory)} 个文件";
			}
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return false;
			if (fileTreeState.IsCurrent(loadVersion))
			{
				FileTree.Clear();
				StatusText = "无法读取版本 " + commit.ShortId + " 的文件：" + ex.Message;
			}
		}
		return true;
		}
		catch (OperationCanceledException) { return false; }
	}

	public async Task<bool> SelectBranchAsync(BranchInfo? branch)
	{
		using var request = requests.Begin("navigation");
		try
		{
		if (branch != null && !await request.Await(PrepareForDocumentTransitionAsync("浏览其他分支")))
		{
			return false;
		}
		if ((object)branch != null && HasRepository)
		{
			SelectedBranch = branch;
			SelectedHistoryBranchName = branch.FriendlyName;
			HistoryContextText = branch.FriendlyName + " 分支版本关系";
			History.Clear();
			ResetHistoryPagination();
			await request.Await(LoadMoreHistoryAsync());
			CommitNode tip = History.FirstOrDefault((CommitNode commit) => string.Equals(commit.Id, branch.TipId, StringComparison.Ordinal));
			if ((object)tip == null)
			{
				StatusText = "无法在已加载历史中找到分支 " + branch.FriendlyName + " 的最新版本";
				return true;
			}
			await request.Await(SelectCommitCoreAsync(tip, request));
			FileTreeContextText = "分支 " + branch.FriendlyName + " · " + tip.ShortId;
			StatusText = "正在查看 " + branch.FriendlyName + " 分支的版本关系和最新文件";
		}
		return true;
		}
		catch (OperationCanceledException) { return false; }
	}
	private Task ShowWorkingTreeAsync() => ShowWorkingTreeCoreAsync();

	private async Task ShowWorkingTreeCoreAsync(RequestContext? parent = null)
	{
		using var ownedRequest = parent is null ? requests.Begin("navigation") : null;
		var request = parent ?? ownedRequest!;
		try
		{
		bool flag = !string.IsNullOrEmpty(SelectedHistoryBranchName);
		fileTreeState.Invalidate();
		SelectedCommit = null;
		SelectedBranch = null;
		SelectedHistoryBranchName = string.Empty;
		HistoryContextText = "全部分支";
		DetailsText = string.Empty;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		if (editorState.CurrentDocumentIsHistorical)
		{
			CurrentDocument = null;
			EditorText = string.Empty;
			HasUnsavedEditorChanges = false;
			IsExternalOnlyDocument = false;
			CanSaveCurrentDocument = false;
			CanOpenCurrentDocumentExternally = false;
			editorState.CurrentDocumentIsHistorical = false;
		}
		if (HasRepository)
		{
			await request.Await(BuildFileTreeAsync(request.RepositoryPath, request.Token));
			StatusText = "正在显示当前工作区文件";
			if (flag)
			{
				History.Clear();
				ResetHistoryPagination();
				await request.Await(LoadMoreHistoryAsync());
				StatusText = "正在显示当前工作区文件 · 全部分支关系";
			}
		}
		}
		catch (OperationCanceledException) { return; }
	}

    public void SelectConflict(ConflictFile? conflict) => _ = SelectConflictAsync(conflict);

    private async Task SelectConflictAsync(ConflictFile? conflict, RequestContext? parent = null)
    {
        parent?.Check();
        using var request = requests.Begin("conflict-details");
        if (conflict is { IsLoaded: false } && git is IConflictDetailsService details)
        {
            SelectedConflict = conflict;
            HasSelectedConflict = true;
            CanEditSelectedConflict = false;
            ConflictBaseText = ConflictOursText = ConflictTheirsText = ConflictResultText = string.Empty;
            try { conflict = await request.Await(details.GetConflictAsync(request.RepositoryPath, conflict.Path, request.Token)); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { if (request.IsCurrent) StatusText = "无法读取冲突：" + error.Message; return; }
        }
        parent?.Check();
		SelectedConflict = conflict;
		HasSelectedConflict = (object)conflict != null;
		CanEditSelectedConflict = (object)conflict != null && !conflict.IsReadOnly;
		if ((object)conflict != null)
		{
			SelectedRightTabIndex = 3;
		}
		ConflictBaseText = conflict?.BaseText ?? string.Empty;
		ConflictOursText = conflict?.OursText ?? string.Empty;
		ConflictTheirsText = conflict?.TheirsText ?? string.Empty;
		ConflictResultText = conflict?.ResultText ?? string.Empty;
	}

	public void UseConflictSide(ConflictSide side)
	{
		ConflictFile? conflictFile = SelectedConflict;
		if ((object)conflictFile != null && conflictFile.IsReadOnly)
		{
			StatusText = "此冲突无法安全文本编辑；请使用外部工具处理后再暂存。";
			return;
		}
		ConflictResultText = conflictState.SideText(side);
	}

	public async Task<GitOperationResult> ResolveSelectedConflictAsync()
	{
		using var operationContext = requests.Capture();
		ConflictFile conflict = SelectedConflict;
		if ((object)conflict == null)
		{
			throw new InvalidOperationException("请先选择冲突文件。");
		}
		if (conflict.IsReadOnly)
		{
			InvalidOperationException exception = new InvalidOperationException("此冲突为只读；请使用外部工具处理。");
			GitOperationResult result = GitOperationResult.Fail("conflict-resolve", "git add -- <path>", exception);
			if (!operationContext.IsCurrent) return result;
		ShowResult(result);
			return result;
		}
		if (IsCurrentDocumentPath(conflict.Path) &&
			!await PrepareForDocumentTransitionAsync("解决当前文件的冲突"))
		{
			return CanceledOperation("conflict-resolve");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ResolveSelectedConflictAsync", string.Empty);
		GitOperationResult result2 = await git.ResolveConflictAsync(operationContext.RepositoryPath, conflict.Path, ConflictResultText, originalDocument: conflict.OriginalDocument, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result2;
		ShowResult(result2);
		if (result2.Success) await RefreshAsync();
		if (!operationContext.IsCurrent) return result2;
		return result2;
	}

	public async Task<GitOperationResult> CreateBranchAsync(string name)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("CreateBranchAsync", string.Empty);
		GitOperationResult result = await git.CreateBranchAsync(operationContext.RepositoryPath, name, SelectedCommit?.Id, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> CheckoutBranchAsync(BranchInfo branch)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("切换分支"))
		{
			return CanceledOperation("checkout");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("CheckoutBranchAsync", string.Empty);
		GitOperationResult result = await git.CheckoutBranchAsync(operationContext.RepositoryPath, branch.FriendlyName, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public Task<BranchDeletionCheck> CheckBranchDeletionAsync(BranchInfo branch)
	{
		return git.CheckBranchDeletionAsync(ActiveRepositoryPath, branch.FriendlyName);
	}

	public async Task<GitOperationResult> DeleteBranchAsync(BranchInfo branch, bool force)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("DeleteBranchAsync", string.Empty);
		GitOperationResult result = await git.DeleteBranchAsync(operationContext.RepositoryPath, branch.FriendlyName, force, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> MergeBranchAsync(BranchInfo branch)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("合并分支"))
		{
			return CanceledOperation("merge");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("MergeBranchAsync", string.Empty);
		GitOperationResult result = await git.MergeAsync(operationContext.RepositoryPath, branch.FriendlyName, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> CherryPickSelectedAsync()
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("拣选提交"))
		{
			return CanceledOperation("cherry-pick");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("CherryPickSelectedAsync", string.Empty);
		GitOperationResult result = await git.CherryPickAsync(operationContext.RepositoryPath, SelectedCommit.Id, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> RevertSelectedAsync()
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("撤销提交"))
		{
			return CanceledOperation("revert");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("RevertSelectedAsync", string.Empty);
		GitOperationResult result = await git.RevertAsync(operationContext.RepositoryPath, SelectedCommit.Id, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> ResetSelectedAsync(GitResetMode mode)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("回退当前分支"))
		{
			return CanceledOperation("reset");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ResetSelectedAsync", string.Empty);
		GitOperationResult result = await git.ResetAsync(operationContext.RepositoryPath, SelectedCommit.Id, mode, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> ContinueOperationAsync()
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("继续 Git 操作"))
		{
			return CanceledOperation("continue");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ContinueOperationAsync", string.Empty);
		GitOperationResult result = await git.ContinueOperationAsync(operationContext.RepositoryPath, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> AbortOperationAsync()
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("中止 Git 操作"))
		{
			return CanceledOperation("abort");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("AbortOperationAsync", string.Empty);
		GitOperationResult result = await git.AbortOperationAsync(operationContext.RepositoryPath, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> ConfigureIdentityAsync(GitIdentity identity, bool global)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ConfigureIdentityAsync", string.Empty);
		GitOperationResult result = await git.SetIdentityAsync(operationContext.RepositoryPath, identity, global, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		return result;
	}

	public async Task SaveRemoteCredentialAsync(RemoteInfo remote, RemoteCredential credential)
	{
		if (credential.Kind == CredentialKind.HttpsToken && !RemoteUrlSecurity.IsHttps(remote.FetchUrl))
		{
			throw new InvalidOperationException("个人访问令牌只能保存并发送到绝对 HTTPS 远程地址。");
		}
		string key = RemoteCredentialKey.Create(remote.FetchUrl);
		if (credential.Kind != CredentialKind.SshAgent)
		{
			await credentialVault.SaveAsync(key, JsonSerializer.Serialize(credential));
		}
		else
		{
			await credentialVault.DeleteAsync(key);
		}
		StatusText = ((credential.Kind == CredentialKind.SshAgent) ? "此远程将使用 Windows SSH Agent。" : ("已保存 " + remote.Name + " 的仓库专用凭据。"));
	}

	public async Task SaveCloneCredentialAsync(string remoteUrl, RemoteCredential credential)
	{
		if (credential.Kind == CredentialKind.HttpsToken && credential.Remember)
		{
			if (!RemoteUrlSecurity.IsHttps(remoteUrl))
			{
				throw new InvalidOperationException("个人访问令牌只能保存并发送到绝对 HTTPS 远程地址。");
			}
			await credentialVault.SaveAsync(RemoteCredentialKey.Create(remoteUrl), JsonSerializer.Serialize(credential));
			StatusText = "已将该仓库的 HTTPS 凭据保存到 Windows 凭据管理器。";
		}
	}

	public async Task DeleteRemoteCredentialAsync(RemoteInfo remote)
	{
		await credentialVault.DeleteAsync(RemoteCredentialKey.Create(remote.FetchUrl));
		StatusText = "已删除 " + remote.Name + " 的仓库专用凭据。";
	}

	public async Task<RemoteCredential?> LoadSavedRemoteCredentialAsync(RemoteInfo? remote = null)
	{
		if ((object)remote == null)
		{
			remote = SelectedRemote ?? Remotes.FirstOrDefault();
		}
		if ((object)remote == null || IsSsh(remote.FetchUrl) || !RemoteUrlSecurity.IsHttps(remote.FetchUrl))
		{
			return null;
		}
		string text = await credentialVault.GetAsync(RemoteCredentialKey.Create(remote.FetchUrl));
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			RemoteCredential remoteCredential = JsonSerializer.Deserialize<RemoteCredential>(text);
			return ((object)remoteCredential != null && remoteCredential.Kind == CredentialKind.HttpsToken) ? remoteCredential : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public async Task<GitOperationResult?> ApplySelectedHunksAsync(IReadOnlyList<DiffHunk> hunks, bool unstage)
	{
		using var operationContext = requests.Capture();
		if (indexPatch == null || (object)SelectedChange == null || hunks.Count == 0)
		{
			StatusText = ((hunks.Count == 0) ? "请先选择至少一个差异块。" : "差异块服务不可用。");
			return null;
		}
		if (HasUnsavedEditorChanges && IsCurrentDocument(SelectedChange.Path))
		{
			if (!(await SaveCurrentDocumentAsync(refreshAfterSave: false)))
			{
				return null;
			}
			await SelectChangeAsync(SelectedChange);
			StatusText = "文件已保存，差异已刷新；请重新选择要处理的差异块。";
			return null;
		}
		string path = SelectedChange.Path;
		GitOperationResult gitOperationResult = ((!unstage) ? (await indexPatch.StageHunksAsync(operationContext.RepositoryPath, path, hunks, cancellationToken: operationContext.Token)) : (await indexPatch.UnstageHunksAsync(operationContext.RepositoryPath, path, hunks, cancellationToken: operationContext.Token)));
		GitOperationResult result = gitOperationResult;
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		FileChange change = (unstage ? StagedChanges : UnstagedChanges).FirstOrDefault((FileChange fileChange) => fileChange.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? (unstage ? UnstagedChanges : StagedChanges).FirstOrDefault((FileChange fileChange) => fileChange.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
		await SelectChangeAsync(change);
		return result;
	}

	public async Task<GitOperationResult> RenameBranchAsync(BranchInfo branch, string newName)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("RenameBranchAsync", string.Empty);
		GitOperationResult result = await git.RenameBranchAsync(operationContext.RepositoryPath, branch.FriendlyName, newName.Trim(), cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> CheckoutSelectedCommitAsync()
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("切换提交"))
		{
			return CanceledOperation("checkout");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("CheckoutSelectedCommitAsync", string.Empty);
		GitOperationResult result = await git.CheckoutCommitAsync(operationContext.RepositoryPath, SelectedCommit.Id, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task CompareCommitsAsync(CommitNode oldCommit, CommitNode newCommit)
	{
		using var request = requests.Begin("navigation");
		try
		{
		ClearDiffPresentation();
		SelectedChange = null;
		SelectedRightTabIndex = 0;
		try
		{
			comparisonOldId = oldCommit.Id;
            comparisonNewId = newCommit.Id;
            LoadDiffPresentation(await request.Await(diff is ICommitDiffMetadataService metadata
                ? metadata.GetCommitDiffMetadataAsync(request.RepositoryPath, oldCommit.Id, newCommit.Id, request.Token)
                : diff.CompareCommitsPresentationAsync(request.RepositoryPath, oldCommit.Id, newCommit.Id, cancellationToken: request.Token)), isCommitComparison: true);
			StatusText = "已比较 " + oldCommit.ShortId + " 与 " + newCommit.ShortId;
		}
		catch (OperationCanceledException) { return; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return;
			DiffText = "无法比较提交：" + ex.Message;
			DiffSummaryText = DiffText;
			ShowDiffEmptyState = true;
			StatusText = "提交比较失败。";
		}
		}
		catch (OperationCanceledException) { return; }
	}

    private string? comparisonOldId, comparisonNewId;
    public async Task<DiffFilePresentation?> LoadComparedFileAsync(DiffFilePresentation file)
    {
        if (comparisonOldId is null || comparisonNewId is null || !ShowCommitDiffCards) return null;
        using var request = requests.Begin("comparison-file:" + file.Path);
        try
        {
            var result = await request.Await(diff.CompareCommitsPresentationAsync(request.RepositoryPath,
                comparisonOldId, comparisonNewId, file.Path, request.Token));
            if (!ShowCommitDiffCards || !DiffFiles.Contains(file)) return null;
            SelectedDiffFile = result.Files.FirstOrDefault();
            DiffRawText = DiffText = result.RawText;
            CanShowRawDiff = !string.IsNullOrWhiteSpace(result.RawText);
            DiffSummaryText = file.Path + " · " + result.Summary;
            return SelectedDiffFile;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { if (request.IsCurrent) StatusText = "无法读取差异：" + error.Message; return null; }
    }

	public void ToggleRawDiff()
	{
		if (CanShowRawDiff)
		{
			ShowRawDiff = !ShowRawDiff;
			RawDiffToggleText = (ShowRawDiff ? "返回易懂说明" : "查看原始差异");
		}
	}

	public Task<GitOperationResult> DiscardChangeAsync(FileChange change)
	{
		return DiscardChangesAsync(new global::_003C_003Ez__ReadOnlySingleElementList<FileChange>(change));
	}

	public async Task<GitOperationResult> DiscardChangesAsync(IReadOnlyList<FileChange> changes)
	{
		using var operationContext = requests.Capture();
		string[] array = (from change in changes
			where !change.IsStaged
			select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			throw new InvalidOperationException("请从未暂存修改列表中选择至少一个要丢弃的文件。");
		}
		string currentFullPath = ((!editorState.CurrentDocumentIsHistorical && (object)CurrentDocument != null) ? Path.GetFullPath(CurrentDocument.Path) : null);
		bool refreshEditor = currentFullPath != null && array.Any((string path) => Path.GetFullPath(Path.Combine(operationContext.RepositoryPath, path)).Equals(currentFullPath, StringComparison.OrdinalIgnoreCase));
		if (refreshEditor && !await PrepareForDocumentTransitionAsync("丢弃当前文件的修改"))
		{
			return CanceledOperation("discard");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("DiscardChangesAsync", string.Empty);
		GitOperationResult result = await git.DiscardFilesAsync(operationContext.RepositoryPath, array, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		if (result.Success)
		{
			SelectedChange = null;
			ClearDiffPresentation();
			DiffContextText = "工作区差异";
			if (refreshEditor && currentFullPath != null)
			{
				if (File.Exists(currentFullPath))
				{
					await OpenFileAsync(currentFullPath);
				}
				else
				{
					ClearCurrentDocument();
				}
			}
		}
		return result;
	}

	public async Task<GitOperationResult> ResolveSelectedBinaryConflictAsync(ConflictSide side)
	{
		using var operationContext = requests.Capture();
		ConflictFile conflictFile = SelectedConflict;
		if ((object)conflictFile == null || !conflictFile.IsBinary)
		{
			throw new InvalidOperationException("请先选择一个二进制冲突文件。");
		}
		if (IsCurrentDocumentPath(conflictFile.Path) &&
			!await PrepareForDocumentTransitionAsync("解决当前二进制文件的冲突"))
		{
			return CanceledOperation("binary-conflict-resolve");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ResolveSelectedBinaryConflictAsync", string.Empty);
		GitOperationResult result = await git.ResolveBinaryConflictAsync(operationContext.RepositoryPath, conflictFile.Path, side, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public Task<IReadOnlyList<RecoveryPoint>> GetRecoveryPointsAsync()
	{
		return recoveryService.ListAsync(ActiveRepositoryPath);
	}

	public async Task<GitOperationResult> RestoreRecoveryPointAsync(RecoveryPoint point)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("恢复工作区"))
		{
			return CanceledOperation("restore");
		}
		if (!Path.GetFullPath(point.RepositoryPath).Equals(Path.GetFullPath(operationContext.RepositoryPath), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("恢复点不属于当前仓库。");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("RestoreRecoveryPointAsync", string.Empty);
		GitOperationResult result = await recoveryService.RestoreAsync(point, operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		try { await ReloadAllAsync(operationContext); }
		catch (Exception exception)
		{
			result = result with { Warnings = result.Warnings.Concat(new[] { "刷新仓库视图失败：" + exception.Message }).ToArray() };
		}
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		DetailsText = string.Join(Environment.NewLine, result.Details);
		if (!result.Success)
			StatusText += $"（阶段：{result.ExecutionStage}；恢复前保护点：{result.RecoveryPointId ?? "尚未创建"}）";
		return result;
	}

	public async Task<GitOperationResult> DeleteRecoveryPointAsync(RecoveryPoint point)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("DeleteRecoveryPointAsync", string.Empty);
		GitOperationResult result = await recoveryService.DeleteAsync(point);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		return result;
	}

	public async Task<GitOperationResult> ConfigureRemoteAsync(string? originalName, string name, string url)
	{
		using var operationContext = requests.Capture();
		GitOperationResult gitOperationResult = ((originalName != null) ? (await git.UpdateRemoteAsync(operationContext.RepositoryPath, originalName, name, url, cancellationToken: operationContext.Token)) : (await git.AddRemoteAsync(operationContext.RepositoryPath, name, url, cancellationToken: operationContext.Token)));
		GitOperationResult result = gitOperationResult;
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> RemoveRemoteAsync(string name)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("RemoveRemoteAsync", string.Empty);
		GitOperationResult result = await git.RemoveRemoteAsync(operationContext.RepositoryPath, name, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await RefreshAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task CreateFileAsync(string parentDirectory, string name, bool directory)
	{
		using var writeContext = requests.Capture();
		try
		{
		ValidateLeafName(name);
		string path = Path.Combine(parentDirectory, name);
		if (!directory)
		{
			await writeContext.Await(files.CreateFileAsync(writeContext.RepositoryPath, path, cancellationToken: writeContext.Token));
		}
		else
		{
			await writeContext.Await(files.CreateDirectoryAsync(writeContext.RepositoryPath, path, cancellationToken: writeContext.Token));
		}
		await writeContext.Await(RefreshAsync());
		}
		catch (OperationCanceledException) { }
	}

	public Task<IReadOnlyList<SystemNewFileType>> GetSystemNewFileTypesAsync()
	{
		return systemNewFiles.GetAvailableTypesAsync();
	}

	public async Task CreateSystemFileAsync(string parentDirectory, string name, SystemNewFileType type)
	{
		using var writeContext = requests.Capture();
		try
		{
		ValidateLeafName(name);
		await writeContext.Await(systemNewFiles.CreateAsync(writeContext.RepositoryPath, Path.Combine(parentDirectory, name), type.Id, cancellationToken: writeContext.Token));
		await writeContext.Await(RefreshAsync());
		}
		catch (OperationCanceledException) { }
	}

	public async Task MoveFileAsync(string source, string newName)
	{
		using var writeContext = requests.Capture();
		try
		{
		ValidateLeafName(newName);
		bool affectsCurrent = PathContainsCurrentDocument(source);
		if (affectsCurrent && !await writeContext.Await(PrepareForDocumentTransitionAsync("重命名当前文件")))
		{
			return;
		}
		string destination = Path.Combine(Path.GetDirectoryName(source) ?? writeContext.RepositoryPath, newName);
		string currentPath = affectsCurrent && CurrentDocument != null ? CurrentDocument.Path : null;
		string relocatedCurrentPath = null;
		if (currentPath != null)
		{
			string relative = Path.GetRelativePath(source, currentPath);
			relocatedCurrentPath = relative.Equals(".", StringComparison.Ordinal)
				? destination
				: Path.Combine(destination, relative);
		}
		await writeContext.Await(files.MoveAsync(writeContext.RepositoryPath, source, destination, cancellationToken: writeContext.Token));
		if (currentPath != null && relocatedCurrentPath != null)
		{
			await draftStore.MoveAsync(writeContext.RepositoryPath, currentPath, relocatedCurrentPath);
			await writeContext.Await(OpenFileAsync(relocatedCurrentPath));
		}
		await writeContext.Await(RefreshAsync());
		}
		catch (OperationCanceledException) { }
	}

	public async Task DeleteFileAsync(string path)
	{
		using var writeContext = requests.Capture();
		try
		{
		bool affectsCurrent = PathContainsCurrentDocument(path);
		if (affectsCurrent && !await writeContext.Await(PrepareForDocumentTransitionAsync("删除当前文件")))
		{
			return;
		}
		string currentPath = affectsCurrent && CurrentDocument != null ? CurrentDocument.Path : null;
		await writeContext.Await(files.DeleteAsync(writeContext.RepositoryPath, path, cancellationToken: writeContext.Token));
		if (currentPath != null)
		{
			await draftStore.DeleteAsync(writeContext.RepositoryPath, currentPath);
			writeContext.Check();
			ClearCurrentDocument();
		}
		await writeContext.Await(RefreshAsync());
		}
		catch (OperationCanceledException) { }
	}

	public IReadOnlyList<string> GetRemoteBranchNames(RemoteInfo remote)
	{
		string prefix = remote.Name + "/";
		return (from branch in Branches
			where branch.IsRemote && branch.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			select branch.FriendlyName.Substring(prefix.Length) into name
			where !name.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
			select name).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string name) => name, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public async Task<GitOperationResult> CreateTagAsync(string name, string? targetId = null, GitTagType tagType = GitTagType.Lightweight, string? message = null)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("CreateTagAsync", string.Empty);
		GitOperationResult result = await git.CreateTagAsync(operationContext.RepositoryPath, name.Trim(), (!string.IsNullOrWhiteSpace(targetId)) ? targetId : Head?.CommitId, tagType, message, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> DeleteTagAsync(string name)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("DeleteTagAsync", string.Empty);
		GitOperationResult result = await git.DeleteTagAsync(operationContext.RepositoryPath, name, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public Task<IReadOnlyList<StashInfo>> GetStashesAsync()
	{
		return git.GetStashesAsync(ActiveRepositoryPath);
	}

	public async Task<GitOperationResult> SaveStashAsync(string message)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("保存当前现场"))
		{
			return CanceledOperation("stash");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("SaveStashAsync", string.Empty);
		GitOperationResult result = await git.SaveStashAsync(operationContext.RepositoryPath, message, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> ApplyStashAsync(int index, bool pop)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync(pop ? "弹出暂存现场" : "应用暂存现场"))
		{
			return CanceledOperation("stash");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("ApplyStashAsync", string.Empty);
		GitOperationResult result = await git.ApplyStashAsync(operationContext.RepositoryPath, index, pop, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> DeleteStashAsync(int index)
	{
		using var operationContext = requests.Capture();
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("DeleteStashAsync", string.Empty);
		GitOperationResult result = await git.DeleteStashAsync(operationContext.RepositoryPath, index, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public async Task<GitOperationResult> RebaseOntoAsync(string upstreamBranch, string? ontoBranch = null)
	{
		using var operationContext = requests.Capture();
		if (!await PrepareForDocumentTransitionAsync("变基当前分支"))
		{
			return CanceledOperation("rebase");
		}
		if (!operationContext.IsCurrent) return GitOperationResult.Canceled("RebaseOntoAsync", string.Empty);
		GitOperationResult result = await git.RebaseOntoAsync(operationContext.RepositoryPath, upstreamBranch, string.IsNullOrWhiteSpace(ontoBranch) ? null : ontoBranch, cancellationToken: operationContext.Token);
		if (!operationContext.IsCurrent) return result;
		ShowResult(result);
		await ReloadAllAsync();
		if (!operationContext.IsCurrent) return result;
		return result;
	}

	public GitOperationPreview Preview(string operation, params string[] affected)
	{
		return git.Preview(operation, affected);
	}

	private async Task<bool> OpenFileAsync(string path, RequestContext? parent = null)
	{
		using var ownedRequest = parent is null ? requests.Begin("navigation") : null;
		var request = parent ?? ownedRequest!;
		try
		{
		path = Path.GetFullPath(path);
		if (!editorState.CurrentDocumentIsHistorical && CurrentDocument != null &&
			Path.GetFullPath(CurrentDocument.Path).Equals(path, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!await request.Await(PrepareForDocumentTransitionAsync("切换文件")))
		{
			return false;
		}
		try
		{
			TextDocument document;
			bool externalOnly;
			if (IsExternalDocumentPath(path))
			{
				FileInfo fileInfo = new FileInfo(path);
				if (!fileInfo.Exists)
				{
					throw new FileNotFoundException("文件不存在。", path);
				}
				document = new TextDocument(path, string.Empty, "binary", Environment.NewLine, fileInfo.LastWriteTimeUtc, IsReadOnly: true, IsBinary: true, fileInfo.Length);
				externalOnly = true;
			}
			else
			{
				document = await request.Await(files.OpenTextAsync(path, cancellationToken: request.Token));
				externalOnly = document.Size > GitVisualizer.Infrastructure.FileSystem.TextFileStorage.EditLimit || document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(path));
			}

			string editorValue = externalOnly ? string.Empty : document.Text;
			bool restoreDraft = false;
			if (!externalOnly && !document.IsReadOnly && HasRepository)
			{
				EditorDraft draft = await request.Await(draftStore.LoadAsync(request.RepositoryPath, path, cancellationToken: request.Token));
				if (draft != null)
				{
					if (string.Equals(draft.Text, document.Text, StringComparison.Ordinal))
					{
						await request.Await(draftStore.DeleteAsync(request.RepositoryPath, path, cancellationToken: request.Token));
					}
					else
					{
						EditorSafetyAction action = await request.Await(editorInteraction.ResolveDraftAsync(draft, cancellationToken: request.Token));
						if (action == EditorSafetyAction.Cancel)
						{
							return false;
						}
						if (action == EditorSafetyAction.Discard)
						{
							await request.Await(draftStore.DeleteAsync(request.RepositoryPath, path, cancellationToken: request.Token));
						}
						else if (action == EditorSafetyAction.Restore)
						{
							var verified = await request.Await(files.OpenTextAsync(path, cancellationToken: request.Token));
							if (verified.IsReadOnly || verified.OriginalByteDigest != document.OriginalByteDigest)
								throw new ExternalFileChangedException(path);
							// Old drafts have no byte metadata: a fresh strict decode is mandatory.
							if (draft.OriginalByteDigest is not null && draft.OriginalByteDigest != verified.OriginalByteDigest)
								document = verified with { OriginalByteDigest = draft.OriginalByteDigest };
							else document = verified;
							editorValue = draft.Text;
							restoreDraft = true;
						}
					}
				}
			}

			CancelScheduledDraftSave();
			editorState.CurrentDocumentIsHistorical = false;
			editorState.CurrentHistoricalCommitId = null;
			editorState.CurrentHistoricalRelativePath = null;
			CurrentDocument = document;
			IsExternalOnlyDocument = externalOnly;
			CanSaveCurrentDocument = !document.IsReadOnly && !externalOnly;
			CanOpenCurrentDocumentExternally = externalOnly || document.IsReadOnly;
			ExternalDocumentHint = document.ReadOnlyReason ?? "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
			EditorText = editorValue;
			HasUnsavedEditorChanges = restoreDraft;
			return true;
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return false;
			StatusText = "无法打开文件：" + ex.Message;
			return false;
		}
		}
		catch (OperationCanceledException) { return false; }
	}

	private async Task<bool> OpenCommitFileAsync(string commitId, string relativePath, RequestContext? parent = null)
	{
		using var ownedRequest = parent is null ? requests.Begin("navigation") : null;
		var request = parent ?? ownedRequest!;
		try
		{
		if (!await request.Await(PrepareForDocumentTransitionAsync("浏览历史文件")))
		{
			return false;
		}
		try
		{
			TextDocument document = await request.Await(git.OpenCommitFileAsync(request.RepositoryPath, commitId, relativePath, cancellationToken: request.Token));
			CancelScheduledDraftSave();
			editorState.CurrentDocumentIsHistorical = true;
			editorState.CurrentHistoricalCommitId = commitId;
			editorState.CurrentHistoricalRelativePath = relativePath;
			CurrentDocument = document;
			IsExternalOnlyDocument = document.Size > GitVisualizer.Infrastructure.FileSystem.TextFileStorage.EditLimit || document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(relativePath));
			CanSaveCurrentDocument = false;
			CanOpenCurrentDocumentExternally = IsExternalOnlyDocument || document.IsReadOnly;
			ExternalDocumentHint = "这是历史提交中的只读文件。可导出只读副本并使用 Windows 默认程序打开。";
			EditorText = (IsExternalOnlyDocument ? string.Empty : document.Text);
			HasUnsavedEditorChanges = false;
			return true;
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return false;
			StatusText = "无法打开历史文件：" + ex.Message;
			return false;
		}
		}
		catch (OperationCanceledException) { return false; }
	}

	private async Task<bool> OpenHistoricalFileExternallyAsync(string commitId, string relativePath)
	{
		using var request = requests.Begin("external");
		try
		{
		try
		{
            string text = BuildHistoricalExportPath(request.RepositoryPath, commitId, relativePath);
            if (!File.Exists(text))
            {
                if (git is IHistoricalFileExportService exporter)
                    await request.Await(exporter.ExportCommitFileAsync(request.RepositoryPath, commitId, relativePath, text, request.Token));
                else
                {
                    var document = await request.Await(git.OpenCommitFileAsync(request.RepositoryPath, commitId, relativePath, request.Token));
                    if (document.ContentBytes is null) throw new InvalidOperationException("无法读取历史文件的原始内容。");
                    Directory.CreateDirectory(Path.GetDirectoryName(text)!);
                    await request.Await(File.WriteAllBytesAsync(text, document.ContentBytes, request.Token));
                }
            }
			new FileInfo(text).IsReadOnly = true;
			await request.Await(files.OpenExternalAsync(text, cancellationToken: request.Token));
			StatusText = "已打开 " + Path.GetFileName(relativePath) + " 的版本 " + commitId.Substring(0, Math.Min(8, commitId.Length)) + "（只读副本）";
			return true;
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception ex)
		{
			if (!request.IsCurrent) return false;
			StatusText = "无法打开历史版本文件：" + ex.Message;
			return false;
		}
		}
		catch (OperationCanceledException) { return false; }
	}

	private static string BuildHistoricalExportPath(string repositoryPath, string commitId, string relativePath)
	{
		static string Hash(string value)
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 16);
		}
		string fileName = Path.GetFileName(relativePath);
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		string text = new string(fileName.Select((char character) => invalidFileNameChars.Contains(character) ? '_' : character).ToArray());
		string text2 = new string(commitId.Where(char.IsLetterOrDigit).Take(40).ToArray());
		return Path.Combine(Path.GetTempPath(), "GitVisualizer", "historical-files", Hash(Path.GetFullPath(repositoryPath)), text2, Hash(relativePath.Replace('\\', '/')), text);
	}

	private async Task<RemoteCredential?> GetRemoteCredentialAsync(RemoteInfo? remote)
	{
		if ((object)remote == null)
		{
			return null;
		}
		return await RemoteCredentialResolver.ResolveAsync(remote.FetchUrl, credentialVault);
	}

	private void LoadDiffPresentation(DiffPresentation presentation, bool isCommitComparison)
	{
		DiffFiles.Clear();
		DiffRegions.Clear();
		DiffHunks.Clear();
		Replace(DiffFiles, presentation.Files);
		SelectedDiffFile = presentation.Files.FirstOrDefault();
		if (!isCommitComparison && (object)SelectedDiffFile != null)
		{
			Replace(DiffRegions, SelectedDiffFile.Regions);
			Replace(DiffHunks, SelectedDiffFile.Regions.Select((DiffRegionPresentation region) => region.SourceHunk).OfType<DiffHunk>());
		}
		DiffContextText = presentation.Title;
		DiffSummaryText = presentation.Summary;
		DiffRawText = presentation.RawText;
		DiffText = presentation.RawText;
		HasDiffHunks = DiffHunks.Count > 0;
		CanShowRawDiff = !string.IsNullOrWhiteSpace(DiffRawText);
		ShowRawDiff = false;
		RawDiffToggleText = "查看原始差异";
		ShowWorkingDiffCards = !isCommitComparison && presentation.HasFiles;
		ShowCommitDiffCards = isCommitComparison && presentation.HasFiles;
		ShowDiffEmptyState = !presentation.HasFiles;
	}

	private void ClearDiffPresentation()
	{
		DiffFiles.Clear();
		DiffRegions.Clear();
		DiffHunks.Clear();
		SelectedDiffFile = null;
		DiffText = string.Empty;
		DiffRawText = string.Empty;
		DiffSummaryText = "请选择一个有变化的文件。";
		HasDiffHunks = false;
		CanShowRawDiff = false;
		ShowRawDiff = false;
		RawDiffToggleText = "查看原始差异";
		ShowWorkingDiffCards = false;
		ShowCommitDiffCards = false;
		ShowDiffEmptyState = true;
	}

	private void ClearCurrentDocument()
	{
		CancelScheduledDraftSave();
		CurrentDocument = null;
		EditorText = string.Empty;
		editorState.CurrentDocumentIsHistorical = false;
		HasUnsavedEditorChanges = false;
		IsExternalOnlyDocument = false;
		CanSaveCurrentDocument = false;
		CanOpenCurrentDocumentExternally = false;
	}

	private async Task SynchronizeCurrentDocumentWithDiskAsync()
	{
		TextDocument document = CurrentDocument;
		if (document == null || HasUnsavedEditorChanges)
		{
			return;
		}
		if (editorState.CurrentDocumentIsHistorical)
		{
			if (!IsBrowsingHistoricalCommit)
			{
				ClearCurrentDocument();
			}
			return;
		}
		try
		{
			await ReloadCurrentDocumentFromDiskAsync(document.Path, deleteDraft: true, discardUnsaved: false);
		}
		catch (IOException ex)
		{
			StatusText = "仓库已刷新，但无法重新载入当前文件：" + ex.Message;
		}
		catch (UnauthorizedAccessException ex)
		{
			StatusText = "仓库已刷新，但无法重新载入当前文件：" + ex.Message;
		}
	}

	private async Task ReloadCurrentDocumentFromDiskAsync(
		string path,
		bool deleteDraft,
		bool discardUnsaved = true)
	{
		using var request = requests.Capture();
		var documentAtRequest = CurrentDocument;
		try
		{
		path = Path.GetFullPath(path);
		TextDocument original = CurrentDocument;
		if (original == null || !Path.GetFullPath(original.Path).Equals(path, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!File.Exists(path))
		{
			if (discardUnsaved || !HasUnsavedEditorChanges)
			{
				if (deleteDraft)
				{
					await request.Await(draftStore.DeleteAsync(request.RepositoryPath, path, cancellationToken: request.Token));
				}
				ClearCurrentDocument();
			}
			return;
		}

		TextDocument document = await request.Await(files.OpenTextAsync(path, cancellationToken: request.Token));
		if (CurrentDocument == null ||
			!Path.GetFullPath(CurrentDocument.Path).Equals(path, StringComparison.OrdinalIgnoreCase) ||
			(!discardUnsaved && HasUnsavedEditorChanges))
		{
			return;
		}

		if (!ReferenceEquals(CurrentDocument, documentAtRequest)) return;
		CancelScheduledDraftSave();
		editorState.CurrentDocumentIsHistorical = false;
		editorState.CurrentHistoricalCommitId = null;
		editorState.CurrentHistoricalRelativePath = null;
		CurrentDocument = document;
		IsExternalOnlyDocument = document.Size > GitVisualizer.Infrastructure.FileSystem.TextFileStorage.EditLimit || document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(path));
		CanSaveCurrentDocument = !document.IsReadOnly && !IsExternalOnlyDocument;
		CanOpenCurrentDocumentExternally = IsExternalOnlyDocument || document.IsReadOnly;
		ExternalDocumentHint = document.ReadOnlyReason ?? "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
		EditorText = IsExternalOnlyDocument ? string.Empty : document.Text;
		HasUnsavedEditorChanges = false;
		if (deleteDraft)
		{
			await request.Await(draftStore.DeleteAsync(request.RepositoryPath, path, cancellationToken: request.Token));
		}
		}
		catch (OperationCanceledException) { return; }
	}

	private static bool IsSsh(string url)
	{
		if (!url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
		{
			if (url.Contains('@', StringComparison.Ordinal))
			{
				return url.Contains(':', StringComparison.Ordinal);
			}
			return false;
		}
		return true;
	}

	private async Task ReloadAllAsync(RequestContext? operationContext = null)
	{
		if (operationContext is not null && !operationContext.IsCurrent) return;
		using var request = requests.Begin("reload");
		try
		{
		operationContext?.Check();
		requests.Invalidate("navigation");
		fileTreeState.Invalidate();
		SelectedCommit = null;
		SelectedBranch = null;
		SelectedHistoryBranchName = string.Empty;
		HistoryContextText = "全部分支";
		DetailsText = string.Empty;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		await request.Await(RefreshAsync());
		operationContext?.Check();
		History.Clear();
		ResetHistoryPagination();
		await request.Await(LoadMoreHistoryAsync());
		operationContext?.Check();
		}
		catch (OperationCanceledException) { return; }
	}

	private async Task ApplySnapshotAsync(RepositorySnapshot snapshot, RequestContext request, RepositoryChangeKind changes = RepositoryChangeKind.All)
	{
		var reloadReferences = lastSnapshot is null || (changes & RepositoryChangeKind.References) != 0;
        var events = reloadReferences ? await request.Await(git.GetHistoryEventsAsync(request.RepositoryPath, request.Token)) : HistoryEvents.ToArray();
        var logs = changes == RepositoryChangeKind.All ? await request.Await(logStore.GetRecentAsync(request.RepositoryPath, 100, request.Token)) : OperationLog.ToArray();
		var refreshedConflicts = await request.Await(git is IConflictDetailsService details
            ? details.GetConflictMetadataAsync(request.RepositoryPath, request.Token)
            : git.GetConflictsAsync(request.RepositoryPath, request.Token));
		request.Check();

        var selectedChangePath = SelectedChange?.Path;
        var selectedChangeStaged = SelectedChange?.IsStaged;
        var selectedBranchName = SelectedBranch?.CanonicalName;
		string selectedRemoteName = SelectedRemote?.Name;
		Head = snapshot.Head;
		CurrentBranch = (snapshot.Head.IsDetached ? ("游离 HEAD · " + snapshot.Head.CommitId.Substring(0, Math.Min(8, snapshot.Head.CommitId.Length))) : ("HEAD → " + snapshot.Head.BranchName));
		Replace(Branches, snapshot.Branches);
		Replace(Tags, snapshot.Tags);
		ObservableCollection<GitHistoryEvent> historyEvents = HistoryEvents;
		Replace(historyEvents, events);
		Replace(Remotes, snapshot.Remotes);
		BranchInfo? branchInfo = snapshot.Branches.FirstOrDefault((BranchInfo branch) => branch.IsCurrent);
		object obj;
		if ((object)branchInfo == null)
		{
			obj = null;
		}
		else
		{
			string? trackedBranch = branchInfo.TrackedBranch;
			obj = ((trackedBranch != null) ? trackedBranch.Split('/', 2)[0] : null);
		}
		string trackedRemoteName = (string)obj;
		SelectedRemote = Remotes.FirstOrDefault((RemoteInfo remote) => remote.Name.Equals(selectedRemoteName, StringComparison.OrdinalIgnoreCase)) ?? Remotes.FirstOrDefault((RemoteInfo remote) => remote.Name.Equals(trackedRemoteName, StringComparison.OrdinalIgnoreCase)) ?? Remotes.FirstOrDefault((RemoteInfo remote) => remote.Name.Equals("origin", StringComparison.OrdinalIgnoreCase)) ?? Remotes.FirstOrDefault();
		Replace(UnstagedChanges, snapshot.Changes.Where((FileChange change) => !change.IsStaged));
		Replace(StagedChanges, snapshot.Changes.Where((FileChange change) => change.IsStaged));
        if (selectedChangePath is not null)
            SelectedChange = UnstagedChanges.Concat(StagedChanges).FirstOrDefault(x => x.Path == selectedChangePath
                && x.IsStaged == selectedChangeStaged) ?? UnstagedChanges.Concat(StagedChanges).FirstOrDefault(x => x.Path == selectedChangePath);
        if (selectedBranchName is not null) SelectedBranch = Branches.FirstOrDefault(x => x.CanonicalName == selectedBranchName);
		Replace(Notices, snapshot.Features.Notices);
		if (!IsBrowsingHistoricalCommit)
		{
			await request.Await(BuildFileTreeAsync(snapshot.WorkingDirectory, request.Token));
		}
		ObservableCollection<OperationLogEntry> operationLog = OperationLog;
		Replace(operationLog, logs);
		SelectedOperationLog = OperationLog.FirstOrDefault(x => x.Id == SelectedOperationLog?.Id) ?? OperationLog.FirstOrDefault();
		ConflictFile conflictBeforeRefresh = SelectedConflict;
		string selectedConflictPath = SelectedConflict?.Path;
		string selectedConflictResultText = ConflictResultText;
		bool preserveEditedConflictResult = SelectedConflict != null &&
			!string.Equals(
				selectedConflictResultText,
				SelectedConflict.ResultText,
				StringComparison.Ordinal);
		ObservableCollection<ConflictFile> conflicts = Conflicts;
		// Keep the original byte snapshot with its draft; refreshing must not authorize an overwrite.
		Replace(conflicts, refreshedConflicts.Select(item =>
			preserveEditedConflictResult && item.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase)
				? conflictBeforeRefresh! : item));
		ConflictFile? refreshedConflict = Conflicts.FirstOrDefault((ConflictFile conflict) => conflict.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase)) ?? Conflicts.FirstOrDefault();
		await SelectConflictAsync(refreshedConflict, request);
        request.Check();
		if (preserveEditedConflictResult && conflictState.SelectedConflict != null &&
			conflictState.SelectedConflict.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase))
		{
			ConflictResultText = selectedConflictResultText;
		}
		UpdateConflictState(snapshot.OperationState);
		StatusText = $"{snapshot.Changes.Count} 个变化 · {snapshot.Branches.Count} 个分支 · 刷新于 {snapshot.RefreshedAt:HH:mm:ss}";
		if (reloadReferences && lastSnapshot is not null &&
            (!Equals(lastSnapshot.Head, snapshot.Head) || !lastSnapshot.Branches.SequenceEqual(snapshot.Branches)
                || !lastSnapshot.Tags.SequenceEqual(snapshot.Tags)))
        {
            await ReloadHistoryPreservingSelectionAsync(request);
        }
        else if (History.Count == 0)
        {
            ResetHistoryPagination();
			await LoadMoreHistoryAsync();
		}
	}

    private async Task ReloadHistoryPreservingSelectionAsync(RequestContext request)
    {
        var wanted = Math.Max(200, History.Count);
        var selectedId = SelectedCommit?.Id;
        ResetHistoryPagination();
        var updated = new List<CommitNode>(wanted);
        bool more;
        do
        {
            var page = await request.Await(string.IsNullOrEmpty(SelectedHistoryBranchName)
                ? git.GetHistoryAsync(request.RepositoryPath, updated.Count, 201, request.Token)
                : git.GetBranchHistoryAsync(request.RepositoryPath, SelectedHistoryBranchName, updated.Count, 201, request.Token));
            updated.AddRange(page.Take(200));
            more = page.Count > 200;
        } while (more && updated.Count < wanted);
        request.Check();
        Replace(History, updated);
        historyState.HistoryLoaded = updated.Count;
        HasLoadedHistory = true;
        HasMoreHistory = more;
        if (selectedId is not null && History.FirstOrDefault(x => x.Id == selectedId) is { } selected)
            SelectedCommit = selected;
    }

    private async Task BuildFileTreeAsync(string root, CancellationToken token)
    {
        using var measurement = PerformanceRecorder.Begin(PerformanceOperation.FileTreeBuild);
        await FileTreeItem.UpdateDirectoryAsync(FileTree, root, token);
    }

    private static Task<FileTreeItem[]> CreateHistoricalNodesAsync(ICommitDirectoryService service,
        string root, string commitId, IReadOnlyList<CommitTreeEntry> entries, CancellationToken token) => Task.Run(() =>
    {
        return entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry =>
            {
                token.ThrowIfCancellationRequested();
                var node = new FileTreeItem
                {
                    Name = Path.GetFileName(entry.Path), FullPath = Path.Combine(root, entry.Path),
                    RelativePath = entry.Path, CommitId = commitId, IsDirectory = entry.IsDirectory, LifetimeToken = token,
                    ChildLoader = async cancellation => await CreateHistoricalNodesAsync(service, root, commitId,
                        await service.GetCommitDirectoryAsync(root, commitId, entry.Path, cancellation), cancellation)
                };
                if (entry.IsDirectory) node.Children.Add(new FileTreeItem { Name = "展开以加载…", FullPath = node.FullPath,
                    IsDirectory = false, IsPlaceholder = true });
                return node;
            }).ToArray();
    }, token);

	private void BuildCommitFileTree(string commitId, IReadOnlyList<CommitTreeEntry> entries)
	{
		FileTree.Clear();
		Dictionary<string, CommitTreeEntry[]> byParent = entries.GroupBy<CommitTreeEntry, string>(delegate(CommitTreeEntry entry)
		{
			int num = entry.Path.LastIndexOf('/');
			return (num >= 0) ? entry.Path.Substring(0, num) : string.Empty;
		}, StringComparer.Ordinal).ToDictionary<IGrouping<string, CommitTreeEntry>, string, CommitTreeEntry[]>((IGrouping<string, CommitTreeEntry> group) => group.Key, (IGrouping<string, CommitTreeEntry> group) => group.ToArray(), StringComparer.Ordinal);
		AddCommitTreeChildren(FileTree, string.Empty, commitId, byParent);
	}

	private void AddCommitTreeChildren(ObservableCollection<FileTreeItem> destination, string parentPath, string commitId, IReadOnlyDictionary<string, CommitTreeEntry[]> byParent)
	{
		if (!byParent.TryGetValue(parentPath, out CommitTreeEntry[] value))
		{
			return;
		}
		foreach (CommitTreeEntry item in value.OrderByDescending((CommitTreeEntry item) => item.IsDirectory).ThenBy<CommitTreeEntry, string>((CommitTreeEntry item) => Path.GetFileName(item.Path), StringComparer.CurrentCultureIgnoreCase))
		{
			FileTreeItem fileTreeItem = new FileTreeItem
			{
				Name = Path.GetFileName(item.Path),
				FullPath = Path.Combine(ActiveRepositoryPath, item.Path.Replace('/', Path.DirectorySeparatorChar)),
				RelativePath = item.Path,
				CommitId = commitId,
				IsDirectory = item.IsDirectory
			};
			destination.Add(fileTreeItem);
			if (item.IsDirectory)
			{
				AddCommitTreeChildren(fileTreeItem.Children, item.Path, commitId, byParent);
			}
		}
	}

	private void AttachWatcher(string path)
	{
		watcher?.Dispose();
		watcher = watcherFactory.Create(path);
		var watcherContext = requests.Capture();
        var eventGate = new object();
        var dispatchQueued = false;
        var dirty = RepositoryChangeKind.None;
        watcher.RepositoryChanged += (_, args) =>
        {
            if (Application.Current?.Dispatcher is not { } dispatcher) return;
            lock (eventGate)
            {
                dirty |= (args as RepositoryChangedEventArgs)?.Changes ?? RepositoryChangeKind.All;
                if (dispatchQueued) return;
                dispatchQueued = true;
            }
            dispatcher.BeginInvoke(new Action(async () =>
            {
                RepositoryChangeKind changes;
                lock (eventGate) { changes = dirty; dirty = RepositoryChangeKind.None; dispatchQueued = false; }
                if (watcherContext.IsCurrent) await QueueRefreshAsync(changes);
            }));
        };
		watcher.Start();
	}

    public Task RevalidateOnFocusAsync()
    {
        var current = watcher;
        return current is GitVisualizer.Infrastructure.FileSystem.RepositoryWatcher concrete ? Task.Run(concrete.Revalidate) : Task.CompletedTask;
    }

	private async Task RememberRepositoryAsync(string path)
	{
		using var rememberContext = requests.Capture();
		string existing = RecentRepositories.FirstOrDefault((string item) => item.Equals(path, StringComparison.OrdinalIgnoreCase));
		if (existing == null)
		{
			RecentRepositories.Add(path);
			repositoryInsertionOrder[path] = nextRepositoryOrder++;
			existing = path;
		}
		while (RecentRepositories.Count > 20)
		{
			string text = RecentRepositories.Where((string item) => !item.Equals(existing, StringComparison.OrdinalIgnoreCase)).MinBy((string item) => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue));
			if (text == null)
			{
				break;
			}
			RecentRepositories.Remove(text);
			repositoryInsertionOrder.Remove(text);
		}
		settings = settings with
		{
			RecentRepositories = RecentRepositories.OrderBy((string item) => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue)).ToArray(),
			LastRepository = path
		};
		await settingsStore.SaveAsync(settings);
		rememberContext.Check();
		await SortRepositoriesAsync(RepositorySortMode);
		rememberContext.Check();
		SelectedRepository = existing;
	}

	private void ResetHistoryPagination()
	{
        if (git is IHistorySessionService sessionService) sessionService.ResetHistorySession();
		requests.Invalidate("history");
		historyState.HistoryLoaded = 0;
		HasLoadedHistory = false;
		HasMoreHistory = false;
	}

	private void ResetRepositoryView(string path)
	{
		requests.Switch(path);
        refreshTask = null;
        pendingChanges = RepositoryChangeKind.None;
        lastSnapshot = null;
		IsCloning = false;
		IsPulling = false;
		IsBusy = false;
		watcher?.Dispose();
		watcher = null;
		CancelScheduledDraftSave();
		refreshCancellation.Cancel();
		refreshCancellation.Dispose();
		refreshCancellation = new CancellationTokenSource();
		ActiveRepositoryPath = path;
		SelectedRepository = path;
		HasRepository = false;
		CurrentBranch = "正在打开仓库…";
		Head = null;
		StatusText = "正在加载 " + path;
		ResetHistoryPagination();
		SelectedBranch = null;
		SelectedHistoryBranchName = string.Empty;
		HistoryContextText = "全部分支";
		History.Clear();
		Branches.Clear();
		Tags.Clear();
		HistoryEvents.Clear();
		Remotes.Clear();
		SelectedRemote = null;
		UnstagedChanges.Clear();
		StagedChanges.Clear();
		FileTree.Clear();
		OperationLog.Clear();
		Conflicts.Clear();
		ClearDiffPresentation();
		SelectConflict(null);
		UpdateConflictState(RepositoryOperationState.None);
		Notices.Clear();
		SelectedCommit = null;
		SelectedOperationLog = null;
		SelectedChange = null;
		SelectedRightTabIndex = 1;
		CurrentDocument = null;
		editorState.CurrentDocumentIsHistorical = false;
		HasUnsavedEditorChanges = false;
		IsExternalOnlyDocument = false;
		CanSaveCurrentDocument = false;
		CanOpenCurrentDocumentExternally = false;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		fileTreeState.Invalidate();
		EditorText = string.Empty;
		DetailsText = string.Empty;
		ConflictBaseText = string.Empty;
		ConflictOursText = string.Empty;
		ConflictTheirsText = string.Empty;
		ConflictResultText = string.Empty;
		EquivalentCommand = string.Empty;
	}

	private void ResetToEmptyRepositoryView()
	{
		ResetRepositoryView(string.Empty);
		SelectedRepository = null;
		CurrentBranch = "未打开仓库";
		StatusText = "拖入文件夹，或点击“打开仓库”开始";
		CommitMessage = string.Empty;
		DiffContextText = "工作区差异";
		SelectedRightTabIndex = 0;
		CanModifyFileTree = false;
		ExternalDocumentHint = "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
	}

	private void UpdateConflictState(RepositoryOperationState state)
	{
		bool num = HasConflicts;
		OperationState = state;
		HasConflicts = Conflicts.Count > 0;
		HasSelectedConflict = (object)SelectedConflict != null;
		bool supportedOperation = ConflictState.SupportsContinuation(state);
		CanAbortOperation = supportedOperation;
		CanContinueOperation = CanAbortOperation && !HasConflicts;
		string text = state == RepositoryOperationState.Bisect
			? "检测到 Git Bisect 状态。M0 仅展示状态，请在终端使用 git bisect good、bad、skip 或 reset；应用内继续/中止已禁用。"
			: ((state == RepositoryOperationState.None) ? ((!HasConflicts) ? "当前没有进行中的冲突操作。" : $"发现 {Conflicts.Count} 个冲突文件，请逐个处理。") : ((!HasConflicts) ? (OperationDisplayName(state) + "的冲突已全部解决，可以继续操作。") : $"{OperationDisplayName(state)}进行中 · 剩余 {Conflicts.Count} 个冲突文件"));
		ConflictStatusText = text;
		if (!num && HasConflicts)
		{
			ConflictDetected?.Invoke(this, new ConflictDetectedEventArgs(Conflicts.Count, OperationDisplayName(state)));
		}
	}

	private static string OperationDisplayName(RepositoryOperationState state)
	{
		return state switch
		{
			RepositoryOperationState.Merge => "合并", 
			RepositoryOperationState.Rebase => "变基", 
			RepositoryOperationState.CherryPick => "拣选提交", 
			RepositoryOperationState.Revert => "撤销提交", 
			RepositoryOperationState.Bisect => "二分查找", 
			RepositoryOperationState.Unknown => "Git 操作", 
			_ => "操作", 
		};
	}

	private async Task RunBusyAsync(Func<CancellationToken, Task> action)
	{
		if (IsBusy || requestsDisposed) return;
		using var context = requests.Capture();
		IsBusy = true;
		try { await action(context.Token); }
		catch (OperationCanceledException) { if (context.IsCurrent) StatusText = "操作已取消，请核对执行结果。"; }
		catch (Exception error) { if (context.IsCurrent) StatusText = error.Message; }
		finally { if (context.IsCurrent) IsBusy = false; }

	}

	private void ShowResult(GitOperationResult result)
	{
		StatusText = (result.Success ? result.Summary : (result.Summary + "：" + result.ErrorMessage));
		EquivalentCommand = result.EquivalentCommand;
		if (result.LogStatus == OperationLogStatus.Failed) StatusText += "（操作结果已保留，日志写入失败）";
	}

	private static RepositoryMetadata ReadRepositoryMetadata(string path, bool includeSize)
	{
		try
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(path);
			return new RepositoryMetadata(directoryInfo.CreationTimeUtc, directoryInfo.LastWriteTimeUtc, includeSize ? CalculateDirectorySize(path) : 0);
		}
		catch (IOException)
		{
			return RepositoryMetadata.Empty;
		}
		catch (UnauthorizedAccessException)
		{
			return RepositoryMetadata.Empty;
		}
	}

	private static long CalculateDirectorySize(string path)
	{
		long num = 0L;
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		foreach (string item in Directory.EnumerateFiles(path, "*", enumerationOptions))
		{
			try
			{
				num = checked(num + new FileInfo(item).Length);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (OverflowException)
			{
				return long.MaxValue;
			}
		}
		return num;
	}

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var incoming = source.ToArray();
        var keys = new HashSet<object>(incoming.Select(Key));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(Key(target[i]))) target.RemoveAt(i);
        var existing = target.ToDictionary(Key);
        for (int i = 0; i < incoming.Length; i++)
        {
            var value = incoming[i];
            var key = Key(value);
            if (i < target.Count && Equals(Key(target[i]), key))
            {
                if (!Equivalent(target[i], value)) target[i] = value;
            }
            else if (existing.TryGetValue(key, out var old))
            {
                var position = target.IndexOf(old);
                if (position >= 0) target.Move(position, i);
                if (!Equivalent(target[i], value)) target[i] = value;
            }
            else target.Insert(i, value);
        }
        while (target.Count > incoming.Length) target.RemoveAt(target.Count - 1);

        static object Key(T item) => item switch
        {
            FileChange change => (change.Path, change.IsStaged),
            BranchInfo branch => branch.CanonicalName,
            TagInfo tag => tag.Name,
            RemoteInfo remote => remote.Name,
            CommitNode commit => commit.Id,
            GitHistoryEvent historyEvent => historyEvent.Id,
            OperationLogEntry log => log.Id,
            ConflictFile conflict => conflict.Path,
            DiffFilePresentation file => file.Path,
            DiffRegionPresentation region => region.Id,
            DiffHunk hunk => hunk.Id,
            RecoveryPoint point => point.Id,
            StashInfo stash => stash.WorkTreeId,
            _ => item!
        };
        static bool Equivalent(T left, T right) => (left, right) switch
        {
            (CommitNode a, CommitNode b) => a with { ParentIds = b.ParentIds } == b && a.ParentIds.SequenceEqual(b.ParentIds),
            (RemoteInfo a, RemoteInfo b) => a.Name == b.Name && a.FetchUrl == b.FetchUrl && a.PushUrl == b.PushUrl
                && a.FetchRefSpecs.SequenceEqual(b.FetchRefSpecs) && a.PushRefSpecs.SequenceEqual(b.PushRefSpecs),
            (OperationLogEntry a, OperationLogEntry b) => a with { Details = b.Details } == b
                && (a.Details ?? []).SequenceEqual(b.Details ?? []),
            _ => EqualityComparer<T>.Default.Equals(left, right)
        };
    }

	public Task<bool> PrepareForCloseAsync()
	{
		return PrepareForDocumentTransitionAsync("退出程序");
	}

	private async Task<bool> PrepareForDocumentTransitionAsync(string reason)
	{
		await editorState.DocumentTransitionGate.WaitAsync();
		try
		{
			await editorState.EditorSaveGate.WaitAsync();
			editorState.EditorSaveGate.Release();

			TextDocument document = CurrentDocument;
			if (document == null || !CanSaveCurrentDocument || !HasUnsavedEditorChanges)
			{
				return true;
			}

			EditorSafetyAction action = await editorInteraction.ResolveUnsavedChangesAsync(document, reason);
			if (action == EditorSafetyAction.Save)
			{
				return await SaveCurrentDocumentAsync(refreshAfterSave: false);
			}
			if (action == EditorSafetyAction.Discard)
			{
				CancelScheduledDraftSave();
				await draftStore.DeleteAsync(ActiveRepositoryPath, document.Path);
				EditorText = document.Text;
				HasUnsavedEditorChanges = false;
				StatusText = "已放弃 " + Path.GetFileName(document.Path) + " 的未保存编辑";
				return true;
			}
			return false;
		}
		finally
		{
			editorState.DocumentTransitionGate.Release();
		}
	}

	private void ScheduleDraftSave()
	{
		CancelScheduledDraftSave();
		TextDocument document = CurrentDocument;
		if (!HasRepository || document == null || !CanSaveCurrentDocument || !HasUnsavedEditorChanges)
		{
			return;
		}
		_ = SaveDraftAfterDelayAsync(
			ActiveRepositoryPath, document, EditorText, editorState.DraftSaveCancellation.Token);
	}

	private async Task SaveDraftAfterDelayAsync(
		string repositoryPath,
		TextDocument document,
		string text,
		CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(750, cancellationToken);
			await draftStore.SaveAsync(new EditorDraft(
				repositoryPath,
				document.Path,
				text,
				document.LastWriteTime,
				DateTimeOffset.UtcNow, document.OriginalByteDigest, document.EncodingName, document.HasBom), cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			StatusText = "保存编辑草稿失败：" + ex.Message;
		}
	}

	private void CancelScheduledDraftSave()
	{
		editorState.CancelDraftSave();
	}

	public void Dispose()
	{
		if (requestsDisposed) return;
		requestsDisposed = true;
		requests.Dispose();
        if (git is IHistorySessionService sessionService) sessionService.ResetHistorySession();
		watcher?.Dispose();
		refreshCancellation.Cancel();
		refreshCancellation.Dispose();
		CancelScheduledDraftSave();
		editorState.DraftSaveCancellation.Dispose();
	}

	private bool IsCurrentDocument(string relativePath)
	{
		return HasUnsavedEditorChanges && IsCurrentDocumentPath(relativePath);
	}

	private bool IsCurrentDocumentPath(string relativePath) =>
		IsCurrentDocumentFullPath(Path.Combine(ActiveRepositoryPath, relativePath));

	private bool IsCurrentDocumentFullPath(string path) =>
		CurrentDocument != null && !editorState.CurrentDocumentIsHistorical &&
		Path.GetFullPath(path).Equals(Path.GetFullPath(CurrentDocument.Path), StringComparison.OrdinalIgnoreCase);

	private bool PathContainsCurrentDocument(string path)
	{
		if (CurrentDocument == null || editorState.CurrentDocumentIsHistorical)
		{
			return false;
		}

		string candidate = Path.GetFullPath(path);
		string documentPath = Path.GetFullPath(CurrentDocument.Path);
		if (candidate.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		string relative = Path.GetRelativePath(candidate, documentPath);
		return !Path.IsPathRooted(relative) &&
			!relative.Equals("..", StringComparison.Ordinal) &&
			!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
			!relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static void ValidateLeafName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
			Path.IsPathRooted(name) || !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
			name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
			name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) ||
			name.EndsWith(' ') || name.EndsWith('.'))
		{
			throw new ArgumentException("名称必须是合法的单个文件或文件夹名称。", nameof(name));
		}

		string stem = name.Split('.')[0];
		string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
		if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
			reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
		{
			throw new ArgumentException("该名称由 Windows 或 Git 保留，不能使用。", nameof(name));
		}
	}

	private static GitOperationResult CanceledOperation(string operation) =>
        GitOperationResult.Canceled(operation, string.Empty);

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnEditorTextChanged(string value)
	{
		HasUnsavedEditorChanges = editorState.IsModified(value);
		ScheduleDraftSave();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedOperationLogChanged(OperationLogEntry? value)
	{
		if ((object)value != null)
		{
			EquivalentCommand = value.EquivalentCommand;
		}
	}
}
