using System.Collections.ObjectModel;
using System.Windows;
using GitVisualizer.App.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class CommitGraphControlTests
{
    [Fact]
    public void CollectionChangesRebuildTheGraphLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var history = new ObservableCollection<CommitNode>();
                var graph = new CommitGraphControl { Items = history };

                history.Add(CreateCommit("11111111", []));
                history.Add(CreateCommit("22222222", ["11111111"]));
                graph.Measure(new Size(800, double.PositiveInfinity));

                Assert.Equal(100, graph.DesiredSize.Height);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void CurrentFirstParentPathStaysOnPrimaryLaneAcrossMerge()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var baseCommit = CreateCommit("aaaaaaaa", []);
                var mainCommit = CreateCommit("bbbbbbbb", [baseCommit.Id]);
                var featureCommit = CreateCommit("cccccccc", [baseCommit.Id]);
                var mergeCommit = CreateCommit(
                    "dddddddd",
                    [mainCommit.Id, featureCommit.Id]);
                var graph = new CommitGraphControl
                {
                    Items = new ObservableCollection<CommitNode>(
                        [mergeCommit, mainCommit, featureCommit, baseCommit]),
                    Head = new HeadInfo(mergeCommit.Id, "main", false)
                };

                Assert.Equal(0, graph.GetLaneForCommit(mergeCommit.Id));
                Assert.Equal(0, graph.GetLaneForCommit(mainCommit.Id));
                Assert.Equal(1, graph.GetLaneForCommit(featureCommit.Id));
                Assert.Equal(0, graph.GetLaneForCommit(baseCommit.Id));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void CollapsingGraphMovesCommitTextToTheLeadingEdge()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var graph = new CommitGraphControl
                {
                    Items = new ObservableCollection<CommitNode>(
                        [CreateCommit("11111111", [])])
                };
                graph.Measure(new Size(800, double.PositiveInfinity));
                graph.Arrange(new Rect(0, 0, 800, 100));
                graph.UpdateLayout();
                var expandedTextStart = graph.GetCommitTextStart();

                graph.IsGraphCollapsed = true;
                graph.UpdateLayout();

                Assert.True(graph.GetCommitTextStart() < expandedTextStart);
                Assert.Equal(12, graph.GetCommitTextStart());
                Assert.Equal(0, graph.GetLaneForCommit("11111111"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static CommitNode CreateCommit(string id, IReadOnlyList<string> parents) =>
        new(
            id,
            id,
            $"提交 {id}",
            "测试用户",
            "test@example.invalid",
            DateTimeOffset.Now,
            parents);
}
