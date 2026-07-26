using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitVisualizer.App.Controls;
using GitVisualizer.App.Dialogs;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using Microsoft.Win32;

namespace GitVisualizer.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private Point dragStart;
    private FileTreeItem? selectedTreeItem;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
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
        var urlDialog = new TextPromptWindow("克隆远程仓库", "输入 HTTPS、SSH 或本地 Git 仓库地址：")
        {
            Owner = this
        };
        if (urlDialog.ShowDialog() != true)
        {
            return;
        }
        var folderDialog = new OpenFolderDialog { Title = "选择克隆目标的父文件夹" };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }
        var repositoryName = GuessRepositoryName(urlDialog.Value);
        var destination = Path.Combine(folderDialog.FolderName, repositoryName);
        await viewModel.CloneRepositoryAsync(urlDialog.Value, destination, null);
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

    private async void RepositorySort_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string mode)
        {
            await viewModel.SortRepositoriesAsync(mode);
        }
    }

    private async void BranchList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BranchList.SelectedItem is BranchInfo branch && !branch.IsCurrent && !branch.IsRemote)
        {
            await viewModel.CheckoutBranchAsync(branch);
        }
    }

    private async void CheckoutBranch_OnClick(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is BranchInfo branch)
        {
            await viewModel.CheckoutBranchAsync(branch);
        }
    }

    private async void MergeBranch_OnClick(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is not BranchInfo branch)
        {
            return;
        }
        if (MessageBox.Show(this,
                $"把 {branch.FriendlyName} 合并到 {viewModel.CurrentBranch}？",
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

    private void CommitGraph_OnCommitSelected(object? sender, CommitSelectedEventArgs e) =>
        viewModel.SelectCommit(e.Commit);

    private async void ChangeList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is FileChange change)
        {
            await viewModel.SelectChangeAsync(change);
        }
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
        var mode = Prompt("Reset", "输入模式：soft、mixed 或 hard", "mixed");
        if (mode is null)
        {
            return;
        }
        if (!Enum.TryParse<GitResetMode>(mode, true, out var resetMode))
        {
            MessageBox.Show(this, "模式必须是 soft、mixed 或 hard。", "Reset");
            return;
        }
        var preview = viewModel.Preview(resetMode == GitResetMode.Hard ? "reset-hard" : "reset");
        if (MessageBox.Show(this,
                $"{preview.Description}\n\n{preview.EquivalentCommand}\n\n执行前会创建恢复点。是否继续？",
                "高风险操作预览", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await viewModel.ResetSelectedAsync(resetMode);
        }
    }

    private async void Pull_OnClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasRepository)
        {
            return;
        }
        var defaultValue = viewModel.SavedPullStrategy switch
        {
            PullStrategy.Rebase => "rebase",
            PullStrategy.FastForwardOnly => "ff-only",
            _ => "merge"
        };
        var value = Prompt(
            "拉取策略",
            "本地和远程都产生提交时，选择 merge、rebase 或 ff-only。\n此选择会按仓库记住：",
            defaultValue);
        if (value is null)
        {
            return;
        }
        var strategy = value.ToLowerInvariant() switch
        {
            "merge" => PullStrategy.Merge,
            "rebase" => PullStrategy.Rebase,
            "ff-only" => PullStrategy.FastForwardOnly,
            _ => PullStrategy.Ask
        };
        if (strategy == PullStrategy.Ask)
        {
            MessageBox.Show(this, "请输入 merge、rebase 或 ff-only。", "拉取策略");
            return;
        }
        await viewModel.PullAsync(strategy);
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
        var url = viewModel.Remotes[0].FetchUrl;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            (url.Contains('@') && url.Contains(':')))
        {
            await viewModel.SaveRemoteCredentialAsync(new RemoteCredential(CredentialKind.SshAgent));
            MessageBox.Show(
                this,
                "此远程将使用 Windows SSH Agent。请先把私钥加入系统 SSH Agent；应用不会读取或记录私钥内容。",
                "SSH 凭据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var dialog = new CredentialWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.SaveRemoteCredentialAsync(dialog.Credential);
        }
    }

    private async void NewFile_OnClick(object sender, RoutedEventArgs e) =>
        await CreateFileSystemItemAsync(false);

    private async void NewFolder_OnClick(object sender, RoutedEventArgs e) =>
        await CreateFileSystemItemAsync(true);

    private async Task CreateFileSystemItemAsync(bool directory)
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
        var name = Prompt(directory ? "新建文件夹" : "新建文件", "名称：");
        if (name is not null)
        {
            await viewModel.CreateFileAsync(parent, name, directory);
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

    private void AutoSaveCheckBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (viewModel.AutoSave)
        {
            return;
        }
        if (MessageBox.Show(this,
                "开启自动保存后，输入内容会在 1 秒后写入磁盘并立即改变 Git 工作区。\n是否继续？",
                "自动保存风险提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Handled = true;
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
}
