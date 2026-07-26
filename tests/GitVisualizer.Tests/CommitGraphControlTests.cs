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

                Assert.Equal(92, graph.DesiredSize.Height);
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
            parents,
            []);
}
