// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Reflection;
using GitVisualizer.App.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class CommitGraphControlTests
{
    [Fact]
    public void CrossViewportConnectionSurvivesCullingAndInteractionReusesCaches()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var commits = Enumerable.Range(0, 10_000)
                    .Select(i => CreateCommit(i.ToString("x8"), i == 0 ? [9999.ToString("x8")] :
                        i == 1 ? [2.ToString("x8")] : [])).ToArray();
                var graph = new CommitGraphControl { Items = new ObservableCollection<CommitNode>(commits) };
                graph.Measure(new Size(800, 600));
                graph.Arrange(new Rect(0, 0, 800, graph.DesiredSize.Height));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object? Field(string name) => typeof(CommitGraphControl).GetField(name, flags)!.GetValue(graph);
                void Set(string name, object value) => typeof(CommitGraphControl).GetField(name, flags)!.SetValue(graph, value);
                void Render()
                {
                    var visual = new DrawingVisual();
                    using var context = visual.RenderOpen();
                    typeof(CommitGraphControl).GetMethod("OnRender", flags)!.Invoke(graph, [context]);
                }
                Render();
                var nodes = Field("nodeById");
                var edges = Field("edgeTree");
                var layout = Field("layout");
                graph.SelectedCommit = commits[5000];
                Set("hoveredLane", 0);
                Render();
                Assert.Same(nodes, Field("nodeById"));
                Assert.Same(edges, Field("edgeTree"));
                Assert.Same(layout, Field("layout"));

                // Both endpoints of the long edge are outside this viewport;
                // the short edge near the first row must be culled.
                Set("viewportTop", 250_000d);
                Set("viewportBottom", 250_600d);
                var connections = new DrawingVisual();
                using (var context = connections.RenderOpen())
                    typeof(CommitGraphControl).GetMethod("DrawParentConnections", flags)!
                        .Invoke(graph, [context, nodes]);
                var connection = Assert.IsType<GeometryDrawing>(Assert.Single(connections.Drawing.Children));
                Assert.True(connection.Geometry.Bounds.Top < 250_000);
                Assert.True(connection.Geometry.Bounds.Bottom > 250_600);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void DesiredWidthTracksTheAvailableViewportWidth()
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

                graph.Measure(new Size(420, double.PositiveInfinity));
                Assert.Equal(420, graph.DesiredSize.Width);

                graph.Measure(new Size(760, double.PositiveInfinity));
                Assert.Equal(760, graph.DesiredSize.Width);
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
                Assert.Equal(52, graph.GetCommitTextStart());
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

    [Fact]
    public void CollapsedGraphRendersEveryCommitOnOnePrimaryLane()
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
                    IsGraphCollapsed = true
                };

                Assert.All(
                    graph.Items,
                    commit => Assert.Equal(
                        0,
                        graph.GetRenderedLaneForCommit(commit.Id)));
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
    public void CommitTextDistanceIsOneHalfOfPreviousSpacingFromFarthestLane()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var baseCommit = CreateCommit("aaaaaaaa", []);
                var mainCommit = CreateCommit("bbbbbbbb", [baseCommit.Id]);
                var firstBranch = CreateCommit("cccccccc", [baseCommit.Id]);
                var secondBranch = CreateCommit("dddddddd", [baseCommit.Id]);
                var thirdBranch = CreateCommit("eeeeeeee", [baseCommit.Id]);
                var mergeCommit = CreateCommit(
                    "ffffffff",
                    [
                        mainCommit.Id,
                        firstBranch.Id,
                        secondBranch.Id,
                        thirdBranch.Id
                    ]);
                var graph = new CommitGraphControl
                {
                    Items = new ObservableCollection<CommitNode>(
                        [
                            mergeCommit,
                            mainCommit,
                            firstBranch,
                            secondBranch,
                            thirdBranch,
                            baseCommit
                        ]),
                    Head = new HeadInfo(mergeCommit.Id, "main", false)
                };

                Assert.Equal(3, graph.GetLaneForCommit(thirdBranch.Id));

                const double farthestLaneX = 22 + 3 * 28;
                const double previousTextStart = 22 + 4 * 28 + 24;
                var expectedTextStart = farthestLaneX +
                    (previousTextStart - farthestLaneX) / 2;

                Assert.Equal(expectedTextStart, graph.GetCommitTextStart(), 10);
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
