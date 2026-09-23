// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitVisualizer.App.Controls;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Diagnostics;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Persistence;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Benchmarks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[3] == "--refresh-large-only")
        {
            var refreshOutput = Path.GetFullPath(args[1]);
            if (Directory.Exists(refreshOutput)) throw new IOException("Output must be new.");
            Directory.CreateDirectory(refreshOutput);
            Environment.SetEnvironmentVariable("GITVISUALIZER_DATA_ROOT", Path.Combine(refreshOutput, "profile"));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Exception? refreshFailure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunRefreshOnlyAsync(Path.Combine(Path.GetFullPath(args[0]), "large-files"), refreshOutput, int.Parse(args[2])); }
                catch (Exception error) { refreshFailure = error; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
            if (refreshFailure is null) return 0;
            Console.Error.WriteLine(refreshFailure); return 1;
        }
        if (args.Length is < 2 or > 4) { Console.Error.WriteLine("Usage: GitVisualizer.Benchmarks <fixtures-root> <new-output-directory> [repetitions=30] [--ui-only]"); return 2; }
        var repetitions = args.Length >= 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 30;
        var uiOnly = args.Length == 4 && args[3] == "--ui-only";
        var smallOnly = args.Length == 4 && args[3] == "--small-only";
        var largeReadOnly = args.Length == 4 && args[3] == "--large-read-only";
        var presented = args.Length == 4 && args[3] == "--presented-tree";
        var externalSync = args.Length == 4 && args[3] == "--external-sync";
        if (args.Length == 4 && !uiOnly && !smallOnly && !largeReadOnly && !presented && !externalSync) throw new ArgumentException("Unknown measurement mode.");
        if (repetitions is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(repetitions));
        var fixtures = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Output directory must be new.");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("GITVISUALIZER_DATA_ROOT", Path.Combine(output, "profile"));
        if (!LocalPaths.Default.IsIsolated) throw new InvalidOperationException("An isolated data profile is required.");
        // A real dispatcher preserves WPF thread affinity over awaits without showing a window.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { if (externalSync) await RunExternalSyncAsync(output, repetitions);
                  else if (presented) await RunPresentedTreeAsync(fixtures, output, repetitions);
                  else if (largeReadOnly) await RunLargeReadAsync(fixtures, output, repetitions);
                  else if (smallOnly) await RunSmallAsync(output, repetitions);
                  else await RunAsync(fixtures, output, repetitions, uiOnly); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }));
        Dispatcher.Run();
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task RunAsync(string fixtures, string output, int repetitions, bool uiOnly)
    {
        var metadata = new
        {
            schemaVersion = 1, configuration = "Release", runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString(), cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            processors = Environment.ProcessorCount, debuggerAttached = Debugger.IsAttached,
            startedUtc = DateTimeOffset.UtcNow, repetitions, uiOnly, warmup = 1, memorySamplingIntervalMs = 10,
            viewportWidth = 800, viewportHeight = 600,
            fixtureManifestSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(fixtures, "manifest.json")))),
            appSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(typeof(MainWindowViewModel).Assembly.Location))),
            note = "First-use samples are separate; no OS cache flush. Memory counters are process-wide. Timed method scopes include waits and early returns; harness checks operation results."
        };
        if (Debugger.IsAttached) throw new InvalidOperationException("Run Release benchmarks without a debugger.");
        await File.WriteAllTextAsync(Path.Combine(output, "conditions.json"), JsonSerializer.Serialize(metadata));
        foreach (var fixture in new[] { "files-10000", "files-100000", "history-100000-200", "large-files", "conflicts-100" })
        {
            if (uiOnly) break;
            var path = Path.Combine(fixtures, fixture);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(fixture);
            for (var iteration = -1; iteration < repetitions; iteration++)
            {
                var name = fixture + (iteration == -1 ? "-first-use" : $"-{iteration:00}");
                using var recorder = new PerformanceRecorder(Path.Combine(output, name + ".jsonl"));
                PerformanceRecorder.Current = recorder;
                try
                {
                    using var viewModel = CreateViewModel();
                    using (recorder.Measure(PerformanceOperation.OpenRepository))
                        if (!await viewModel.OpenRepositoryAsync(path)) throw new InvalidOperationException("Open failed: " + fixture);
                    using (recorder.Measure(PerformanceOperation.Refresh)) await viewModel.RefreshAsync();
                    if (viewModel.StatusText.StartsWith("刷新失败", StringComparison.Ordinal))
                        throw new InvalidOperationException("Refresh failed: " + fixture);
                    if (fixture == "history-100000-200")
                    {
                        for (var page = 0; page < 5; page++) await viewModel.LoadMoreHistoryCommand.ExecuteAsync(null);
                        if (viewModel.History.Count != 1200 || viewModel.History.Select(c => c.Id).Distinct().Count() != 1200)
                            throw new InvalidDataException("History page count/uniqueness failed.");
                    }
                    // Record existing tree truncation as a baseline observation, never claim all files are displayed.
                    if (iteration == -1)
                        await File.WriteAllTextAsync(Path.Combine(output, fixture + "-tree.json"),
                            JsonSerializer.Serialize(new { fixture, materializedNodes = CountNodes(viewModel.FileTree) }));
                }
                finally { PerformanceRecorder.Current = null; }
                recorder.Dispose();
                EnsureRecorder(recorder);
            }
            Console.WriteLine("Measured " + fixture);
        }

        var service = new LibGitRepositoryService(new RecoveryService(), new OperationLogStore());
        // The public history API caps a request at 1000; load ten pages without changing that behavior.
        var commits = new List<CommitNode>();
        for (var offset = 0; offset < 10000; offset += 1000)
            commits.AddRange(await service.GetHistoryAsync(Path.Combine(fixtures, "history-100000-200"), offset, 1000));
        if (commits.Count != 10000 || commits.Select(c => c.Id).Distinct().Count() != 10000)
            throw new InvalidDataException("Graph requires exactly 10000 distinct commits.");
        var snapshot = await service.GetSnapshotAsync(Path.Combine(fixtures, "history-100000-200"));
        var graph = new CommitGraphControl
        {
            Items = new ObservableCollection<CommitNode>(commits), Branches = snapshot.Branches, Head = snapshot.Head
        };
        var viewport = new ScrollViewer { Content = graph, Width = 800, Height = 600 };
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.UpdateLayout();
        var directoryPath = Path.Combine(fixtures, "files-100000", "dir-0000");
        var directory = FileTreeItem.Create(directoryPath, 1);
        var lazyLoad = typeof(FileTreeItem).GetMethod("LoadChildrenAsync");
        var children = new System.Windows.Data.Binding(nameof(FileTreeItem.Children));
        var template = new HierarchicalDataTemplate(typeof(FileTreeItem)) { ItemsSource = children };
        var tree = new TreeView { Width = 800, Height = 600, ItemTemplate = template };
        tree.Items.Add(directory);
        // The archived baseline has an eager tree; the current implementation records
        // its actual background enumeration and UI-side batched collection application.
        if (lazyLoad is null) DirectoryExpansionMeasurement.Attach(tree);
        tree.Measure(new Size(800, 600));
        tree.Arrange(new Rect(0, 0, 800, 600));
        tree.UpdateLayout();
        for (var iteration = -1; iteration < repetitions; iteration++)
        {
            using var recorder = new PerformanceRecorder(Path.Combine(output,
                iteration == -1 ? "ui-first-use.jsonl" : $"ui-{iteration:00}.jsonl"));
            PerformanceRecorder.Current = recorder;
            try
            {
                directory = FileTreeItem.Create(directoryPath, 1);
                tree.Items.Clear();
                tree.Items.Add(directory);
                tree.UpdateLayout();
                if (lazyLoad is not null)
                {
                    // An off-screen WPF TreeView may virtualize its root container. The tree
                    // item's expansion handler calls this same method; invoking it here keeps
                    // the actual dispatcher-side collection batches measurable without relying
                    // on a presentation source that does not exist in the benchmark harness.
                    using (recorder.Measure(PerformanceOperation.DirectoryExpand))
                        await (Task)lazyLoad.Invoke(directory, new object[] { CancellationToken.None, false })!;
                }
                if (directory.Children.Count != 1000)
                    throw new InvalidDataException($"Directory benchmark requires 1000 actual nodes, got {directory.Children.Count}.");
                graph.InvalidateVisual();
                viewport.UpdateLayout();
                var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(viewport);
                tree.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            finally { PerformanceRecorder.Current = null; }
            recorder.Dispose();
            EnsureRecorder(recorder);
        }
        Console.WriteLine("Measured graph and directory expansion; completed.");
    }

    private static async Task RunExternalSyncAsync(string output, int repetitions)
    {
        if (Debugger.IsAttached) throw new InvalidOperationException("Debugger attached.");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Localization/Strings.zh-CN.xaml", "Themes/Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/GitVisualizer;component/" + resource, UriKind.Relative) });
        var root = Path.Combine(output, "repository");
        Directory.CreateDirectory(root);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Benchmark");
        await Git("config", "user.email", "benchmark@example.invalid");
        await Git("commit", "--allow-empty", "-m", "base");
        await Git("branch", "other");
        using var vm = CreateViewModel(liveWatcher: true);
        var window = new GitVisualizer.App.MainWindow(vm) { ShowActivated = false, Width = 1280, Height = 800 };
        var samples = new List<object>();
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!await vm.OpenRepositoryAsync(root)) throw new IOException("Open failed.");
            for (int iteration = -1; iteration < repetitions; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                await File.WriteAllTextAsync(Path.Combine(root, "state.txt"), $"iteration {iteration}\n");
                await Git("add", "state.txt");
                await Converge("stage", () => vm.StagedChanges.Any(x => x.Path == "state.txt"), started, iteration);
                started = Stopwatch.GetTimestamp();
                await Git("commit", "-m", $"external {iteration}");
                var id = (await Git("rev-parse", "HEAD")).Trim();
                await Converge("commit", () => vm.Head?.CommitId == id && vm.History.Any(x => x.Id == id)
                    && vm.StagedChanges.Count == 0, started, iteration);
                started = Stopwatch.GetTimestamp();
                await Git("switch", "other");
                var other = (await Git("rev-parse", "HEAD")).Trim();
                await Converge("checkout-other", () => vm.Head?.CommitId == other && vm.Head.BranchName == "other", started, iteration);
                started = Stopwatch.GetTimestamp();
                await Git("switch", "main");
                await Converge("checkout-main", () => vm.Head?.CommitId == id && vm.Head.BranchName == "main", started, iteration);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "external-sync.json"), JsonSerializer.Serialize(new
            {
                configuration = "Release", debuggerAttached = Debugger.IsAttached, repetitions, samples,
                note = "Actual MainWindow and RepositoryWatcher; separate Git processes. Time covers external operation start to bound-model convergence, stricter than refresh start. No manual refresh is called."
            }));
        }
        finally { window.Close(); }
        async Task Converge(string operation, Func<bool> ready, long start, int iteration)
        {
            while (!ready())
            {
                if (Stopwatch.GetElapsedTime(start).TotalSeconds > 2)
                    throw new InvalidDataException($"External {operation} did not converge within 2 seconds: {vm.StatusText}");
                await Task.Delay(10);
            }
            samples.Add(new { operation, iteration, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
        }
        async Task<string> Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Git start failed.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(await stderr);
            return await stdout;
        }
    }

    private static async Task RunPresentedTreeAsync(string fixtures, string output, int repetitions)
    {
        if (Debugger.IsAttached) throw new InvalidOperationException("Debugger attached.");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Localization/Strings.zh-CN.xaml", "Themes/Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/GitVisualizer;component/" + resource, UriKind.Relative) });
        using var vm = CreateViewModel();
        var window = new GitVisualizer.App.MainWindow(vm) { ShowActivated = false, Width = 1280, Height = 800 };
        var samples = new List<object>();
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!await vm.OpenRepositoryAsync(Path.Combine(fixtures, "files-100000")))
                throw new IOException("Presented fixture open failed.");
            var tree = (TreeView)window.FindName("FileTreeView");
            for (int iteration = -1; iteration < repetitions; iteration++)
            {
                var directory = FileTreeItem.Create(Path.Combine(fixtures, "files-100000", "dir-0000"));
                vm.FileTree.Clear();
                vm.FileTree.Add(directory);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                tree.UpdateLayout();
                var container = tree.ItemContainerGenerator.ContainerFromItem(directory) as TreeViewItem
                    ?? throw new InvalidDataException("Presented tree root has no container.");
                var operations = new Dictionary<DispatcherOperation, long>();
                double maximumOperationMs = 0;
                void Started(object? sender, DispatcherHookEventArgs e) => operations[e.Operation] = Stopwatch.GetTimestamp();
                void Completed(object? sender, DispatcherHookEventArgs e)
                {
                    if (operations.Remove(e.Operation, out var start))
                        maximumOperationMs = Math.Max(maximumOperationMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                var hooks = Dispatcher.CurrentDispatcher.Hooks;
                hooks.OperationStarted += Started;
                hooks.OperationCompleted += Completed;
                var started = Stopwatch.GetTimestamp();
                using var recorder = new PerformanceRecorder(Path.Combine(output, $"presented-{iteration}.jsonl"));
                PerformanceRecorder.Current = recorder;
                try
                {
                    container.IsExpanded = true;
                    await directory.LoadChildrenAsync();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    tree.UpdateLayout();
                    if (directory.Children.Count != 1000 || directory.Children.Any(x => x.IsPlaceholder))
                        throw new InvalidDataException("Presented tree lost directory entries.");
                    samples.Add(new { iteration, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        maximumOperationMs, nodes = directory.Children.Count, expanded = container.IsExpanded });
                }
                finally
                {
                    PerformanceRecorder.Current = null;
                    hooks.OperationStarted -= Started;
                    hooks.OperationCompleted -= Completed;
                }
                recorder.Dispose();
                EnsureRecorder(recorder);
                if (!directory.IsExpanded) throw new InvalidDataException("Expanded state did not reach the retained node.");
                var scroll = FindScroll(tree) ?? throw new InvalidDataException("Tree has no scroll viewer.");
                scroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                tree.UpdateLayout();
                if (container.ItemContainerGenerator.ContainerFromItem(directory.Children[^1]) is not TreeViewItem last)
                    throw new InvalidDataException("The last file is not accessible after scrolling.");
                last.IsSelected = true;
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (!directory.Children[^1].IsSelected || !directory.IsExpanded)
                    throw new InvalidDataException("Virtualization lost selection or expansion state.");
                container.IsExpanded = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "presented-tree.json"), JsonSerializer.Serialize(new
            {
                configuration = "Release", debuggerAttached = Debugger.IsAttached, repetitions,
                note = "Actual MainWindow, template and expansion handler. Dispatcher operation duration includes layout/render callbacks; iteration -1 is first use.", samples
            }));
        }
        finally { window.Close(); }

        static ScrollViewer? FindScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer scroll) return scroll;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
    }

    private static async Task RunLargeReadAsync(string fixtures, string output, int repetitions)
    {
        if (Debugger.IsAttached) throw new InvalidOperationException("Debugger attached.");
        var path = Path.Combine(fixtures, "large-files");
        var git = new LibGitRepositoryService(new RecoveryService(), new OperationLogStore());
        var commit = (await git.GetSnapshotAsync(path)).Head.CommitId;
        var samples = new List<object>();
        foreach (var scenario in new[] { "working-1GiB", "working-64MiB", "historical-64MiB" })
        for (int iteration = -1; iteration < repetitions; iteration++)
        {
            using var process = Process.GetCurrentProcess();
            var peakBefore = process.PeakWorkingSet64;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var start = Stopwatch.GetTimestamp();
            var document = scenario == "historical-64MiB"
                ? await git.OpenCommitFileAsync(path, commit, "large-64mib.txt")
                : await TextFileStorage.OpenAsync(Path.Combine(path, scenario == "working-1GiB" ? "one-gib.bin" : "large-64mib.txt"));
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            process.Refresh();
            var peakGrowthBytes = Math.Max(0, process.PeakWorkingSet64 - peakBefore);
            if (!document.IsReadOnly || document.Text.Length != 0 || document.ContentBytes is not null)
                throw new InvalidDataException("Large file contents entered the editor.");
            if (allocatedBytes >= 32L * 1024 * 1024 || peakGrowthBytes >= 32L * 1024 * 1024)
                throw new InvalidDataException("Large-file read exceeded the 32 MiB allocation/peak-growth target.");
            samples.Add(new { scenario, iteration, elapsedMs, allocatedBytes, peakGrowthBytes, size = document.Size });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "large-read-times.json"), JsonSerializer.Serialize(new
        {
            repetitions, configuration = "Release", debuggerAttached = Debugger.IsAttached,
            appSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(typeof(MainWindowViewModel).Assembly.Location))), samples,
            note = "Managed allocation and process lifetime peak-working-set growth; first-use is iteration -1."
        }));
        Console.WriteLine("Large reads stayed read-only and below allocation/peak-growth budgets.");
    }

    private static async Task RunSmallAsync(string output, int repetitions)
    {
        if (Debugger.IsAttached) throw new InvalidOperationException("Debugger attached.");
        var path = Path.Combine(output, "small-repository");
        Directory.CreateDirectory(path);
        var log = new OperationLogStore();
        var git = new LibGitRepositoryService(new RecoveryService(), log);
        var identity = new GitIdentity("Benchmark", "benchmark@example.invalid");
        if (!(await git.InitializeAsync(path, identity)).Success) throw new IOException("Fixture initialization failed.");
        var file = Path.Combine(path, "a.txt");
        await File.WriteAllTextAsync(file, "base\n");
        if (!(await git.StageFilesAsync(path, ["a.txt"])).Success || !(await git.CommitAsync(path, "base", identity)).Success)
            throw new IOException("Fixture commit failed.");
        using var vm = CreateViewModel();
        if (!await vm.OpenRepositoryAsync(path)) throw new IOException("Fixture open failed.");
        var samples = new List<object>();
        for (int iteration = -1; iteration < repetitions; iteration++)
        {
            await Measure("refresh", () => vm.RefreshAsync());
            if (vm.StatusText.StartsWith("刷新失败", StringComparison.Ordinal)) throw new IOException(vm.StatusText);
            await Measure("open-text", async () =>
            {
                var document = await TextFileStorage.OpenAsync(file);
                if (document.IsReadOnly) throw new IOException("Small file became read-only.");
            });
            await Measure("history-page", async () =>
            {
                var history = await git.GetHistoryAsync(path, 0, 201);
                if (history.Count != 1) throw new IOException("Missing small-repository commit.");
            });
            var original = await TextFileStorage.OpenAsync(file);
            var expected = "iteration " + iteration + "\n";
            await Measure("safe-save", () => TextFileStorage.SaveAsync(path, original, expected));
            if (await File.ReadAllTextAsync(file) != expected) throw new IOException("Save changed file bytes.");
            await Measure("stage-file", async () =>
            {
                if (!(await git.StageFilesAsync(path, ["a.txt"])).Success) throw new IOException("Stage failed.");
            });
            await Measure("unstage-file", async () =>
            {
                if (!(await git.UnstageFilesAsync(path, ["a.txt"])).Success) throw new IOException("Unstage failed.");
            });
            var snapshot = await git.GetSnapshotAsync(path);
            if (snapshot.Changes.Count(x => x.Path == "a.txt" && !x.IsStaged) != 1
                || snapshot.Changes.Any(x => x.IsStaged)) throw new IOException("Index integrity failed.");
            async Task Measure(string operation, Func<Task> action)
            {
                var start = Stopwatch.GetTimestamp();
                await action();
                samples.Add(new { operation, iteration, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "small-times.json"), JsonSerializer.Serialize(new
        {
            configuration = "Release", repetitions, warmup = 1, debuggerAttached = Debugger.IsAttached,
            appSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(typeof(MainWindowViewModel).Assembly.Location))), samples
        }));
        Console.WriteLine("Measured small repository; file, index, and commit integrity verified.");
    }

    private static async Task RunRefreshOnlyAsync(string path, string output, int repetitions)
    {
        if (Debugger.IsAttached || !LocalPaths.Default.IsIsolated) throw new InvalidOperationException("Isolated Release required.");
        var samples = new List<object>();
        for (int iteration = -1; iteration < repetitions; iteration++)
        {
            using var vm = CreateViewModel();
            if (!await vm.OpenRepositoryAsync(path)) throw new IOException("Open failed.");
            var before = vm.UnstagedChanges.Concat(vm.StagedChanges).Select(x => (x.Path, x.State, x.IsStaged)).ToArray();
            var start = Stopwatch.GetTimestamp();
            await vm.RefreshAsync();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (vm.StatusText.StartsWith("刷新失败") || !before.SequenceEqual(vm.UnstagedChanges.Concat(vm.StagedChanges).Select(x => (x.Path, x.State, x.IsStaged))))
                throw new InvalidDataException("Refresh changed fixture status or failed.");
            samples.Add(new { iteration, elapsedMs });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "refresh-only.json"), JsonSerializer.Serialize(new
        { configuration = "Release", debuggerAttached = Debugger.IsAttached, repetitions, samples,
            note = "Whole public RefreshAsync timed without nested memory-recording scopes; first-use separate. Fixture status preserved." }));
    }

    private static int CountNodes(IEnumerable<FileTreeItem> nodes) => nodes.Sum(node => 1 + CountNodes(node.Children));
    private static void EnsureRecorder(PerformanceRecorder recorder)
    {
        if (recorder.DroppedSamples != 0 || recorder.WriteError is not null)
            throw new IOException("Incomplete performance output.", recorder.WriteError);
    }
    private static MainWindowViewModel CreateViewModel(bool liveWatcher = false)
    {
        var recovery = new RecoveryService();
        var log = new OperationLogStore();
        return new MainWindowViewModel(new LibGitRepositoryService(recovery, log), new LibGitDiffService(),
            liveWatcher ? new RepositoryWatcherFactory() : new QuietWatcherFactory(), new FileWorkspaceService(), new WindowsShellNewFileService(),
            new SettingsStore(), log, recovery, new DisabledCredentials(), draftStore: new EditorDraftStore());
    }

    private sealed class DisabledCredentials : ICredentialVault
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class QuietWatcherFactory : IRepositoryWatcherFactory
    {
        public IRepositoryWatcher Create(string repositoryPath) => new QuietWatcher(repositoryPath);
        private sealed class QuietWatcher(string path) : IRepositoryWatcher
        {
            public string RepositoryPath => path;
            public event EventHandler? RepositoryChanged { add { } remove { } }
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
        }
    }
}
