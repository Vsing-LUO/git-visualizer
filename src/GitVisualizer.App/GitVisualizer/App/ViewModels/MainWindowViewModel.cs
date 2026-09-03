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

	internal const int HistoryPageSize = 200;

	private static readonly HashSet<string> ExternalDocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".rtf", ".odt", ".ods",
		".odp", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
	};

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

	private CancellationTokenSource refreshCancellation = new CancellationTokenSource();

	private CancellationTokenSource draftSaveCancellation = new CancellationTokenSource();

	private readonly SemaphoreSlim editorSaveGate = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim documentTransitionGate = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);

	private AppSettings settings = AppSettings.Default;

	private int historyLoaded;

	private int repositorySortVersion;

	private int nextRepositoryOrder;

	private int fileTreeLoadVersion;

	private bool currentDocumentIsHistorical;

	private string? currentHistoricalCommitId;

	private string? currentHistoricalRelativePath;

	private readonly Dictionary<string, int> repositoryInsertionOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private string activeRepositoryPath = string.Empty;

	private string? selectedRepository;

	private string repositorySortMode = "修改时间";

	private string currentBranch = "未打开仓库";

	private HeadInfo? head;

	private BranchInfo? selectedBranch;

	private RemoteInfo? selectedRemote;

	private string selectedHistoryBranchName = string.Empty;

	private string historyContextText = "全部分支";

	private bool hasLoadedHistory;

	private bool hasMoreHistory;

	private bool isCommitGraphCollapsed;

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

	private string editorText = string.Empty;

	private string detailsText = string.Empty;

	private string equivalentCommand = string.Empty;

	private int selectedRightTabIndex;

	private bool isBusy;

	private bool isCloning;

	private string cloneDestinationPath = string.Empty;

	private bool isPulling;

	private string pullSourceText = "正在连接上游远程仓库";

	private bool hasRepository;

	private bool isExternalOnlyDocument;

	private bool canSaveCurrentDocument;

	private bool hasUnsavedEditorChanges;

	private bool canOpenCurrentDocumentExternally;

	private bool isBrowsingHistoricalCommit;

	private bool canModifyFileTree;

	private string fileTreeContextText = "工作区";

	private string externalDocumentHint = "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";

	private TextDocument? currentDocument;

	private FileChange? selectedChange;

	private CommitNode? selectedCommit;

	private OperationLogEntry? selectedOperationLog;

	private ConflictFile? selectedConflict;

	private RepositoryOperationState operationState;

	private bool hasConflicts;

	private bool hasSelectedConflict;

	private bool canEditSelectedConflict;

	private bool hasDiffHunks;

	private bool canContinueOperation;

	private bool canAbortOperation;

	private string conflictStatusText = "当前没有进行中的冲突操作。";

	private string conflictBaseText = string.Empty;

	private string conflictOursText = string.Empty;

	private string conflictTheirsText = string.Empty;

	private string conflictResultText = string.Empty;

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

	public ObservableCollection<GitHistoryEvent> HistoryEvents { get; } = new ObservableCollection<GitHistoryEvent>();

	public ObservableCollection<RemoteInfo> Remotes { get; } = new ObservableCollection<RemoteInfo>();

	public ObservableCollection<FileChange> UnstagedChanges { get; } = new ObservableCollection<FileChange>();

	public ObservableCollection<FileChange> StagedChanges { get; } = new ObservableCollection<FileChange>();

	public ObservableCollection<CommitNode> History { get; } = new ObservableCollection<CommitNode>();

	public ObservableCollection<FileTreeItem> FileTree { get; } = new ObservableCollection<FileTreeItem>();

	public ObservableCollection<OperationLogEntry> OperationLog { get; } = new ObservableCollection<OperationLogEntry>();

	public ObservableCollection<ConflictFile> Conflicts { get; } = new ObservableCollection<ConflictFile>();

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
			return selectedHistoryBranchName;
		}
		[MemberNotNull("selectedHistoryBranchName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(selectedHistoryBranchName, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedHistoryBranchName);
				selectedHistoryBranchName = value;
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
			return historyContextText;
		}
		[MemberNotNull("historyContextText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(historyContextText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HistoryContextText);
				historyContextText = value;
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
			return hasLoadedHistory;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasLoadedHistory, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasLoadedHistory);
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHistoryComplete);
				hasLoadedHistory = value;
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
			return hasMoreHistory;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasMoreHistory, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasMoreHistory);
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsHistoryComplete);
				hasMoreHistory = value;
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
			return isCommitGraphCollapsed;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isCommitGraphCollapsed, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsCommitGraphCollapsed);
				isCommitGraphCollapsed = value;
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
			return editorText;
		}
		[MemberNotNull("editorText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(editorText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.EditorText);
				editorText = value;
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
			return isExternalOnlyDocument;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isExternalOnlyDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsExternalOnlyDocument);
				isExternalOnlyDocument = value;
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
			return canSaveCurrentDocument;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canSaveCurrentDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanSaveCurrentDocument);
				canSaveCurrentDocument = value;
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
			return hasUnsavedEditorChanges;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasUnsavedEditorChanges, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasUnsavedEditorChanges);
				hasUnsavedEditorChanges = value;
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
			return canOpenCurrentDocumentExternally;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canOpenCurrentDocumentExternally, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanOpenCurrentDocumentExternally);
				canOpenCurrentDocumentExternally = value;
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
			return isBrowsingHistoricalCommit;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isBrowsingHistoricalCommit, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsBrowsingHistoricalCommit);
				isBrowsingHistoricalCommit = value;
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
			return canModifyFileTree;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canModifyFileTree, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanModifyFileTree);
				canModifyFileTree = value;
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
			return fileTreeContextText;
		}
		[MemberNotNull("fileTreeContextText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(fileTreeContextText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.FileTreeContextText);
				fileTreeContextText = value;
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
			return externalDocumentHint;
		}
		[MemberNotNull("externalDocumentHint")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(externalDocumentHint, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ExternalDocumentHint);
				externalDocumentHint = value;
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
			return currentDocument;
		}
		set
		{
			if (!EqualityComparer<TextDocument>.Default.Equals(currentDocument, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CurrentDocument);
				currentDocument = value;
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
			return selectedCommit;
		}
		set
		{
			if (!EqualityComparer<CommitNode>.Default.Equals(selectedCommit, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedCommit);
				selectedCommit = value;
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
			return selectedConflict;
		}
		set
		{
			if (!EqualityComparer<ConflictFile>.Default.Equals(selectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedConflict);
				selectedConflict = value;
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
			return operationState;
		}
		set
		{
			if (!EqualityComparer<RepositoryOperationState>.Default.Equals(operationState, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.OperationState);
				operationState = value;
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
			return hasConflicts;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasConflicts, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasConflicts);
				hasConflicts = value;
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
			return hasSelectedConflict;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(hasSelectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.HasSelectedConflict);
				hasSelectedConflict = value;
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
			return canEditSelectedConflict;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canEditSelectedConflict, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanEditSelectedConflict);
				canEditSelectedConflict = value;
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
			return canContinueOperation;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canContinueOperation, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanContinueOperation);
				canContinueOperation = value;
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
			return canAbortOperation;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(canAbortOperation, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.CanAbortOperation);
				canAbortOperation = value;
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
			return conflictStatusText;
		}
		[MemberNotNull("conflictStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictStatusText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictStatusText);
				conflictStatusText = value;
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
			return conflictBaseText;
		}
		[MemberNotNull("conflictBaseText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictBaseText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictBaseText);
				conflictBaseText = value;
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
			return conflictOursText;
		}
		[MemberNotNull("conflictOursText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictOursText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictOursText);
				conflictOursText = value;
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
			return conflictTheirsText;
		}
		[MemberNotNull("conflictTheirsText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictTheirsText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictTheirsText);
				conflictTheirsText = value;
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
			return conflictResultText;
		}
		[MemberNotNull("conflictResultText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(conflictResultText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConflictResultText);
				conflictResultText = value;
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
	public IAsyncRelayCommand<FileChange?> StageCommand => stageCommand ?? (stageCommand = new AsyncRelayCommand<FileChange>(StageAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<FileChange?> UnstageCommand => unstageCommand ?? (unstageCommand = new AsyncRelayCommand<FileChange>(UnstageAsync));

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
			flag = await git.IsRepositoryAsync(last);
		}
		if (flag)
		{
			await OpenRepositoryAsync(last);
		}
	}

	public Task<bool> IsRepositoryAsync(string path)
	{
		return git.IsRepositoryAsync(path);
	}

	public async Task SortRepositoriesAsync(string mode)
	{
		if (!RepositorySortModes.Contains<string>(mode, StringComparer.Ordinal))
		{
			return;
		}
		RepositorySortMode = mode;
		int version = ++repositorySortVersion;
		string[] paths = RecentRepositories.ToArray();
		Dictionary<string, RepositoryMetadata> metadata = await Task.Run(() => paths.ToDictionary<string, string, RepositoryMetadata>((string path) => path, (string path) => ReadRepositoryMetadata(path, mode == "文件大小"), StringComparer.OrdinalIgnoreCase));
		if (version != repositorySortVersion)
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
		bool opened = false;
		string normalizedPath = Path.GetFullPath(path);
		if (HasRepository && normalizedPath.Equals(ActiveRepositoryPath, StringComparison.OrdinalIgnoreCase))
		{
			await RefreshAsync();
			await RememberRepositoryAsync(ActiveRepositoryPath);
			return true;
		}
		if (!await PrepareForDocumentTransitionAsync("切换仓库"))
		{
			return false;
		}
		await RunBusyAsync(async delegate(CancellationToken token)
		{
			RepositorySnapshot snapshot = await git.GetSnapshotAsync(normalizedPath, token);
			ResetRepositoryView(normalizedPath);
			ActiveRepositoryPath = snapshot.RepositoryPath;
			HasRepository = true;
			await RememberRepositoryAsync(snapshot.RepositoryPath);
			AttachWatcher(snapshot.RepositoryPath);
			await ApplySnapshotAsync(snapshot, token);
			await recoveryService.PruneRepositoryReferencesAsync(snapshot.RepositoryPath, token);
			opened = true;
		});
		return opened;
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
		GitOperationResult result = await git.SetGlobalIdentityAsync(identity);
		ShowResult(result);
		return result;
	}

	public async Task<GitOperationResult> InitializeRepositoryAsync(string path, GitIdentity? identity)
	{
		GitOperationResult result = await git.InitializeAsync(path, identity);
		ShowResult(result);
		if (result.Success)
		{
			await OpenRepositoryAsync(path);
		}
		return result;
	}

	public async Task<GitOperationResult> CloneRepositoryAsync(string url, string path, RemoteCredential? credential)
	{
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
			if (result?.Success ?? false)
			{
				await OpenRepositoryAsync(normalizedPath);
			}
			return result ?? GitOperationResult.Fail("clone", "git clone", new InvalidOperationException("当前有其他操作正在进行，未能开始克隆。"));
		}
		finally
		{
			IsCloning = false;
		}
	}

	public async Task RefreshAsync()
	{
		if (!HasRepository)
		{
			return;
		}
		CancellationTokenSource cancellation = new CancellationTokenSource();
		CancellationToken token = cancellation.Token;
		CancellationTokenSource previousCancellation = refreshCancellation;
		refreshCancellation = cancellation;
		previousCancellation.Cancel();
		previousCancellation.Dispose();
		try
		{
			await refreshGate.WaitAsync(token);
			try
			{
				await ApplySnapshotAsync(await git.GetSnapshotAsync(ActiveRepositoryPath, token), token);
				await SynchronizeCurrentDocumentWithDiskAsync();
			}
			finally
			{
				refreshGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			StatusText = "刷新失败：" + ex2.Message;
		}
	}

	private async Task CommitAsync()
	{
		if (HasRepository)
		{
			GitOperationResult result = await git.CommitAsync(ActiveRepositoryPath, CommitMessage);
			ShowResult(result);
			if (result.Success)
			{
				await CompleteSuccessfulCommitAsync(result);
			}
		}
	}

	private async Task AmendAsync()
	{
		if (HasRepository)
		{
			GitOperationResult result = await git.CommitAsync(ActiveRepositoryPath, CommitMessage, null, amend: true);
			ShowResult(result);
			if (result.Success)
			{
				await CompleteSuccessfulCommitAsync(result);
			}
		}
	}

	private async Task CompleteSuccessfulCommitAsync(GitOperationResult result)
	{
		await ReloadAllAsync();
		await ShowWorkingTreeAsync();
		SelectedCommit = null;
		SelectedRightTabIndex = 1;
		CommitMessage = string.Empty;
		StatusText = result.Summary;
	}

	private async Task StageAsync(FileChange? change)
	{
		if ((object)change != null)
		{
			bool flag = IsCurrentDocument(change.Path);
			if (flag)
			{
				flag = !(await SaveCurrentDocumentAsync(refreshAfterSave: false));
			}
			if (!flag)
			{
				ShowResult(await git.StageFilesAsync(ActiveRepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(change.Path)));
				await RefreshAsync();
			}
		}
	}

	private async Task UnstageAsync(FileChange? change)
	{
		if ((object)change != null)
		{
			ShowResult(await git.UnstageFilesAsync(ActiveRepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(change.Path)));
			await RefreshAsync();
		}
	}

	private async Task StageAllAsync()
	{
		if (await SaveCurrentDocumentAsync(refreshAfterSave: false))
		{
			RepositorySnapshot repositorySnapshot;
			try
			{
				repositorySnapshot = await git.GetSnapshotAsync(ActiveRepositoryPath);
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
				await RefreshAsync();
			}
			else
			{
				ShowResult(await git.StageFilesAsync(ActiveRepositoryPath, array));
				await RefreshAsync();
			}
		}
	}

	private async Task UnstageAllAsync()
	{
		if (StagedChanges.Count != 0)
		{
			ShowResult(await git.UnstageFilesAsync(ActiveRepositoryPath, StagedChanges.Select((FileChange change) => change.Path).ToArray()));
			await RefreshAsync();
		}
	}

	private async Task SaveEditorAsync()
	{
		await SaveCurrentDocumentAsync(refreshAfterSave: true);
	}

	private async Task SaveAndStageEditorAsync()
	{
		if ((object)CurrentDocument == null || !CanSaveCurrentDocument)
		{
			return;
		}
		string documentPath = CurrentDocument.Path;
		if (await SaveCurrentDocumentAsync(refreshAfterSave: false))
		{
			string relativePath = Path.GetRelativePath(ActiveRepositoryPath, documentPath);
			if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal) || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			{
				StatusText = "当前文件不在已打开的仓库中，不能暂存。";
				return;
			}
			ShowResult(await git.StageFilesAsync(ActiveRepositoryPath, new global::_003C_003Ez__ReadOnlySingleElementList<string>(relativePath)));
			await RefreshAsync();
		}
	}

	public async Task<GitOperationResult?> StageSelectedFilesAsync(IReadOnlyList<FileChange> changes)
	{
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
		GitOperationResult result = await git.StageFilesAsync(ActiveRepositoryPath, paths);
		ShowResult(result);
		await RefreshAsync();
		return result;
	}

	public async Task<GitOperationResult?> UnstageSelectedFilesAsync(IReadOnlyList<FileChange> changes)
	{
		string[] array = (from change in changes
			where change.IsStaged
			select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			StatusText = "请先选择至少一个已暂存文件。";
			return null;
		}
		GitOperationResult result = await git.UnstageFilesAsync(ActiveRepositoryPath, array);
		ShowResult(result);
		await RefreshAsync();
		return result;
	}

	private async Task<bool> SaveCurrentDocumentAsync(bool refreshAfterSave)
	{
		if ((object)CurrentDocument == null || !CanSaveCurrentDocument || !HasUnsavedEditorChanges)
		{
			return true;
		}
		await editorSaveGate.WaitAsync();
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
				await files.SaveTextAsync(ActiveRepositoryPath, document, text, allowExternalOverwrite: false);
			}
			catch (ExternalFileChangedException)
			{
				EditorSafetyAction action = await editorInteraction.ResolveExternalChangeAsync(document);
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
				await files.SaveTextAsync(ActiveRepositoryPath, document, text, allowExternalOverwrite: true);
			}
			TextDocument textDocument = await files.OpenTextAsync(document.Path);
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
			await draftStore.DeleteAsync(ActiveRepositoryPath, document.Path);
			StatusText = "已保存 " + Path.GetFileName(document.Path);
			if (refreshAfterSave)
			{
				await RefreshAsync();
			}
			return true;
		}
		catch (Exception ex)
		{
			StatusText = "保存失败：" + ex.Message;
			ScheduleDraftSave();
			return false;
		}
		finally
		{
			editorSaveGate.Release();
		}
	}

	private async Task OpenCurrentDocumentExternallyAsync()
	{
		if (currentDocumentIsHistorical && currentHistoricalCommitId != null && currentHistoricalRelativePath != null)
		{
			await OpenHistoricalFileExternallyAsync(currentHistoricalCommitId, currentHistoricalRelativePath);
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
		RemoteInfo remote = SelectedRemote;
		if ((object)remote == null)
		{
			StatusText = "仓库尚未配置远程地址。";
			return;
		}
		RemoteCredential credential = await GetRemoteCredentialAsync(remote);
		GitOperationResult result = await git.FetchAsync(ActiveRepositoryPath, remote.Name, credential);
		await ReloadAllAsync();
		ShowResult(result);
	}

	public Task<GitOperationResult> PullAsync(PullStrategy strategy)
	{
		RemoteInfo remote = SelectedRemote;
		string remoteBranchName = Head?.BranchName ?? string.Empty;
		return PullAsync(remote, remoteBranchName, strategy);
	}

	public async Task<GitOperationResult> PullAsync(RemoteInfo? remote, string remoteBranchName, PullStrategy strategy)
	{
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
				result = await git.PullAsync(ActiveRepositoryPath, remote.Name, remoteBranchName, strategy, credential);
			}
			catch (Exception exception)
			{
				result = GitOperationResult.Fail("pull", PullCommand(strategy), exception);
			}
			Dictionary<string, PullStrategy> dictionary = settings.PullStrategies.ToDictionary<KeyValuePair<string, PullStrategy>, string, PullStrategy>((KeyValuePair<string, PullStrategy> pair) => pair.Key, (KeyValuePair<string, PullStrategy> pair) => pair.Value);
			dictionary[ActiveRepositoryPath] = strategy;
			settings = settings with
			{
				PullStrategies = dictionary
			};
			await settingsStore.SaveAsync(settings);
			try
			{
				await ReloadAllAsync();
			}
			catch (Exception ex)
			{
				result = result with
				{
					Warnings = result.Warnings.Append("拉取后刷新界面失败：" + ex.Message).ToArray()
				};
			}
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
			IsPulling = false;
			IsBusy = false;
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
				result = await git.PushAsync(ActiveRepositoryPath, remote.Name, forceWithLease, credential, progress);
			}
			catch (Exception exception)
			{
				result = GitOperationResult.Fail("push", forceWithLease ? ("git push --force-with-lease " + remote.Name) : ("git push " + remote.Name), exception);
			}
			try
			{
				await ReloadAllAsync();
			}
			catch (Exception ex)
			{
				result = result with
				{
					Warnings = result.Warnings.Append("推送后刷新界面失败：" + ex.Message).ToArray()
				};
			}
			ShowResult(result);
			return result;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task LoadMoreHistoryAsync()
	{
		if (!HasRepository || (HasLoadedHistory && !HasMoreHistory))
		{
			return;
		}
		IReadOnlyList<CommitNode> readOnlyList = ((!string.IsNullOrEmpty(SelectedHistoryBranchName)) ? (await git.GetBranchHistoryAsync(ActiveRepositoryPath, SelectedHistoryBranchName, historyLoaded, 201)) : (await git.GetHistoryAsync(ActiveRepositoryPath, historyLoaded, 201)));
		(int, bool) tuple = CalculateHistoryPageState(readOnlyList.Count);
		foreach (CommitNode item in readOnlyList.Take(tuple.Item1))
		{
			History.Add(item);
		}
		historyLoaded += tuple.Item1;
		HasMoreHistory = tuple.Item2;
		HasLoadedHistory = true;
		StatusText = ((tuple.Item1 == 0) ? "已经显示全部提交。" : (string.IsNullOrEmpty(SelectedHistoryBranchName) ? $"已加载 {historyLoaded} 个提交 · 全部分支" : $"已加载 {historyLoaded} 个提交 · {SelectedHistoryBranchName} 分支"));
	}

	internal static (int VisibleCount, bool HasMore) CalculateHistoryPageState(int fetchedCount)
	{
		int num = Math.Max(0, fetchedCount);
		return (VisibleCount: Math.Min(num, 200), HasMore: num > 200);
	}

	public async Task<bool> SelectChangeAsync(FileChange? change)
	{
		if (change != null && !IsCurrentDocumentPath(change.Path) &&
			!await PrepareForDocumentTransitionAsync("切换文件"))
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
			LoadDiffPresentation(await diff.GetWorkingDiffPresentationAsync(ActiveRepositoryPath, change.Path, change.IsStaged), isCommitComparison: false);
			string path = Path.Combine(ActiveRepositoryPath, change.Path);
			if (File.Exists(path))
			{
				await OpenFileAsync(path);
			}
		}
		catch (Exception ex)
		{
			DiffText = "无法显示差异：" + ex.Message;
			DiffSummaryText = DiffText;
			ShowDiffEmptyState = true;
		}
		return true;
	}

	public async Task<bool> SelectFileAsync(FileTreeItem? item)
	{
		if (item != null && !item.IsDirectory)
		{
			if ((item.CommitId != null || !IsCurrentDocumentFullPath(item.FullPath)) &&
				!await PrepareForDocumentTransitionAsync("切换文件"))
			{
				return false;
			}
			SelectedRightTabIndex = 1;
			string commitId = item.CommitId;
			if (commitId != null)
			{
				return await OpenCommitFileAsync(commitId, item.RelativePath);
			}
			return await OpenFileAsync(item.FullPath);
		}
		return true;
	}

	public async Task<bool> SelectCommitAsync(CommitNode? commit)
	{
		if (!await PrepareForDocumentTransitionAsync("浏览提交历史"))
		{
			return false;
		}
		SelectedCommit = commit;
		if ((object)commit == null)
		{
			await ShowWorkingTreeAsync();
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
		int loadVersion = ++fileTreeLoadVersion;
		IsBrowsingHistoricalCommit = true;
		CanModifyFileTree = false;
		FileTreeContextText = "版本 " + commit.ShortId;
		try
		{
			IReadOnlyList<CommitTreeEntry> readOnlyList = await git.GetCommitTreeAsync(ActiveRepositoryPath, commit.Id);
			if (loadVersion == fileTreeLoadVersion && string.Equals(SelectedCommit?.Id, commit.Id, StringComparison.Ordinal))
			{
				BuildCommitFileTree(commit.Id, readOnlyList);
				StatusText = $"正在查看版本 {commit.ShortId} 的 {readOnlyList.Count((CommitTreeEntry entry) => !entry.IsDirectory)} 个文件";
			}
		}
		catch (Exception ex)
		{
			if (loadVersion == fileTreeLoadVersion)
			{
				FileTree.Clear();
				StatusText = "无法读取版本 " + commit.ShortId + " 的文件：" + ex.Message;
			}
		}
		return true;
	}

	public async Task<bool> SelectBranchAsync(BranchInfo? branch)
	{
		if (branch != null && !await PrepareForDocumentTransitionAsync("浏览其他分支"))
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
			await LoadMoreHistoryAsync();
			CommitNode tip = History.FirstOrDefault((CommitNode commit) => string.Equals(commit.Id, branch.TipId, StringComparison.Ordinal));
			if ((object)tip == null)
			{
				StatusText = "无法在已加载历史中找到分支 " + branch.FriendlyName + " 的最新版本";
				return true;
			}
			await SelectCommitAsync(tip);
			FileTreeContextText = "分支 " + branch.FriendlyName + " · " + tip.ShortId;
			StatusText = "正在查看 " + branch.FriendlyName + " 分支的版本关系和最新文件";
		}
		return true;
	}

	private async Task ShowWorkingTreeAsync()
	{
		bool flag = !string.IsNullOrEmpty(SelectedHistoryBranchName);
		fileTreeLoadVersion++;
		SelectedCommit = null;
		SelectedBranch = null;
		SelectedHistoryBranchName = string.Empty;
		HistoryContextText = "全部分支";
		DetailsText = string.Empty;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		if (currentDocumentIsHistorical)
		{
			CurrentDocument = null;
			EditorText = string.Empty;
			HasUnsavedEditorChanges = false;
			IsExternalOnlyDocument = false;
			CanSaveCurrentDocument = false;
			CanOpenCurrentDocumentExternally = false;
			currentDocumentIsHistorical = false;
		}
		if (HasRepository)
		{
			BuildFileTree(ActiveRepositoryPath);
			StatusText = "正在显示当前工作区文件";
			if (flag)
			{
				History.Clear();
				ResetHistoryPagination();
				await LoadMoreHistoryAsync();
				StatusText = "正在显示当前工作区文件 · 全部分支关系";
			}
		}
	}

	public void SelectConflict(ConflictFile? conflict)
	{
		SelectedConflict = conflict;
		HasSelectedConflict = (object)conflict != null;
		CanEditSelectedConflict = (object)conflict != null && !conflict.IsBinary;
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
		if ((object)conflictFile != null && conflictFile.IsBinary)
		{
			StatusText = "二进制冲突已锁定文本编辑；请使用外部工具处理后再暂存。";
			return;
		}
		ConflictResultText = side switch
		{
			ConflictSide.Ours => ConflictOursText,
			ConflictSide.Theirs => ConflictTheirsText,
			ConflictSide.Both => ConflictOursText.TrimEnd() + Environment.NewLine + ConflictTheirsText.TrimStart(),
			_ => ConflictResultText,
		};
	}

	public async Task<GitOperationResult> ResolveSelectedConflictAsync()
	{
		ConflictFile conflict = SelectedConflict;
		if ((object)conflict == null)
		{
			throw new InvalidOperationException("请先选择冲突文件。");
		}
		if (conflict.IsBinary)
		{
			InvalidOperationException exception = new InvalidOperationException("二进制冲突不能通过文本编辑器解决；本版本已阻止可能破坏文件的写入。");
			GitOperationResult result = GitOperationResult.Fail("conflict-resolve", "git add -- <path>", exception);
			ShowResult(result);
			return result;
		}
		if (IsCurrentDocumentPath(conflict.Path) &&
			!await PrepareForDocumentTransitionAsync("解决当前文件的冲突"))
		{
			return CanceledOperation("conflict-resolve");
		}
		GitOperationResult result2 = await git.ResolveConflictAsync(ActiveRepositoryPath, conflict.Path, ConflictResultText);
		ShowResult(result2);
		await RefreshAsync();
		return result2;
	}

	public async Task<GitOperationResult> CreateBranchAsync(string name)
	{
		GitOperationResult result = await git.CreateBranchAsync(ActiveRepositoryPath, name, SelectedCommit?.Id);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> CheckoutBranchAsync(BranchInfo branch)
	{
		if (!await PrepareForDocumentTransitionAsync("切换分支"))
		{
			return CanceledOperation("checkout");
		}
		GitOperationResult result = await git.CheckoutBranchAsync(ActiveRepositoryPath, branch.FriendlyName);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public Task<BranchDeletionCheck> CheckBranchDeletionAsync(BranchInfo branch)
	{
		return git.CheckBranchDeletionAsync(ActiveRepositoryPath, branch.FriendlyName);
	}

	public async Task<GitOperationResult> DeleteBranchAsync(BranchInfo branch, bool force)
	{
		GitOperationResult result = await git.DeleteBranchAsync(ActiveRepositoryPath, branch.FriendlyName, force);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> MergeBranchAsync(BranchInfo branch)
	{
		if (!await PrepareForDocumentTransitionAsync("合并分支"))
		{
			return CanceledOperation("merge");
		}
		GitOperationResult result = await git.MergeAsync(ActiveRepositoryPath, branch.FriendlyName);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> CherryPickSelectedAsync()
	{
		if (!await PrepareForDocumentTransitionAsync("拣选提交"))
		{
			return CanceledOperation("cherry-pick");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		GitOperationResult result = await git.CherryPickAsync(ActiveRepositoryPath, SelectedCommit.Id);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> RevertSelectedAsync()
	{
		if (!await PrepareForDocumentTransitionAsync("撤销提交"))
		{
			return CanceledOperation("revert");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		GitOperationResult result = await git.RevertAsync(ActiveRepositoryPath, SelectedCommit.Id);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> ResetSelectedAsync(GitResetMode mode)
	{
		if (!await PrepareForDocumentTransitionAsync("回退当前分支"))
		{
			return CanceledOperation("reset");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		GitOperationResult result = await git.ResetAsync(ActiveRepositoryPath, SelectedCommit.Id, mode);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> ContinueOperationAsync()
	{
		if (!await PrepareForDocumentTransitionAsync("继续 Git 操作"))
		{
			return CanceledOperation("continue");
		}
		GitOperationResult result = await git.ContinueOperationAsync(ActiveRepositoryPath);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> AbortOperationAsync()
	{
		if (!await PrepareForDocumentTransitionAsync("中止 Git 操作"))
		{
			return CanceledOperation("abort");
		}
		GitOperationResult result = await git.AbortOperationAsync(ActiveRepositoryPath);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> ConfigureIdentityAsync(GitIdentity identity, bool global)
	{
		GitOperationResult result = await git.SetIdentityAsync(ActiveRepositoryPath, identity, global);
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
		GitOperationResult gitOperationResult = ((!unstage) ? (await indexPatch.StageHunksAsync(ActiveRepositoryPath, path, hunks)) : (await indexPatch.UnstageHunksAsync(ActiveRepositoryPath, path, hunks)));
		GitOperationResult result = gitOperationResult;
		ShowResult(result);
		await RefreshAsync();
		FileChange change = (unstage ? StagedChanges : UnstagedChanges).FirstOrDefault((FileChange fileChange) => fileChange.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? (unstage ? UnstagedChanges : StagedChanges).FirstOrDefault((FileChange fileChange) => fileChange.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
		await SelectChangeAsync(change);
		return result;
	}

	public async Task<GitOperationResult> RenameBranchAsync(BranchInfo branch, string newName)
	{
		GitOperationResult result = await git.RenameBranchAsync(ActiveRepositoryPath, branch.FriendlyName, newName.Trim());
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> CheckoutSelectedCommitAsync()
	{
		if (!await PrepareForDocumentTransitionAsync("切换提交"))
		{
			return CanceledOperation("checkout");
		}
		if ((object)SelectedCommit == null)
		{
			throw new InvalidOperationException("请先选择一个提交。");
		}
		GitOperationResult result = await git.CheckoutCommitAsync(ActiveRepositoryPath, SelectedCommit.Id);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task CompareCommitsAsync(CommitNode oldCommit, CommitNode newCommit)
	{
		ClearDiffPresentation();
		SelectedChange = null;
		SelectedRightTabIndex = 0;
		try
		{
			LoadDiffPresentation(await diff.CompareCommitsPresentationAsync(ActiveRepositoryPath, oldCommit.Id, newCommit.Id), isCommitComparison: true);
			StatusText = "已比较 " + oldCommit.ShortId + " 与 " + newCommit.ShortId;
		}
		catch (Exception ex)
		{
			DiffText = "无法比较提交：" + ex.Message;
			DiffSummaryText = DiffText;
			ShowDiffEmptyState = true;
			StatusText = "提交比较失败。";
		}
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
		string[] array = (from change in changes
			where !change.IsStaged
			select change.Path).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			throw new InvalidOperationException("请从未暂存修改列表中选择至少一个要丢弃的文件。");
		}
		string currentFullPath = ((!currentDocumentIsHistorical && (object)CurrentDocument != null) ? Path.GetFullPath(CurrentDocument.Path) : null);
		bool refreshEditor = currentFullPath != null && array.Any((string path) => Path.GetFullPath(Path.Combine(ActiveRepositoryPath, path)).Equals(currentFullPath, StringComparison.OrdinalIgnoreCase));
		if (refreshEditor && !await PrepareForDocumentTransitionAsync("丢弃当前文件的修改"))
		{
			return CanceledOperation("discard");
		}
		GitOperationResult result = await git.DiscardFilesAsync(ActiveRepositoryPath, array);
		ShowResult(result);
		await RefreshAsync();
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
		GitOperationResult result = await git.ResolveBinaryConflictAsync(ActiveRepositoryPath, conflictFile.Path, side);
		ShowResult(result);
		await RefreshAsync();
		return result;
	}

	public Task<IReadOnlyList<RecoveryPoint>> GetRecoveryPointsAsync()
	{
		return recoveryService.ListAsync(ActiveRepositoryPath);
	}

	public async Task<GitOperationResult> RestoreRecoveryPointAsync(RecoveryPoint point)
	{
		if (!await PrepareForDocumentTransitionAsync("恢复工作区"))
		{
			return CanceledOperation("restore");
		}
		if (!Path.GetFullPath(point.RepositoryPath).Equals(Path.GetFullPath(ActiveRepositoryPath), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("恢复点不属于当前仓库。");
		}
		GitOperationResult result = await recoveryService.RestoreAsync(point);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> DeleteRecoveryPointAsync(RecoveryPoint point)
	{
		GitOperationResult result = await recoveryService.DeleteAsync(point);
		ShowResult(result);
		return result;
	}

	public async Task<GitOperationResult> ConfigureRemoteAsync(string? originalName, string name, string url)
	{
		GitOperationResult gitOperationResult = ((originalName != null) ? (await git.UpdateRemoteAsync(ActiveRepositoryPath, originalName, name, url)) : (await git.AddRemoteAsync(ActiveRepositoryPath, name, url)));
		GitOperationResult result = gitOperationResult;
		ShowResult(result);
		await RefreshAsync();
		return result;
	}

	public async Task<GitOperationResult> RemoveRemoteAsync(string name)
	{
		GitOperationResult result = await git.RemoveRemoteAsync(ActiveRepositoryPath, name);
		ShowResult(result);
		await RefreshAsync();
		return result;
	}

	public async Task CreateFileAsync(string parentDirectory, string name, bool directory)
	{
		ValidateLeafName(name);
		string path = Path.Combine(parentDirectory, name);
		if (!directory)
		{
			await files.CreateFileAsync(ActiveRepositoryPath, path);
		}
		else
		{
			await files.CreateDirectoryAsync(ActiveRepositoryPath, path);
		}
		await RefreshAsync();
	}

	public Task<IReadOnlyList<SystemNewFileType>> GetSystemNewFileTypesAsync()
	{
		return systemNewFiles.GetAvailableTypesAsync();
	}

	public async Task CreateSystemFileAsync(string parentDirectory, string name, SystemNewFileType type)
	{
		ValidateLeafName(name);
		await systemNewFiles.CreateAsync(ActiveRepositoryPath, Path.Combine(parentDirectory, name), type.Id);
		await RefreshAsync();
	}

	public async Task MoveFileAsync(string source, string newName)
	{
		ValidateLeafName(newName);
		bool affectsCurrent = PathContainsCurrentDocument(source);
		if (affectsCurrent && !await PrepareForDocumentTransitionAsync("重命名当前文件"))
		{
			return;
		}
		string destination = Path.Combine(Path.GetDirectoryName(source) ?? ActiveRepositoryPath, newName);
		string currentPath = affectsCurrent && CurrentDocument != null ? CurrentDocument.Path : null;
		string relocatedCurrentPath = null;
		if (currentPath != null)
		{
			string relative = Path.GetRelativePath(source, currentPath);
			relocatedCurrentPath = relative.Equals(".", StringComparison.Ordinal)
				? destination
				: Path.Combine(destination, relative);
		}
		await files.MoveAsync(ActiveRepositoryPath, source, destination);
		if (currentPath != null && relocatedCurrentPath != null)
		{
			await draftStore.MoveAsync(ActiveRepositoryPath, currentPath, relocatedCurrentPath);
			await OpenFileAsync(relocatedCurrentPath);
		}
		await RefreshAsync();
	}

	public async Task DeleteFileAsync(string path)
	{
		bool affectsCurrent = PathContainsCurrentDocument(path);
		if (affectsCurrent && !await PrepareForDocumentTransitionAsync("删除当前文件"))
		{
			return;
		}
		string currentPath = affectsCurrent && CurrentDocument != null ? CurrentDocument.Path : null;
		await files.DeleteAsync(ActiveRepositoryPath, path);
		if (currentPath != null)
		{
			await draftStore.DeleteAsync(ActiveRepositoryPath, currentPath);
			ClearCurrentDocument();
		}
		await RefreshAsync();
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
		GitOperationResult result = await git.CreateTagAsync(ActiveRepositoryPath, name.Trim(), (!string.IsNullOrWhiteSpace(targetId)) ? targetId : Head?.CommitId, tagType, message);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> DeleteTagAsync(string name)
	{
		GitOperationResult result = await git.DeleteTagAsync(ActiveRepositoryPath, name);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public Task<IReadOnlyList<StashInfo>> GetStashesAsync()
	{
		return git.GetStashesAsync(ActiveRepositoryPath);
	}

	public async Task<GitOperationResult> SaveStashAsync(string message)
	{
		if (!await PrepareForDocumentTransitionAsync("保存当前现场"))
		{
			return CanceledOperation("stash");
		}
		GitOperationResult result = await git.SaveStashAsync(ActiveRepositoryPath, message);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> ApplyStashAsync(int index, bool pop)
	{
		if (!await PrepareForDocumentTransitionAsync(pop ? "弹出暂存现场" : "应用暂存现场"))
		{
			return CanceledOperation("stash");
		}
		GitOperationResult result = await git.ApplyStashAsync(ActiveRepositoryPath, index, pop);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> DeleteStashAsync(int index)
	{
		GitOperationResult result = await git.DeleteStashAsync(ActiveRepositoryPath, index);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public async Task<GitOperationResult> RebaseOntoAsync(string upstreamBranch, string? ontoBranch = null)
	{
		if (!await PrepareForDocumentTransitionAsync("变基当前分支"))
		{
			return CanceledOperation("rebase");
		}
		GitOperationResult result = await git.RebaseOntoAsync(ActiveRepositoryPath, upstreamBranch, string.IsNullOrWhiteSpace(ontoBranch) ? null : ontoBranch);
		ShowResult(result);
		await ReloadAllAsync();
		return result;
	}

	public GitOperationPreview Preview(string operation, params string[] affected)
	{
		return git.Preview(operation, affected);
	}

	private async Task<bool> OpenFileAsync(string path)
	{
		path = Path.GetFullPath(path);
		if (!currentDocumentIsHistorical && CurrentDocument != null &&
			Path.GetFullPath(CurrentDocument.Path).Equals(path, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!await PrepareForDocumentTransitionAsync("切换文件"))
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
				document = await files.OpenTextAsync(path);
				externalOnly = document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(path));
			}

			string editorValue = externalOnly ? string.Empty : document.Text;
			bool restoreDraft = false;
			if (!externalOnly && !document.IsReadOnly && HasRepository)
			{
				EditorDraft draft = await draftStore.LoadAsync(ActiveRepositoryPath, path);
				if (draft != null)
				{
					if (string.Equals(draft.Text, document.Text, StringComparison.Ordinal))
					{
						await draftStore.DeleteAsync(ActiveRepositoryPath, path);
					}
					else
					{
						EditorSafetyAction action = await editorInteraction.ResolveDraftAsync(draft);
						if (action == EditorSafetyAction.Cancel)
						{
							return false;
						}
						if (action == EditorSafetyAction.Discard)
						{
							await draftStore.DeleteAsync(ActiveRepositoryPath, path);
						}
						else if (action == EditorSafetyAction.Restore)
						{
							editorValue = draft.Text;
							restoreDraft = true;
						}
					}
				}
			}

			CancelScheduledDraftSave();
			currentDocumentIsHistorical = false;
			currentHistoricalCommitId = null;
			currentHistoricalRelativePath = null;
			CurrentDocument = document;
			IsExternalOnlyDocument = externalOnly;
			CanSaveCurrentDocument = !document.IsReadOnly && !externalOnly;
			CanOpenCurrentDocumentExternally = externalOnly;
			ExternalDocumentHint = "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
			EditorText = editorValue;
			HasUnsavedEditorChanges = restoreDraft;
			return true;
		}
		catch (Exception ex)
		{
			StatusText = "无法打开文件：" + ex.Message;
			return false;
		}
	}

	private async Task<bool> OpenCommitFileAsync(string commitId, string relativePath)
	{
		if (!await PrepareForDocumentTransitionAsync("浏览历史文件"))
		{
			return false;
		}
		try
		{
			TextDocument document = await git.OpenCommitFileAsync(ActiveRepositoryPath, commitId, relativePath);
			CancelScheduledDraftSave();
			currentDocumentIsHistorical = true;
			currentHistoricalCommitId = commitId;
			currentHistoricalRelativePath = relativePath;
			CurrentDocument = document;
			IsExternalOnlyDocument = document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(relativePath));
			CanSaveCurrentDocument = false;
			CanOpenCurrentDocumentExternally = IsExternalOnlyDocument;
			ExternalDocumentHint = "这是历史提交中的只读文件。可导出只读副本并使用 Windows 默认程序打开。";
			EditorText = (IsExternalOnlyDocument ? string.Empty : document.Text);
			HasUnsavedEditorChanges = false;
			return true;
		}
		catch (Exception ex)
		{
			StatusText = "无法打开历史文件：" + ex.Message;
			return false;
		}
	}

	private async Task<bool> OpenHistoricalFileExternallyAsync(string commitId, string relativePath)
	{
		try
		{
			TextDocument textDocument = ((currentHistoricalCommitId == commitId && currentHistoricalRelativePath == relativePath && CurrentDocument?.ContentBytes != null) ? CurrentDocument : (await git.OpenCommitFileAsync(ActiveRepositoryPath, commitId, relativePath)));
			if (textDocument.ContentBytes == null)
			{
				throw new InvalidOperationException("无法读取历史文件的原始内容。");
			}
			string text = BuildHistoricalExportPath(ActiveRepositoryPath, commitId, relativePath);
			if (!File.Exists(text))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(text)!);
				await File.WriteAllBytesAsync(text, textDocument.ContentBytes);
			}
			new FileInfo(text).IsReadOnly = true;
			await files.OpenExternalAsync(text);
			StatusText = "已打开 " + Path.GetFileName(relativePath) + " 的版本 " + commitId.Substring(0, Math.Min(8, commitId.Length)) + "（只读副本）";
			return true;
		}
		catch (Exception ex)
		{
			StatusText = "无法打开历史版本文件：" + ex.Message;
			return false;
		}
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
		currentDocumentIsHistorical = false;
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
		if (currentDocumentIsHistorical)
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
					await draftStore.DeleteAsync(ActiveRepositoryPath, path);
				}
				ClearCurrentDocument();
			}
			return;
		}

		TextDocument document = await files.OpenTextAsync(path);
		if (CurrentDocument == null ||
			!Path.GetFullPath(CurrentDocument.Path).Equals(path, StringComparison.OrdinalIgnoreCase) ||
			(!discardUnsaved && HasUnsavedEditorChanges))
		{
			return;
		}

		CancelScheduledDraftSave();
		currentDocumentIsHistorical = false;
		currentHistoricalCommitId = null;
		currentHistoricalRelativePath = null;
		CurrentDocument = document;
		IsExternalOnlyDocument = document.IsBinary || ExternalDocumentExtensions.Contains(Path.GetExtension(path));
		CanSaveCurrentDocument = !document.IsReadOnly && !IsExternalOnlyDocument;
		CanOpenCurrentDocumentExternally = IsExternalOnlyDocument;
		ExternalDocumentHint = "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
		EditorText = IsExternalOnlyDocument ? string.Empty : document.Text;
		HasUnsavedEditorChanges = false;
		if (deleteDraft)
		{
			await draftStore.DeleteAsync(ActiveRepositoryPath, path);
		}
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

	private async Task ReloadAllAsync()
	{
		fileTreeLoadVersion++;
		SelectedCommit = null;
		SelectedBranch = null;
		SelectedHistoryBranchName = string.Empty;
		HistoryContextText = "全部分支";
		DetailsText = string.Empty;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		await RefreshAsync();
		History.Clear();
		ResetHistoryPagination();
		await LoadMoreHistoryAsync();
	}

	private async Task ApplySnapshotAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken)
	{
		string selectedRemoteName = SelectedRemote?.Name;
		Head = snapshot.Head;
		CurrentBranch = (snapshot.Head.IsDetached ? ("游离 HEAD · " + snapshot.Head.CommitId.Substring(0, Math.Min(8, snapshot.Head.CommitId.Length))) : ("HEAD → " + snapshot.Head.BranchName));
		Replace(Branches, snapshot.Branches);
		Replace(Tags, snapshot.Tags);
		ObservableCollection<GitHistoryEvent> historyEvents = HistoryEvents;
		Replace(historyEvents, await git.GetHistoryEventsAsync(snapshot.RepositoryPath, cancellationToken));
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
		Replace(Notices, snapshot.Features.Notices);
		if (!IsBrowsingHistoricalCommit)
		{
			BuildFileTree(snapshot.WorkingDirectory);
		}
		ObservableCollection<OperationLogEntry> operationLog = OperationLog;
		Replace(operationLog, await logStore.GetRecentAsync(snapshot.RepositoryPath, 100, cancellationToken));
		SelectedOperationLog = OperationLog.FirstOrDefault();
		string selectedConflictPath = SelectedConflict?.Path;
		string selectedConflictResultText = ConflictResultText;
		bool preserveEditedConflictResult = SelectedConflict != null &&
			!string.Equals(
				selectedConflictResultText,
				SelectedConflict.ResultText,
				StringComparison.Ordinal);
		ObservableCollection<ConflictFile> conflicts = Conflicts;
		Replace(conflicts, await git.GetConflictsAsync(snapshot.RepositoryPath, cancellationToken));
		ConflictFile selectedConflict = Conflicts.FirstOrDefault((ConflictFile conflict) => conflict.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase)) ?? Conflicts.FirstOrDefault();
		SelectConflict(selectedConflict);
		if (preserveEditedConflictResult && selectedConflict != null &&
			selectedConflict.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase))
		{
			ConflictResultText = selectedConflictResultText;
		}
		UpdateConflictState(snapshot.OperationState);
		StatusText = $"{snapshot.Changes.Count} 个变化 · {snapshot.Branches.Count} 个分支 · 刷新于 {snapshot.RefreshedAt:HH:mm:ss}";
		if (History.Count == 0)
		{
			ResetHistoryPagination();
			await LoadMoreHistoryAsync();
		}
	}

	private void BuildFileTree(string root)
	{
		FileTree.Clear();
		try
		{
			foreach (string item in (from path in Directory.EnumerateFileSystemEntries(root)
				where !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase) && !FileTreeItem.IsTransientOfficeLockFile(path)
				select path).OrderByDescending(Directory.Exists).ThenBy<string, string>(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(2000))
			{
				FileTree.Add(FileTreeItem.Create(item, 3));
			}
		}
		catch (IOException)
		{
		}
	}

	private void BuildCommitFileTree(string commitId, IReadOnlyList<CommitTreeEntry> entries)
	{
		FileTree.Clear();
		Dictionary<string, CommitTreeEntry[]> byParent = entries.Take(10000).GroupBy<CommitTreeEntry, string>(delegate(CommitTreeEntry entry)
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
		watcher.RepositoryChanged += async delegate
		{
			await Application.Current.Dispatcher.InvokeAsync((Func<Task>)async delegate
			{
				await RefreshAsync();
			});
		};
		watcher.Start();
	}

	private async Task RememberRepositoryAsync(string path)
	{
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
		await SortRepositoriesAsync(RepositorySortMode);
		SelectedRepository = existing;
	}

	private void ResetHistoryPagination()
	{
		historyLoaded = 0;
		HasLoadedHistory = false;
		HasMoreHistory = false;
	}

	private void ResetRepositoryView(string path)
	{
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
		currentDocumentIsHistorical = false;
		HasUnsavedEditorChanges = false;
		IsExternalOnlyDocument = false;
		CanSaveCurrentDocument = false;
		CanOpenCurrentDocumentExternally = false;
		IsBrowsingHistoricalCommit = false;
		CanModifyFileTree = true;
		FileTreeContextText = "工作区";
		fileTreeLoadVersion++;
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
		bool supportedOperation = state is RepositoryOperationState.Merge or RepositoryOperationState.Rebase or RepositoryOperationState.CherryPick or RepositoryOperationState.Revert;
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
		if (IsBusy)
		{
			return;
		}
		IsBusy = true;
		try
		{
			await action(CancellationToken.None);
		}
		catch (Exception ex)
		{
			StatusText = ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void ShowResult(GitOperationResult result)
	{
		StatusText = (result.Success ? result.Summary : (result.Summary + "：" + result.ErrorMessage));
		EquivalentCommand = result.EquivalentCommand;
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
		target.Clear();
		foreach (T item in source)
		{
			target.Add(item);
		}
	}

	public Task<bool> PrepareForCloseAsync()
	{
		return PrepareForDocumentTransitionAsync("退出程序");
	}

	private async Task<bool> PrepareForDocumentTransitionAsync(string reason)
	{
		await documentTransitionGate.WaitAsync();
		try
		{
			await editorSaveGate.WaitAsync();
			editorSaveGate.Release();

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
			documentTransitionGate.Release();
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
		draftSaveCancellation = new CancellationTokenSource();
		_ = SaveDraftAfterDelayAsync(
			ActiveRepositoryPath, document, EditorText, draftSaveCancellation.Token);
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
				DateTimeOffset.UtcNow), cancellationToken);
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
		draftSaveCancellation.Cancel();
		draftSaveCancellation.Dispose();
		draftSaveCancellation = new CancellationTokenSource();
	}

	public void Dispose()
	{
		watcher?.Dispose();
		refreshCancellation.Cancel();
		refreshCancellation.Dispose();
		CancelScheduledDraftSave();
		draftSaveCancellation.Dispose();
		editorSaveGate.Dispose();
		documentTransitionGate.Dispose();
		refreshGate.Dispose();
	}

	private bool IsCurrentDocument(string relativePath)
	{
		return HasUnsavedEditorChanges && IsCurrentDocumentPath(relativePath);
	}

	private bool IsCurrentDocumentPath(string relativePath) =>
		IsCurrentDocumentFullPath(Path.Combine(ActiveRepositoryPath, relativePath));

	private bool IsCurrentDocumentFullPath(string path) =>
		CurrentDocument != null && !currentDocumentIsHistorical &&
		Path.GetFullPath(path).Equals(Path.GetFullPath(CurrentDocument.Path), StringComparison.OrdinalIgnoreCase);

	private bool PathContainsCurrentDocument(string path)
	{
		if (CurrentDocument == null || currentDocumentIsHistorical)
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
		GitOperationResult.Fail(operation, "git " + operation,
			new OperationCanceledException("用户取消了操作，仓库和当前文档均未更改。"));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnEditorTextChanged(string value)
	{
		HasUnsavedEditorChanges = (object)CurrentDocument != null && CanSaveCurrentDocument && !string.Equals(value, CurrentDocument.Text, StringComparison.Ordinal);
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
