using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GitVisualizer.App.Controls;
using GitVisualizer.App.Dialogs;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using Microsoft.Win32;

namespace GitVisualizer.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> BuiltInNewFileExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".docx", ".cs", ".json", ".xml"
        };

    private readonly MainWindowViewModel viewModel;
    private Point dragStart;
    private FileTreeItem? selectedTreeItem;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        viewModel.ConflictDetected += ViewModel_OnConflictDetected;
    }

    private void ViewModel_OnConflictDetected(
        object? sender,
        ConflictDetectedEventArgs e)
    {
        MessageBox.Show(
            this,
            $"{e.OperationName}过程中检测到 {e.ConflictCount} 个冲突文件，当前 Git 操作已暂停。\n\n" +
            "请确认此提示，然后在“冲突”页面逐个检查并解决冲突。所有冲突解决前，请勿继续当前操作。",
            "检测到 Git 冲突",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async void OpenRepository_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Git 仓库" };
        if (dialog.ShowDialog(this) == true)
        {
            await TryOpenOrInitializeAsync(dialog.FolderName);
        }
    }

    private async void InitializeRepository_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要初始化的文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            await TryOpenOrInitializeAsync(dialog.FolderName);
        }
    }

    private async void CloneRepository_OnClick(object sender, RoutedEventArgs e)
    {
        var cloneDialog = new CloneRepositoryWindow
        {
            Owner = this
        };
        if (cloneDialog.ShowDialog() != true)
        {
            return;
        }
        var folderDialog = new OpenFolderDialog { Title = "选择克隆目标的父文件夹" };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }
        var repositoryName = GuessRepositoryName(cloneDialog.RepositoryUrl);
        var destination = Path.Combine(folderDialog.FolderName, repositoryName);
        var result = await viewModel.CloneRepositoryAsync(
            cloneDialog.RepositoryUrl,
            destination,
            cloneDialog.Credential);
        if (result.Success)
        {
            if (cloneDialog.Credential is { Remember: true } credential)
            {
                await viewModel.SaveCloneCredentialAsync(
                    cloneDialog.RepositoryUrl,
                    credential);
            }
            MessageBox.Show(
                this,
                $"远程仓库已成功克隆。\n\n克隆位置：\n{Path.GetFullPath(destination)}",
                "克隆完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            var errorMessage = result.ErrorMessage ?? result.Summary;
            if (errorMessage.Contains(
                    "authentication required but no callback set",
                    StringComparison.OrdinalIgnoreCase))
            {
                errorMessage =
                    "远程仓库要求身份验证。请重新克隆并选择“令牌登录”，然后填写用户名和访问令牌。";
            }
            MessageBox.Show(
                this,
                errorMessage,
                "克隆失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ConfigureRemote_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository)
        {
            MessageBox.Show(this, "请先打开一个本地仓库。", "配置远程仓库");
            return;
        }

        var dialog = new RemoteConfigurationWindow(viewModel.Remotes)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = dialog.RemoteToRemove is { } remoteToRemove
            ? await viewModel.RemoveRemoteAsync(remoteToRemove)
            : await viewModel.ConfigureRemoteAsync(
                dialog.OriginalName,
                dialog.RemoteName,
                dialog.RemoteUrl);
        if (!result.Success)
        {
            MessageBox.Show(
                this,
                result.ErrorMessage ?? result.Summary,
                dialog.RemoteToRemove is null
                    ? "无法保存远程仓库"
                    : "无法删除远程仓库",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RecoveryCenter_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository)
        {
            return;
        }
        var points = await viewModel.GetRecoveryPointsAsync();
        var dialog = new RecoveryCenterWindow(points) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPoint is null)
        {
            return;
        }
        var point = dialog.SelectedPoint;
        if (MessageBox.Show(
                this,
                $"恢复到 {point.CreatedAt:yyyy-MM-dd HH:mm:ss} 的状态？\n\n" +
                "程序会先保存当前现场，然后创建并切换到独立 recovered/... 分支，同时恢复当时的工作区和暂存区。",
                "确认恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var result = await viewModel.RestoreRecoveryPointAsync(point);
        MessageBox.Show(
            this,
            result.Success
                ? result.Summary + "\n\n" + string.Join("\n", result.Details)
                : result.ErrorMessage ?? result.Summary,
            result.Success ? "恢复完成" : "恢复失败",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private async void Push_OnClick(object sender, RoutedEventArgs e)
    {
        var remote = viewModel.SelectedRemote;
        if (remote is null)
        {
            MessageBox.Show(
                this,
                "请先配置远程仓库，并在“推送”按钮左侧选择推送目标。",
                "选择推送目标",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunPushAsync(remote, forceWithLease: false);
    }

    private async Task RunPushAsync(RemoteInfo remote, bool forceWithLease)
    {
        var branchName = viewModel.Head?.BranchName ?? "当前 HEAD";
        var monitor = new PushMonitorWindow(
            remote.Name,
            remote.PushUrl,
            branchName)
        {
            Owner = this
        };
        monitor.Show();

        var progress = new Progress<GitPushProgress>(monitor.Report);
        var result = await viewModel.PushToRemoteAsync(remote, progress, forceWithLease);
        monitor.Complete(result);
    }

    private async Task<bool> TryOpenOrInitializeAsync(string path)
    {
        try
        {
            if (await viewModel.IsRepositoryAsync(path))
            {
                return await viewModel.OpenRepositoryAsync(path);
            }

            var answer = MessageBox.Show(
                this,
                "此文件夹尚未初始化为 Git 仓库，是否立即初始化并打开？\n\n" + path,
                "初始化仓库",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }

            var identity = new GitIdentity(
                Environment.UserName,
                $"{Environment.UserName}@local.invalid");
            var result = await viewModel.InitializeRepositoryAsync(path, identity);
            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.ErrorMessage ?? "仓库初始化失败。",
                    "无法初始化仓库",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return result.Success;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开所选文件夹：\n\n{exception.Message}",
                "打开仓库失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private async void RecentRepositories_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is not string path ||
            PathsEqual(path, viewModel.ActiveRepositoryPath))
        {
            return;
        }
        if (viewModel.IsBusy)
        {
            viewModel.SelectedRepository = viewModel.HasRepository
                ? viewModel.ActiveRepositoryPath
                : null;
            return;
        }

        if (!await TryOpenOrInitializeAsync(path))
        {
            viewModel.SelectedRepository = viewModel.HasRepository
                ? viewModel.ActiveRepositoryPath
                : null;
        }
    }

    private void RecentRepositories_OnPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox &&
            e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void RemoveRepository_OnClick(object sender, RoutedEventArgs e)
    {
        if (RecentRepositoriesList.SelectedItem is not string path)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"只把以下仓库从左侧导航列表中移除？\n\n{path}\n\n" +
            "仓库目录、项目文件和 .git 数据都不会被删除。",
            "从仓库列表中移除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await viewModel.RemoveRecentRepositoryAsync(path);
        }
    }

    private async void RepositorySort_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string mode)
        {
            await viewModel.SortRepositoriesAsync(mode);
        }
    }

    private void OpenRepositoryFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var path = RecentRepositoriesList.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(path) && viewModel.HasRepository)
        {
            path = viewModel.ActiveRepositoryPath;
        }
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(
                this,
                "所选仓库目录不存在，请重新打开仓库。",
                "打开仓库目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(path),
                UseShellExecute = true
            });
            viewModel.StatusText = $"已在文件资源管理器中打开 {path}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开仓库目录：\n\n{exception.Message}",
                "打开仓库目录",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void BranchList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BranchList.SelectedItem is BranchInfo branch && !branch.IsCurrent && !branch.IsRemote)
        {
            await viewModel.CheckoutBranchAsync(branch);
        }
    }

    private async void BranchList_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 ||
            sender is not ListBox listBox ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem
            {
                DataContext: BranchInfo branch
            })
        {
            return;
        }

        await viewModel.SelectBranchAsync(branch);
    }

    private void BranchList_OnPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox &&
            e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void DeleteBranch_OnClick(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is not BranchInfo branch)
        {
            return;
        }

        BranchDeletionCheck check;
        try
        {
            check = await viewModel.CheckBranchDeletionAsync(branch);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法检查分支是否可删除：{exception.Message}",
                "删除分支",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (check.UncommittedChangeCount > 0)
        {
            MessageBox.Show(
                this,
                $"当前工作区还有 {check.UncommittedChangeCount} 项已暂存或未暂存的未提交修改。\n\n" +
                "请先提交或处理这些修改，删除分支操作已中断。",
                "不能删除分支",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (check.IsRemote)
        {
            MessageBox.Show(
                this,
                "这里显示的是远程跟踪分支，不能通过本地分支删除功能移除。",
                "不能删除分支",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (check.IsCurrent)
        {
            MessageBox.Show(
                this,
                "不能删除当前分支。请先双击切换到其他本地分支，再执行删除。",
                "不能删除分支",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (check.IsMainline)
        {
            MessageBox.Show(
                this,
                $"不能删除主线分支 {check.MainlineName}。",
                "不能删除分支",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var force = !check.IsMergedIntoMainline;
        var message = force
            ? $"分支 {check.BranchName} 尚未合并到主线 {check.MainlineName}。\n\n" +
              "强制删除可能丢失仅存在于该分支的提交。仍要删除吗？"
            : $"确定删除已经合并到主线 {check.MainlineName} 的分支 {check.BranchName} 吗？";
        var answer = MessageBox.Show(
            this,
            message,
            force ? "分支尚未合并" : "删除分支",
            MessageBoxButton.YesNo,
            force ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await viewModel.DeleteBranchAsync(branch, force);
        }
    }

    private async void MergeBranch_OnClick(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is not BranchInfo branch)
        {
            return;
        }
        if (MessageBox.Show(this,
                $"把 {branch.FriendlyName} 合并到 {viewModel.Head?.BranchName ?? viewModel.CurrentBranch}？",
                "合并预览", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            await viewModel.MergeBranchAsync(branch);
        }
    }

    private async void FileTree_OnSelectedItemChanged(
        object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selectedTreeItem = e.NewValue as FileTreeItem;
        await viewModel.SelectFileAsync(selectedTreeItem);
    }

    private async void FileTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (selectedTreeItem is null || selectedTreeItem.IsDirectory)
        {
            return;
        }

        await viewModel.SelectFileAsync(selectedTreeItem);
        if (viewModel.IsExternalOnlyDocument &&
            viewModel.CanOpenCurrentDocumentExternally)
        {
            await viewModel.OpenFileExternallyAsync(selectedTreeItem.FullPath);
            e.Handled = true;
        }
    }

    private async void CommitGraph_OnCommitSelected(object? sender, CommitSelectedEventArgs e) =>
        await viewModel.SelectCommitAsync(e.Commit);

    private async void CommitGraph_OnBranchSelected(object? sender, BranchSelectedEventArgs e) =>
        await viewModel.SelectBranchAsync(e.Branch);

    private async void ChangeList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is FileChange change)
        {
            await viewModel.SelectChangeAsync(change);
        }
    }

    private async void ChangeList_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem
            {
                DataContext: FileChange clickedChange
            } ||
            listBox.SelectedItem is not FileChange selectedChange ||
            !selectedChange.Equals(clickedChange))
        {
            return;
        }

        await viewModel.SelectChangeAsync(clickedChange);
    }

    private void ChangeList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            ((ListBox)sender).SelectedItem is FileChange change &&
            (e.GetPosition(this) - dragStart).Length > 5)
        {
            DragDrop.DoDragDrop((DependencyObject)sender, change, DragDropEffects.Move);
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            dragStart = e.GetPosition(this);
        }
    }

    private async void StagedList_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(FileChange)) is FileChange change && !change.IsStaged)
        {
            await viewModel.StageCommand.ExecuteAsync(change);
        }
    }

    private async void UnstagedList_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(FileChange)) is FileChange change && change.IsStaged)
        {
            await viewModel.UnstageCommand.ExecuteAsync(change);
        }
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            await TryOpenOrInitializeAsync(paths[0]);
            return;
        }
        if (!viewModel.HasRepository)
        {
            MessageBox.Show(this, "请先打开一个仓库，再把文件拖入工作区。", "Git 可视化");
            return;
        }
        foreach (var source in paths.Where(File.Exists))
        {
            var destination = Path.Combine(viewModel.ActiveRepositoryPath, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                MessageBox.Show(this, $"目标已存在，未覆盖：\n{destination}", "导入文件");
                continue;
            }
            File.Copy(source, destination);
        }
        await viewModel.RefreshAsync();
    }

    private async void CreateBranch_OnClick(object sender, RoutedEventArgs e)
    {
        var name = Prompt("创建分支", "输入新分支名称：");
        if (name is not null)
        {
            await viewModel.CreateBranchAsync(name);
        }
    }

    private async void CherryPick_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "把所选提交应用到当前分支？", "拣选提交",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            await viewModel.CherryPickSelectedAsync();
        }
    }

    private async void Revert_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "创建一个新提交来撤销所选提交？", "安全撤销",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            await viewModel.RevertSelectedAsync();
        }
    }

    private async void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedCommit is not { } commit)
        {
            MessageBox.Show(this, "请先选择一个提交。", "回退当前分支",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ResetModeWindow(
            viewModel.CurrentBranch,
            commit.ShortId,
            commit.Message)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.ResetSelectedAsync(dialog.SelectedMode);
        }
    }

    private async void StageSelectedHunks_OnClick(object sender, RoutedEventArgs e) =>
        await viewModel.ApplySelectedHunksAsync(
            HunkList.SelectedItems.Cast<DiffHunk>().ToArray(),
            unstage: false);

    private async void UnstageSelectedHunks_OnClick(object sender, RoutedEventArgs e) =>
        await viewModel.ApplySelectedHunksAsync(
            HunkList.SelectedItems.Cast<DiffHunk>().ToArray(),
            unstage: true);

    private async void Pull_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository)
        {
            return;
        }
        var dialog = new PullStrategyWindow(
            viewModel.Remotes,
            viewModel.Branches,
            viewModel.SelectedRemote,
            viewModel.SavedPullStrategy)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var remote = dialog.SelectedRemote;
        if (remote is null || string.IsNullOrWhiteSpace(dialog.SelectedRemoteBranch))
        {
            MessageBox.Show(this, "请选择远程仓库和远程分支。", "拉取");
            return;
        }
        viewModel.SelectedRemote = remote;
        var remoteName = remote.Name;
        var branchName = viewModel.Head?.BranchName ?? viewModel.CurrentBranch;
        var result = await viewModel.PullAsync(
            remote,
            dialog.SelectedRemoteBranch,
            dialog.SelectedStrategy);
        if (result.Success && !viewModel.HasConflicts)
        {
            MessageBox.Show(
                this,
                $"远程更新已成功拉取。\n\n" +
                $"远程仓库：{remoteName}\n" +
                $"当前分支：{branchName}\n" +
                $"拉取方式：{PullStrategyDisplayName(dialog.SelectedStrategy)}\n\n" +
                $"结果：{result.Summary}",
                "拉取完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else if (!result.Success)
        {
            MessageBox.Show(
                this,
                result.ErrorMessage ?? result.Summary,
                "拉取失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string PullStrategyDisplayName(PullStrategy strategy) => strategy switch
    {
        PullStrategy.Rebase => "把本地修改接到远程更新之后",
        PullStrategy.FastForwardOnly => "仅在没有分歧时更新",
        _ => "保留双方修改并合并"
    };

    private async void TagManagement_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TagManagementWindow(viewModel.Tags) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var result = dialog.Action == TagManagementAction.Create
            ? await viewModel.CreateTagAsync(dialog.TagName, viewModel.SelectedCommit?.Id)
            : await viewModel.DeleteTagAsync(dialog.TagName);
        ShowOperationFailure(result, "标签操作失败");
    }

    private async void StashManagement_OnClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StashInfo> stashes;
        try
        {
            stashes = await viewModel.GetStashesAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法读取临时现场", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var dialog = new StashManagementWindow(stashes) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var result = dialog.Action switch
        {
            StashManagementAction.Save => await viewModel.SaveStashAsync(dialog.StashMessage),
            StashManagementAction.Apply => await viewModel.ApplyStashAsync(dialog.SelectedIndex, false),
            StashManagementAction.Pop => await viewModel.ApplyStashAsync(dialog.SelectedIndex, true),
            StashManagementAction.Delete => await viewModel.DeleteStashAsync(dialog.SelectedIndex),
            _ => null
        };
        if (result is not null)
        {
            ShowOperationFailure(result, "临时现场操作失败");
        }
    }

    private async void Rebase_OnClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.Head?.IsDetached == true)
        {
            MessageBox.Show(this, "请先切换到本地分支，再执行变基。", "变基");
            return;
        }
        var branches = viewModel.Branches
            .Where(branch => !branch.IsCurrent)
            .Select(branch => branch.FriendlyName)
            .ToArray();
        var dialog = new RebaseWindow(branches) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var result = await viewModel.RebaseOntoAsync(dialog.UpstreamBranch, dialog.OntoBranch);
            ShowOperationFailure(result, "变基失败");
        }
    }

    private async void ForcePush_OnClick(object sender, RoutedEventArgs e)
    {
        var remote = viewModel.SelectedRemote;
        var branchName = viewModel.Head?.BranchName;
        if (remote is null || string.IsNullOrWhiteSpace(branchName))
        {
            MessageBox.Show(this, "请先选择远程仓库，并确保当前位于本地分支。", "Force-with-lease 推送");
            return;
        }
        var dialog = new ForcePushConfirmationWindow(remote.Name, branchName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunPushAsync(remote, forceWithLease: true);
        }
    }

    private void ShowOperationFailure(GitOperationResult result, string title)
    {
        if (!result.Success)
        {
            MessageBox.Show(this, result.ErrorMessage ?? result.Summary, title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Identity_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository)
        {
            MessageBox.Show(this, "请先打开一个仓库。", "Git 身份");
            return;
        }
        var name = Prompt("Git 身份", "用户名：", Environment.UserName);
        if (name is null)
        {
            return;
        }
        var email = Prompt("Git 身份", "邮箱：", $"{Environment.UserName}@local.invalid");
        if (email is null)
        {
            return;
        }
        var scope = MessageBox.Show(
            this,
            "选择“是”设置为所有仓库的默认身份；选择“否”只修改当前仓库。",
            "配置范围",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (scope == MessageBoxResult.Cancel)
        {
            return;
        }
        await viewModel.ConfigureIdentityAsync(new GitIdentity(name, email), scope == MessageBoxResult.Yes);
    }

    private async void Credential_OnClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.Remotes.Count == 0)
        {
            MessageBox.Show(this, "当前仓库没有远程地址。", "远程凭据");
            return;
        }
        var remote = viewModel.SelectedRemote ?? viewModel.Remotes[0];
        var url = remote.FetchUrl;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            (url.Contains('@') && url.Contains(':')))
        {
            await viewModel.SaveRemoteCredentialAsync(
                remote,
                new RemoteCredential(CredentialKind.SshAgent));
            MessageBox.Show(
                this,
                "此远程将使用 Windows SSH Agent。请先把私钥加入系统 SSH Agent；应用不会读取或记录私钥内容。",
                "SSH 凭据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var savedCredential = await viewModel.LoadSavedRemoteCredentialAsync(remote);
        var dialog = new CredentialWindow(remote.FetchUrl, savedCredential) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (dialog.DeleteRequested)
            {
                await viewModel.DeleteRemoteCredentialAsync(remote);
            }
            else
            {
                await viewModel.SaveRemoteCredentialAsync(remote, dialog.Credential);
            }
        }
    }

    private async void NewFile_OnClick(object sender, RoutedEventArgs e) =>
        await CreateFileSystemItemAsync(false, "新建 文本文档.txt");

    private async void NewFolder_OnClick(object sender, RoutedEventArgs e) =>
        await CreateFileSystemItemAsync(true, "新建文件夹");

    private async void NewItemMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository || sender is not Button button || button.ContextMenu is null)
        {
            return;
        }
        await PopulateSystemNewTypesAsync();
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async Task PopulateSystemNewTypesAsync()
    {
        SystemNewTypesMenuItem.Items.Clear();
        SystemNewTypesMenuItem.Items.Add(new MenuItem
        {
            Header = "正在读取系统新建类型…",
            IsEnabled = false
        });

        try
        {
            var types = (await viewModel.GetSystemNewFileTypesAsync())
                .Where(type => !BuiltInNewFileExtensions.Contains(type.Extension))
                .ToArray();
            SystemNewTypesMenuItem.Items.Clear();
            foreach (var type in types)
            {
                var item = new MenuItem
                {
                    Header = $"{type.DisplayName} ({type.Extension})",
                    Tag = type,
                    Icon = CreateFileTypeIcon(type.Extension)
                };
                item.Click += SystemNewItemType_OnClick;
                SystemNewTypesMenuItem.Items.Add(item);
            }

            if (types.Length == 0)
            {
                SystemNewTypesMenuItem.Items.Add(new MenuItem
                {
                    Header = "未发现其他安全的系统模板",
                    IsEnabled = false
                });
            }
        }
        catch (Exception exception)
        {
            SystemNewTypesMenuItem.Items.Clear();
            SystemNewTypesMenuItem.Items.Add(new MenuItem
            {
                Header = $"读取失败：{exception.Message}",
                IsEnabled = false
            });
        }
    }

    private async void NewItemType_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string type })
        {
            return;
        }
        var (directory, suggestedName) = type switch
        {
            "folder" => (true, "新建文件夹"),
            ".md" => (false, "README.md"),
            ".docx" => (false, "新建 Word 文档.docx"),
            ".cs" => (false, "新建类.cs"),
            ".json" => (false, "data.json"),
            ".xml" => (false, "data.xml"),
            _ => (false, "新建 文本文档.txt")
        };
        await CreateFileSystemItemAsync(directory, suggestedName, type == "folder" ? null : type);
    }

    private async void SystemNewItemType_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: SystemNewFileType type })
        {
            return;
        }

        await CreateFileSystemItemAsync(
            false,
            type.SuggestedFileName,
            type.Extension,
            type);
    }

    private async Task CreateFileSystemItemAsync(
        bool directory,
        string suggestedName,
        string? requiredExtension = null,
        SystemNewFileType? systemType = null)
    {
        if (!viewModel.HasRepository)
        {
            return;
        }
        var parent = selectedTreeItem is null
            ? viewModel.ActiveRepositoryPath
            : selectedTreeItem.IsDirectory
                ? selectedTreeItem.FullPath
                : Path.GetDirectoryName(selectedTreeItem.FullPath) ?? viewModel.ActiveRepositoryPath;
        var name = Prompt(
            directory ? "新建文件夹" : "新建文件",
            $"创建位置：\n{parent}\n\n名称：",
            suggestedName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        name = name.Trim();
        if (name is "." or ".." ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(
                this,
                "请输入不包含路径分隔符的有效名称。",
                "名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (requiredExtension is not null)
        {
            var actualExtension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(actualExtension))
            {
                name += requiredExtension;
            }
            else if (systemType is not null &&
                     !actualExtension.Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    $"该模板要求文件名使用 {requiredExtension} 扩展名。",
                    "扩展名不匹配",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        try
        {
            if (systemType is null)
            {
                await viewModel.CreateFileAsync(parent, name, directory);
            }
            else
            {
                await viewModel.CreateSystemFileAsync(parent, name, systemType);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                directory ? "无法创建文件夹" : "无法创建文件",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RenameFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (selectedTreeItem is null)
        {
            return;
        }
        var name = Prompt("重命名", "新名称：", selectedTreeItem.Name);
        if (name is not null && !name.Equals(selectedTreeItem.Name, StringComparison.Ordinal))
        {
            await viewModel.MoveFileAsync(selectedTreeItem.FullPath, name);
        }
    }

    private async void DeleteFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (selectedTreeItem is null)
        {
            return;
        }
        if (MessageBox.Show(
                this,
                $"删除以下项目？Git 会把删除记录为工作区变化。\n\n{selectedTreeItem.FullPath}",
                "删除文件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await viewModel.DeleteFileAsync(selectedTreeItem.FullPath);
        }
    }

    private void ConflictList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        viewModel.SelectConflict(((ListBox)sender).SelectedItem as ConflictFile);

    private void UseOurs_OnClick(object sender, RoutedEventArgs e) =>
        viewModel.UseConflictSide(ConflictSide.Ours);

    private void UseTheirs_OnClick(object sender, RoutedEventArgs e) =>
        viewModel.UseConflictSide(ConflictSide.Theirs);

    private void UseBoth_OnClick(object sender, RoutedEventArgs e) =>
        viewModel.UseConflictSide(ConflictSide.Both);

    private async void ResolveConflict_OnClick(object sender, RoutedEventArgs e) =>
        await viewModel.ResolveSelectedConflictAsync();

    private async void ResolveBinaryOurs_OnClick(object sender, RoutedEventArgs e) =>
        await ResolveBinaryConflictAsync(ConflictSide.Ours);

    private async void ResolveBinaryTheirs_OnClick(object sender, RoutedEventArgs e) =>
        await ResolveBinaryConflictAsync(ConflictSide.Theirs);

    private async void ResolveBinaryCurrentFile_OnClick(object sender, RoutedEventArgs e) =>
        await ResolveBinaryConflictAsync(ConflictSide.CurrentFile);

    private async Task ResolveBinaryConflictAsync(ConflictSide side)
    {
        var label = side switch
        {
            ConflictSide.Ours => "当前版本（ours）",
            ConflictSide.Theirs => "对方版本（theirs）",
            _ => "当前工作区文件"
        };
        if (MessageBox.Show(
                this,
                $"采用{label}的原始字节并标记该冲突为已解决？",
                "解决二进制冲突",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await viewModel.ResolveSelectedBinaryConflictAsync(side);
        }
    }

    private async void ContinueOperation_OnClick(object sender, RoutedEventArgs e) =>
        await viewModel.ContinueOperationAsync();

    private async void AbortOperation_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "中止当前 Git 操作并恢复到操作前状态？",
                "中止操作",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await viewModel.AbortOperationAsync();
        }
    }

    private async void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            await viewModel.SaveEditorCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            await viewModel.RefreshAsync();
            e.Handled = true;
        }
    }

    private string? Prompt(string title, string text, string initialValue = "")
    {
        var dialog = new TextPromptWindow(title, text, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private static string GuessRepositoryName(string url)
    {
        var value = url.TrimEnd('/', '\\');
        var index = Math.Max(value.LastIndexOf('/'), value.LastIndexOf(':'));
        var name = index >= 0 ? value[(index + 1)..] : value;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return Path.GetFullPath(left)
            .Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static Image? CreateFileTypeIcon(string extension)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(
            "file" + extension,
            FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetFileInfoIcon | ShellGetFileInfoSmallIcon | ShellGetFileInfoUseFileAttributes);
        if (result == nint.Zero || info.IconHandle == nint.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return new Image
            {
                Source = source,
                Width = 16,
                Height = 16
            };
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private const uint FileAttributeNormal = 0x80;
    private const uint ShellGetFileInfoIcon = 0x100;
    private const uint ShellGetFileInfoSmallIcon = 0x1;
    private const uint ShellGetFileInfoUseFileAttributes = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo shellFileInfo,
        uint shellFileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
