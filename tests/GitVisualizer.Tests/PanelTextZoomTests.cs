using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitVisualizer.App;
using GitVisualizer.App.Controls;

namespace GitVisualizer.Tests;

public sealed class PanelTextZoomTests
{
    [Theory]
    [InlineData(1.0, 120, 1.1)]
    [InlineData(1.9, 120, 2.0)]
    [InlineData(2.0, 120, 2.0)]
    [InlineData(1.1, -120, 1.0)]
    [InlineData(1.0, -120, 1.0)]
    [InlineData(double.NaN, 0, 1.0)]
    public void WheelStepIsTenPercentAndClamped(
        double current,
        int delta,
        double expected)
    {
        Assert.Equal(expected, PanelTextZoom.CalculateNextScale(current, delta));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    [InlineData(4, true, true)]
    public void ZoomPermissionMatchesDockedAndDetachedRules(
        int tabIndex,
        bool detached,
        bool expected)
    {
        Assert.Equal(expected, PanelTextZoom.IsZoomAllowed(tabIndex, detached));
    }

    [Fact]
    public void RestrictedTabsRenderAtBaselineWhenDockedButKeepRememberedScale()
    {
        Assert.Equal(1.0, PanelTextZoom.GetEffectiveScale(0, false, 1.8));
        Assert.Equal(1.8, PanelTextZoom.GetEffectiveScale(0, true, 1.8));
        Assert.Equal(1.8, PanelTextZoom.GetEffectiveScale(1, false, 1.8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorInitialStateDoesNotAcceptZoomInput(bool detached)
    {
        Assert.False(PanelTextZoom.IsZoomInputAllowed(1, detached, false));
        Assert.True(PanelTextZoom.IsZoomInputAllowed(1, detached, true));
        Assert.True(PanelTextZoom.IsZoomInputAllowed(2, detached, false));
    }

    [Fact]
    public void ElementSpecificMaximumScaleCapsOnlyThatText()
    {
        RunOnSta(() =>
        {
            var capped = new TextBlock { FontSize = 12, Text = "摘要" };
            var regular = new TextBlock { FontSize = 12, Text = "正文" };
            var scope = new StackPanel();
            scope.Children.Add(capped);
            scope.Children.Add(regular);
            PanelTextZoom.SetMaximumElementScale(capped, 1.7);

            PanelTextZoom.Attach(scope);
            PanelTextZoom.SetScale(scope, 2.0);

            Assert.Equal(20.4, capped.FontSize);
            Assert.Equal(24, regular.FontSize);
        });
    }

    [Fact]
    public void NestedBoundaryKeepsIndependentScale()
    {
        RunOnSta(() =>
        {
            var outerText = new TextBlock { FontSize = 10, Text = "详情" };
            var fixedText = new TextBlock { FontSize = 10, Text = "固定操作" };
            var boundary = new StackPanel();
            boundary.Children.Add(fixedText);
            PanelTextZoom.SetIsScaleBoundary(boundary, true);
            var scope = new StackPanel();
            scope.Children.Add(outerText);
            scope.Children.Add(boundary);

            PanelTextZoom.Attach(scope);
            PanelTextZoom.Attach(boundary);
            PanelTextZoom.SetScale(scope, 2.0);
            PanelTextZoom.SetScale(boundary, 1.0);

            Assert.Equal(20, outerText.FontSize);
            Assert.Equal(10, fixedText.FontSize);
        });
    }

    [Fact]
    public void SelectedCommitActionsAreaKeepsScreenshotTextSizes()
    {
        RunOnSta(() =>
        {
            var detailsText = new TextBlock { FontSize = 10, Text = "详情" };
            var title = new TextBlock { FontSize = 20.4, Text = "所选提交操作" };
            var hint = new TextBlock { FontSize = 10, Text = "先选择提交，再选择操作" };
            var buttonText = new TextBlock { FontSize = 10, Text = "比较提交" };
            var actionPanel = new StackPanel();
            actionPanel.Children.Add(title);
            actionPanel.Children.Add(hint);
            actionPanel.Children.Add(new Button { Content = buttonText });
            PanelTextZoom.SetIsScaleBoundary(actionPanel, true);
            PanelTextZoom.SetIsExcluded(actionPanel, true);
            var detailsScope = new StackPanel();
            detailsScope.Children.Add(detailsText);
            detailsScope.Children.Add(actionPanel);

            PanelTextZoom.Attach(detailsScope);
            PanelTextZoom.SetScale(detailsScope, 2.0);

            Assert.Equal(20, detailsText.FontSize);
            Assert.Equal(20.4, title.FontSize);
            Assert.Equal(10, hint.FontSize);
            Assert.Equal(10, buttonText.FontSize);
        });
    }

    [Fact]
    public void ExcludedEmptyStateSubtreeStaysAtBaseline()
    {
        RunOnSta(() =>
        {
            var prompt = new TextBlock { FontSize = 16, Text = "请选择内容" };
            var emptyState = new Border { Child = prompt };
            PanelTextZoom.SetIsExcluded(emptyState, true);
            var scope = new StackPanel();
            scope.Children.Add(emptyState);

            PanelTextZoom.Attach(scope);
            PanelTextZoom.SetScale(scope, 2.0);

            Assert.Equal(16, prompt.FontSize);
        });
    }

    [Fact]
    public void FontAppliedAfterInitialZoomBecomesTheSynchronizedBaseline()
    {
        RunOnSta(() =>
        {
            var body = new TextBlock { FontSize = 10, Text = "动态模板" };
            var scope = new StackPanel();
            scope.Children.Add(body);
            PanelTextZoom.Attach(scope);
            PanelTextZoom.SetScale(scope, 1.5);
            Assert.Equal(15, body.FontSize);

            body.FontSize = 12;
            PanelTextZoom.SetScale(scope, 1.5);

            Assert.Equal(18, body.FontSize);
        });
    }

    [Fact]
    public void ScopeScalesTextAndLeavesButtonsAndDecorativeGlyphsFixed()
    {
        RunOnSta(() =>
        {
            var body = new TextBlock { FontSize = 12, Text = "正文" };
            var textBox = new TextBox { FontSize = 13, Text = "代码" };
            var buttonText = new TextBlock { FontSize = 11, Text = "按钮" };
            var button = new Button { Content = buttonText };
            var glyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Text = "\uE8A7"
            };
            var scope = new StackPanel();
            scope.Children.Add(body);
            scope.Children.Add(textBox);
            scope.Children.Add(button);
            scope.Children.Add(glyph);

            PanelTextZoom.Attach(scope);
            PanelTextZoom.SetScale(scope, 1.5);

            Assert.Equal(18, body.FontSize);
            Assert.Equal(19.5, textBox.FontSize);
            Assert.Equal(11, buttonText.FontSize);
            Assert.Equal(18, glyph.FontSize);

            PanelTextZoom.SetScale(scope, 1.0);
            Assert.Equal(12, body.FontSize);
            Assert.Equal(13, textBox.FontSize);
        });
    }

    [Fact]
    public void NewlyLoadedTextInheritsCurrentScopeScale()
    {
        RunOnSta(() =>
        {
            var scope = new StackPanel();
            var window = new Window
            {
                Width = 220,
                Height = 140,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                Content = scope
            };
            try
            {
                PanelTextZoom.Attach(scope);
                PanelTextZoom.SetScale(scope, 1.6);
                window.Show();

                var dynamicText = new TextBlock { FontSize = 10, Text = "动态日志" };
                scope.Children.Add(dynamicText);
                FlushDispatcher(window.Dispatcher);

                Assert.Equal(16, dynamicText.FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FeaturePanelHostCanMoveBetweenDockAndDetachedWindow()
    {
        RunOnSta(() =>
        {
            var dock = new Grid();
            var state = new object();
            var host = new Grid { DataContext = state };
            dock.Children.Add(host);
            var detached = new FeaturePanelWindow();
            try
            {
                dock.Children.Remove(host);
                detached.PanelContent = host;
                Assert.Same(host, detached.PanelContent);
                Assert.Same(state, host.DataContext);

                detached.PanelContent = null;
                dock.Children.Add(host);
                Assert.Same(dock, host.Parent);
                Assert.Same(state, host.DataContext);
            }
            finally
            {
                detached.PanelContent = null;
                detached.Close();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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

    private static void FlushDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
