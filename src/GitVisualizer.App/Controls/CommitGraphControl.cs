using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public sealed class CommitSelectedEventArgs : EventArgs
{
    public CommitSelectedEventArgs(CommitNode commit) => Commit = commit;
    public CommitNode Commit { get; }
}

public sealed class CommitGraphControl : FrameworkElement
{
    private const double RowHeight = 46;
    private const double LaneWidth = 22;
    private const double TextStart = 150;
    private static readonly Brush[] LaneBrushes =
    [
        Brushes.DodgerBlue, Brushes.MediumSeaGreen, Brushes.MediumPurple,
        Brushes.DarkOrange, Brushes.DeepPink, Brushes.Teal
    ];
    private IReadOnlyList<LayoutNode> layout = [];

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IEnumerable<CommitNode>),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure |
                                            FrameworkPropertyMetadataOptions.AffectsRender,
            (element, _) => ((CommitGraphControl)element).RebuildLayout()));

    public IEnumerable<CommitNode>? Items
    {
        get => (IEnumerable<CommitNode>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public event EventHandler<CommitSelectedEventArgs>? CommitSelected;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : Math.Max(availableSize.Width, 600);
        return new Size(width, Math.Max(layout.Count * RowHeight, 80));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var foreground = TryFindResource(SystemColors.ControlTextBrushKey) as Brush ?? Brushes.White;
        var secondary = foreground.Clone();
        secondary.Opacity = 0.62;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var nodeById = layout.ToDictionary(item => item.Commit.Id, StringComparer.Ordinal);

        foreach (var item in layout)
        {
            var x = 20 + item.Lane * LaneWidth;
            var y = item.Index * RowHeight + RowHeight / 2;
            foreach (var parentId in item.Commit.ParentIds)
            {
                if (!nodeById.TryGetValue(parentId, out var parent))
                {
                    drawingContext.DrawLine(
                        new Pen(LaneBrushes[item.Lane % LaneBrushes.Length], 2),
                        new Point(x, y),
                        new Point(x, y + RowHeight / 2));
                    continue;
                }
                var parentX = 20 + parent.Lane * LaneWidth;
                var parentY = parent.Index * RowHeight + RowHeight / 2;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    var midY = y + Math.Min(RowHeight * 0.7, (parentY - y) / 2);
                    context.BezierTo(
                        new Point(x, midY),
                        new Point(parentX, midY),
                        new Point(parentX, parentY),
                        true, false);
                }
                drawingContext.DrawGeometry(
                    null,
                    new Pen(LaneBrushes[item.Lane % LaneBrushes.Length], 2),
                    geometry);
            }
        }

        foreach (var item in layout)
        {
            var x = 20 + item.Lane * LaneWidth;
            var y = item.Index * RowHeight + RowHeight / 2;
            var brush = LaneBrushes[item.Lane % LaneBrushes.Length];
            drawingContext.DrawEllipse(brush, new Pen(Brushes.White, 1.5), new Point(x, y), 6, 6);

            var message = new FormattedText(
                item.Commit.Message,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                foreground,
                dpi)
            {
                MaxTextWidth = Math.Max(100, ActualWidth - TextStart - 160),
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawingContext.DrawText(message, new Point(TextStart, y - 16));

            var meta = $"{item.Commit.ShortId}  {item.Commit.AuthorName}  {item.Commit.AuthoredAt.LocalDateTime:g}";
            if (item.Commit.Decorations.Count > 0)
            {
                meta += "  ·  " + string.Join("  ", item.Commit.Decorations);
            }
            var metadata = new FormattedText(
                meta,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10.5,
                secondary,
                dpi)
            {
                MaxTextWidth = Math.Max(100, ActualWidth - TextStart - 20),
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawingContext.DrawText(metadata, new Point(TextStart, y + 3));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var index = (int)(e.GetPosition(this).Y / RowHeight);
        if (index >= 0 && index < layout.Count)
        {
            CommitSelected?.Invoke(this, new CommitSelectedEventArgs(layout[index].Commit));
            Focus();
        }
    }

    private void RebuildLayout()
    {
        var commits = Items?.ToArray() ?? [];
        var activeLanes = new List<string?>();
        var result = new List<LayoutNode>(commits.Length);
        for (var index = 0; index < commits.Length; index++)
        {
            var commit = commits[index];
            var lane = activeLanes.FindIndex(id => id == commit.Id);
            if (lane < 0)
            {
                lane = activeLanes.FindIndex(id => id is null);
                if (lane < 0)
                {
                    lane = activeLanes.Count;
                    activeLanes.Add(null);
                }
            }

            activeLanes[lane] = commit.ParentIds.FirstOrDefault();
            foreach (var parent in commit.ParentIds.Skip(1))
            {
                var free = activeLanes.FindIndex(id => id is null);
                if (free < 0)
                {
                    activeLanes.Add(parent);
                }
                else
                {
                    activeLanes[free] = parent;
                }
            }
            result.Add(new LayoutNode(commit, lane, index));
        }
        layout = result;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private sealed record LayoutNode(CommitNode Commit, int Lane, int Index);
}
