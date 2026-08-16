using System.Collections.Specialized;
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

public sealed class BranchSelectedEventArgs : EventArgs
{
    public BranchSelectedEventArgs(BranchInfo branch) => Branch = branch;
    public BranchInfo Branch { get; }
}

public sealed class CommitGraphControl : FrameworkElement
{
    private const double RowHeight = 50;
    private const double LaneWidth = 28;
    private const double GraphLeft = 22;
    private const double MinimumTextStart = 132;
    private const double BadgeHeight = 18;

    private static readonly Brush[] LaneBrushes =
    [
        Brushes.DodgerBlue,
        Brushes.MediumSeaGreen,
        Brushes.MediumPurple,
        Brushes.DarkOrange,
        Brushes.DeepPink,
        Brushes.Teal
    ];

    private IReadOnlyList<LayoutNode> layout = [];
    private readonly List<BranchBadgeHit> branchBadgeHits = [];
    private Rect? headBadgeHit;
    private int? hoveredLane;
    private string? hoveredBranchName;
    private bool isHeadHovered;
    private double graphTextStart = MinimumTextStart;
    private int renderedHighestLane;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IEnumerable<CommitNode>),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnItemsChanged));

    public IEnumerable<CommitNode>? Items
    {
        get => (IEnumerable<CommitNode>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty SelectedCommitProperty = DependencyProperty.Register(
        nameof(SelectedCommit),
        typeof(CommitNode),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public CommitNode? SelectedCommit
    {
        get => (CommitNode?)GetValue(SelectedCommitProperty);
        set => SetValue(SelectedCommitProperty, value);
    }

    public static readonly DependencyProperty BranchesProperty = DependencyProperty.Register(
        nameof(Branches),
        typeof(IEnumerable<BranchInfo>),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnReferencesChanged));

    public IEnumerable<BranchInfo>? Branches
    {
        get => (IEnumerable<BranchInfo>?)GetValue(BranchesProperty);
        set => SetValue(BranchesProperty, value);
    }

    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags),
        typeof(IEnumerable<TagInfo>),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnReferencesChanged));

    public IEnumerable<TagInfo>? Tags
    {
        get => (IEnumerable<TagInfo>?)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public static readonly DependencyProperty EventsProperty = DependencyProperty.Register(
        nameof(Events),
        typeof(IEnumerable<GitHistoryEvent>),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnEventsChanged));

    public IEnumerable<GitHistoryEvent>? Events
    {
        get => (IEnumerable<GitHistoryEvent>?)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public static readonly DependencyProperty HeadProperty = DependencyProperty.Register(
        nameof(Head),
        typeof(HeadInfo),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLayoutContextChanged));

    public HeadInfo? Head
    {
        get => (HeadInfo?)GetValue(HeadProperty);
        set => SetValue(HeadProperty, value);
    }

    public static readonly DependencyProperty SelectedBranchNameProperty = DependencyProperty.Register(
        nameof(SelectedBranchName),
        typeof(string),
        typeof(CommitGraphControl),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLayoutContextChanged));

    public string SelectedBranchName
    {
        get => (string)GetValue(SelectedBranchNameProperty);
        set => SetValue(SelectedBranchNameProperty, value);
    }

    public event EventHandler<CommitSelectedEventArgs>? CommitSelected;
    public event EventHandler<BranchSelectedEventArgs>? BranchSelected;

    private static void OnItemsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CommitGraphControl)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= control.OnItemsCollectionChanged;
        }
        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += control.OnItemsCollectionChanged;
        }
        control.RebuildLayout();
    }

    private static void OnReferencesChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CommitGraphControl)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= control.OnReferencesCollectionChanged;
        }
        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += control.OnReferencesCollectionChanged;
        }
        control.RebuildLayout();
    }

    private static void OnEventsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CommitGraphControl)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= control.OnEventsCollectionChanged;
        }
        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += control.OnEventsCollectionChanged;
        }
        control.InvalidateVisual();
    }

    private static void OnLayoutContextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((CommitGraphControl)dependencyObject).RebuildLayout();
    }

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args) =>
        RebuildLayout();

    private void OnReferencesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args) =>
        RebuildLayout();

    private void OnEventsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args) =>
        InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? 900
            : Math.Max(availableSize.Width, 650);
        return new Size(width, Math.Max(layout.Count * RowHeight, 80));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var foreground =
            TryFindResource(SystemColors.ControlTextBrushKey) as Brush ??
            Brushes.Black;
        var secondary = foreground.Clone();
        secondary.Opacity = 0.62;
        var selectionFill =
            (TryFindResource(SystemColors.HighlightBrushKey) as Brush ??
             Brushes.DodgerBlue).Clone();
        selectionFill.Opacity = 0.14;
        var selectionBorder =
            (TryFindResource(SystemColors.HighlightBrushKey) as Brush ??
             Brushes.DodgerBlue).Clone();
        selectionBorder.Opacity = 0.72;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var nodeById = layout.ToDictionary(
            item => item.Commit.Id,
            StringComparer.Ordinal);
        var branches = Branches?.ToArray() ?? [];
        var tags = Tags?.ToArray() ?? [];
        var events = Events?.ToArray() ?? [];
        var branchesByTip = branches
            .Where(branch => !string.IsNullOrEmpty(branch.TipId))
            .GroupBy(branch => branch.TipId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var tagsByTarget = tags
            .Where(tag => !string.IsNullOrEmpty(tag.TargetId))
            .GroupBy(tag => tag.TargetId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var eventsByCommit = events
            .Where(historyEvent => !string.IsNullOrEmpty(historyEvent.CommitId))
            .GroupBy(historyEvent => historyEvent.CommitId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(historyEvent => historyEvent.OccurredAt)
                    .ToArray(),
                StringComparer.Ordinal);

        var highestLane = layout.Count == 0
            ? 0
            : layout.Max(item => item.Lane);
        graphTextStart = Math.Max(
            MinimumTextStart,
            GraphLeft + (highestLane + 1) * LaneWidth + 24);
        renderedHighestLane = highestLane;
        branchBadgeHits.Clear();
        headBadgeHit = null;

        drawingContext.DrawRectangle(
            Brushes.Transparent,
            null,
            new Rect(
                0,
                0,
                Math.Max(ActualWidth, 1),
                Math.Max(layout.Count * RowHeight, 1)));

        DrawSelection(drawingContext, selectionFill, selectionBorder);
        DrawParentConnections(drawingContext, nodeById);

        foreach (var item in layout)
        {
            DrawCommit(
                drawingContext,
                item,
                foreground,
                secondary,
                branchesByTip,
                tagsByTarget,
                eventsByCommit,
                nodeById,
                dpi);
        }
    }

    private void DrawSelection(
        DrawingContext drawingContext,
        Brush selectionFill,
        Brush selectionBorder)
    {
        var selected = layout.FirstOrDefault(item =>
            string.Equals(
                item.Commit.Id,
                SelectedCommit?.Id,
                StringComparison.Ordinal));
        if (selected is null)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            selectionFill,
            new Pen(selectionBorder, 1),
            new Rect(
                1,
                selected.Index * RowHeight + 1,
                Math.Max(0, ActualWidth - 2),
                RowHeight - 2),
            4,
            4);
    }

    private void DrawParentConnections(
        DrawingContext drawingContext,
        IReadOnlyDictionary<string, LayoutNode> nodeById)
    {
        foreach (var item in layout)
        {
            var x = LaneX(item.Lane);
            var y = RowY(item.Index);
            foreach (var parentId in item.Commit.ParentIds)
            {
                var isHovered = hoveredLane == item.Lane;
                var pen = CreateLanePen(item.Lane, isHovered);
                if (!nodeById.TryGetValue(parentId, out var parent))
                {
                    drawingContext.DrawLine(
                        pen,
                        new Point(x, y),
                        new Point(x, y + RowHeight / 2));
                    continue;
                }

                var parentX = LaneX(parent.Lane);
                var parentY = RowY(parent.Index);
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    var verticalDistance = Math.Max(RowHeight, parentY - y);
                    var bendY = y + Math.Min(
                        RowHeight * 0.72,
                        verticalDistance / 2);
                    context.BezierTo(
                        new Point(x, bendY),
                        new Point(parentX, bendY),
                        new Point(parentX, parentY),
                        true,
                        false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private void DrawCommit(
        DrawingContext drawingContext,
        LayoutNode item,
        Brush foreground,
        Brush secondary,
        IReadOnlyDictionary<string, BranchInfo[]> branchesByTip,
        IReadOnlyDictionary<string, TagInfo[]> tagsByTarget,
        IReadOnlyDictionary<string, GitHistoryEvent[]> eventsByCommit,
        IReadOnlyDictionary<string, LayoutNode> nodeById,
        double dpi)
    {
        var x = LaneX(item.Lane);
        var y = RowY(item.Index);
        var isLaneHovered = hoveredLane == item.Lane;
        var commitEvents = eventsByCommit.GetValueOrDefault(item.Commit.Id) ?? [];
        var isMerge = item.Commit.ParentIds.Count > 1 ||
                      commitEvents.Any(historyEvent =>
                          historyEvent.Kind == GitHistoryEventKind.Merge);
        var isRevert = commitEvents.Any(historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.Revert);
        drawingContext.DrawEllipse(
            isRevert ? Brushes.White : CreateLaneBrush(item.Lane, isLaneHovered ? 1 : 0.92),
            new Pen(
                isRevert
                    ? CreateLaneBrush(item.Lane, 1)
                    : Brushes.White,
                isLaneHovered ? 2 : 1.5),
            new Point(x, y),
            isLaneHovered ? 7 : 6,
            isLaneHovered ? 7 : 6);
        if (isMerge)
        {
            drawingContext.DrawEllipse(
                null,
                new Pen(CreateLaneBrush(item.Lane, 0.92), 2),
                new Point(x, y),
                10,
                10);
        }

        var message = CreateText(
            item.Commit.Message,
            13,
            foreground,
            dpi,
            FontWeights.Normal);
        message.MaxTextWidth = Math.Max(
            100,
            ActualWidth - graphTextStart - 24);
        message.Trimming = TextTrimming.CharacterEllipsis;
        drawingContext.DrawText(
            message,
            new Point(graphTextStart, y - 18));

        var metadataX = graphTextStart;
        if (Head is not null &&
            string.Equals(Head.CommitId, item.Commit.Id, StringComparison.Ordinal))
        {
            metadataX = DrawHeadBadge(
                drawingContext,
                metadataX,
                y + 2,
                dpi);
        }

        foreach (var historyEvent in commitEvents
                     .Where(historyEvent => historyEvent.Kind !=
                                            GitHistoryEventKind.CommitCreated)
                     .GroupBy(historyEvent =>
                         (historyEvent.Kind, historyEvent.BranchName))
                     .Select(group => group.Last())
                     .Take(3))
        {
            metadataX = DrawEventBadge(
                drawingContext,
                historyEvent,
                metadataX,
                y + 2,
                dpi);
        }

        if (branchesByTip.TryGetValue(item.Commit.Id, out var commitBranches))
        {
            foreach (var branch in commitBranches
                         .OrderByDescending(branch => branch.IsCurrent)
                         .ThenBy(branch => branch.IsRemote)
                         .ThenBy(
                             branch => branch.FriendlyName,
                             StringComparer.CurrentCulture))
            {
                metadataX = DrawBranchBadge(
                    drawingContext,
                    branch,
                    ResolveBranchDisplayState(branch, nodeById),
                    item.Lane,
                    metadataX,
                    y + 2,
                    dpi);
            }
        }

        if (tagsByTarget.TryGetValue(item.Commit.Id, out var commitTags))
        {
            foreach (var tag in commitTags.OrderBy(
                         tag => tag.Name,
                         StringComparer.CurrentCulture))
            {
                metadataX = DrawTagBadge(
                    drawingContext,
                    tag,
                    metadataX,
                    y + 2,
                    dpi);
            }
        }

        var parentSummary = item.Commit.ParentIds.Count > 1
            ? $"  合并提交 · {item.Commit.ParentIds.Count} 个父提交"
            : string.Empty;
        var metadata = CreateText(
            $"{item.Commit.ShortId}  {item.Commit.AuthorName}  " +
            $"{item.Commit.AuthoredAt.LocalDateTime:g}{parentSummary}",
            10.5,
            secondary,
            dpi,
            FontWeights.Normal);
        metadata.MaxTextWidth = Math.Max(
            100,
            ActualWidth - metadataX - 20);
        metadata.Trimming = TextTrimming.CharacterEllipsis;
        drawingContext.DrawText(
            metadata,
            new Point(metadataX, y + 4));
    }

    private double DrawHeadBadge(
        DrawingContext drawingContext,
        double startX,
        double top,
        double dpi)
    {
        var labelText = Head?.IsDetached == true ? "HEAD（游离）" : "HEAD";
        var label = CreateText(
            labelText,
            10,
            Brushes.White,
            dpi,
            FontWeights.SemiBold);
        var width = Math.Max(44, label.Width + 14);
        var bounds = new Rect(startX, top, width, BadgeHeight);
        var fill = new SolidColorBrush(Color.FromRgb(230, 126, 34));
        fill.Freeze();
        drawingContext.DrawRoundedRectangle(
            fill,
            new Pen(fill, 1),
            bounds,
            8,
            8);
        drawingContext.DrawText(label, new Point(startX + 7, top + 1.5));
        headBadgeHit = bounds;
        return startX + width + 6;
    }

    private double DrawBranchBadge(
        DrawingContext drawingContext,
        BranchInfo branch,
        BranchDisplayState state,
        int lane,
        double startX,
        double top,
        double dpi)
    {
        var isSelected = string.Equals(
            branch.FriendlyName,
            SelectedBranchName,
            StringComparison.Ordinal);
        var isHovered = string.Equals(
            branch.FriendlyName,
            hoveredBranchName,
            StringComparison.Ordinal);
        var isEmphasized = branch.IsCurrent || isSelected || isHovered;
        var stateText = state switch
        {
            BranchDisplayState.Current => "当前",
            BranchDisplayState.Main => "主分支",
            BranchDisplayState.Merged => "已合并",
            BranchDisplayState.Active => "活跃",
            BranchDisplayState.Remote => "远程",
            _ => string.Empty
        };
        var labelText = string.IsNullOrEmpty(stateText)
            ? branch.FriendlyName
            : $"{branch.FriendlyName} · {stateText}";
        var foreground = isEmphasized
            ? Brushes.White
            : state is BranchDisplayState.Remote or BranchDisplayState.Merged
                ? Brushes.DimGray
                : CreateLaneBrush(lane, 1);
        var label = CreateText(
            labelText,
            10,
            foreground,
            dpi,
            isEmphasized ? FontWeights.SemiBold : FontWeights.Normal);
        label.MaxTextWidth = 150;
        label.Trimming = TextTrimming.CharacterEllipsis;
        var width = Math.Min(164, Math.Max(38, label.Width + 14));
        var bounds = new Rect(startX, top, width, BadgeHeight);

        var accent = state switch
        {
            BranchDisplayState.Remote => Color.FromRgb(108, 117, 125),
            BranchDisplayState.Merged => Color.FromRgb(125, 133, 140),
            _ => LaneColor(lane)
        };
        var fill = new SolidColorBrush(accent)
        {
            Opacity = isEmphasized ? 0.92 : 0.12
        };
        var borderBrush = new SolidColorBrush(accent)
        {
            Opacity = isEmphasized ? 1 : 0.72
        };
        fill.Freeze();
        borderBrush.Freeze();
        drawingContext.DrawRoundedRectangle(
            fill,
            new Pen(borderBrush, isEmphasized ? 1.4 : 1),
            bounds,
            8,
            8);
        drawingContext.DrawText(label, new Point(startX + 7, top + 1.5));
        branchBadgeHits.Add(new BranchBadgeHit(bounds, branch));
        return startX + width + 6;
    }

    private static double DrawEventBadge(
        DrawingContext drawingContext,
        GitHistoryEvent historyEvent,
        double startX,
        double top,
        double dpi)
    {
        var (text, accent) = historyEvent.Kind switch
        {
            GitHistoryEventKind.BranchCreated =>
                ($"分叉 · {historyEvent.BranchName}", Color.FromRgb(126, 87, 194)),
            GitHistoryEventKind.BranchDeleted =>
                ($"已删除 · {historyEvent.BranchName}", Color.FromRgb(176, 75, 75)),
            GitHistoryEventKind.Checkout =>
                ("checkout", Color.FromRgb(44, 123, 229)),
            GitHistoryEventKind.Reset =>
                ("reset", Color.FromRgb(219, 126, 36)),
            GitHistoryEventKind.Merge =>
                ("merge", Color.FromRgb(38, 145, 96)),
            GitHistoryEventKind.Revert =>
                ("revert", Color.FromRgb(204, 82, 82)),
            _ =>
                (historyEvent.Kind.ToString(), Color.FromRgb(108, 117, 125))
        };
        var foreground = new SolidColorBrush(accent);
        foreground.Freeze();
        var label = CreateText(
            text,
            10,
            foreground,
            dpi,
            FontWeights.SemiBold);
        label.MaxTextWidth = 140;
        label.Trimming = TextTrimming.CharacterEllipsis;
        var width = Math.Min(154, Math.Max(44, label.Width + 14));
        var bounds = new Rect(startX, top, width, BadgeHeight);
        var fill = new SolidColorBrush(accent) { Opacity = 0.12 };
        var border = new SolidColorBrush(accent) { Opacity = 0.82 };
        fill.Freeze();
        border.Freeze();
        drawingContext.DrawRoundedRectangle(
            fill,
            new Pen(border, 1),
            bounds,
            8,
            8);
        drawingContext.DrawText(label, new Point(startX + 7, top + 1.5));
        return startX + width + 6;
    }

    private static double DrawTagBadge(
        DrawingContext drawingContext,
        TagInfo tag,
        double startX,
        double top,
        double dpi)
    {
        var label = CreateText(
            $"tag: {tag.Name}",
            10,
            Brushes.DimGray,
            dpi,
            FontWeights.Normal);
        label.MaxTextWidth = 140;
        label.Trimming = TextTrimming.CharacterEllipsis;
        var width = Math.Min(154, Math.Max(44, label.Width + 14));
        var bounds = new Rect(startX, top, width, BadgeHeight);
        var fill = new SolidColorBrush(Color.FromArgb(22, 96, 103, 112));
        var border = new SolidColorBrush(Color.FromArgb(150, 108, 117, 125));
        fill.Freeze();
        border.Freeze();
        drawingContext.DrawRoundedRectangle(
            fill,
            new Pen(border, 1),
            bounds,
            8,
            8);
        drawingContext.DrawText(label, new Point(startX + 7, top + 1.5));
        return startX + width + 6;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        var badgeHit = branchBadgeHits.FirstOrDefault(item =>
            item.Bounds.Contains(position));
        var headHovered = headBadgeHit?.Contains(position) == true;
        int? lane = null;
        if (badgeHit is null &&
            !headHovered &&
            position.X >= GraphLeft - 10 &&
            position.X < graphTextStart)
        {
            var nearestLane = (int)Math.Round(
                (position.X - GraphLeft) / LaneWidth);
            if (nearestLane >= 0 &&
                nearestLane <= renderedHighestLane &&
                Math.Abs(position.X - LaneX(nearestLane)) <= 9)
            {
                lane = nearestLane;
            }
        }

        var branchName = badgeHit?.Branch.FriendlyName;
        if (string.Equals(
                branchName,
                hoveredBranchName,
                StringComparison.Ordinal) &&
            lane == hoveredLane &&
            headHovered == isHeadHovered)
        {
            return;
        }

        hoveredBranchName = branchName;
        hoveredLane = lane;
        isHeadHovered = headHovered;
        Cursor = badgeHit is null ? Cursors.Arrow : Cursors.Hand;
        ToolTip = badgeHit is not null
            ? $"{badgeHit.Branch.FriendlyName}\n分支指针指向此提交；单击查看该分支"
            : headHovered
                ? Head?.IsDetached == true
                    ? "HEAD 当前直接指向此提交（游离状态）"
                    : $"HEAD 当前附着于分支 {Head?.BranchName}"
                : lane is not null
                    ? $"提交线路 {lane + 1}\n颜色仅用于区分提交关系线路"
                    : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoveredBranchName is null &&
            hoveredLane is null &&
            !isHeadHovered)
        {
            return;
        }

        hoveredBranchName = null;
        hoveredLane = null;
        isHeadHovered = false;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var position = e.GetPosition(this);
        var badgeHit = branchBadgeHits.FirstOrDefault(item =>
            item.Bounds.Contains(position));
        if (badgeHit is not null)
        {
            BranchSelected?.Invoke(
                this,
                new BranchSelectedEventArgs(badgeHit.Branch));
            Focus();
            e.Handled = true;
            return;
        }

        var index = (int)(position.Y / RowHeight);
        if (index < 0 || index >= layout.Count)
        {
            return;
        }

        var commit = layout[index].Commit;
        SetCurrentValue(SelectedCommitProperty, commit);
        CommitSelected?.Invoke(this, new CommitSelectedEventArgs(commit));
        Focus();
    }

    private BranchDisplayState ResolveBranchDisplayState(
        BranchInfo branch,
        IReadOnlyDictionary<string, LayoutNode> nodeById)
    {
        if (branch.IsRemote)
        {
            return BranchDisplayState.Remote;
        }
        if (branch.IsCurrent ||
            string.Equals(
                branch.FriendlyName,
                Head?.BranchName,
                StringComparison.Ordinal))
        {
            return BranchDisplayState.Current;
        }
        if (string.Equals(
                branch.FriendlyName,
                "main",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                branch.FriendlyName,
                "master",
                StringComparison.OrdinalIgnoreCase))
        {
            return BranchDisplayState.Main;
        }
        if (Head is not null &&
            IsAncestor(branch.TipId, Head.CommitId, nodeById))
        {
            return BranchDisplayState.Merged;
        }
        return BranchDisplayState.Active;
    }

    private static bool IsAncestor(
        string ancestorId,
        string descendantId,
        IReadOnlyDictionary<string, LayoutNode> nodeById)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(descendantId);
        while (pending.Count > 0)
        {
            var commitId = pending.Pop();
            if (!visited.Add(commitId))
            {
                continue;
            }
            if (string.Equals(commitId, ancestorId, StringComparison.Ordinal))
            {
                return true;
            }
            if (!nodeById.TryGetValue(commitId, out var node))
            {
                continue;
            }
            foreach (var parentId in node.Commit.ParentIds)
            {
                pending.Push(parentId);
            }
        }
        return false;
    }

    private void RebuildLayout()
    {
        var commits = Items?.ToArray() ?? [];
        var commitById = commits.ToDictionary(
            commit => commit.Id,
            StringComparer.Ordinal);
        var selectedTipId = Branches?
            .FirstOrDefault(branch =>
                string.Equals(
                    branch.FriendlyName,
                    SelectedBranchName,
                    StringComparison.Ordinal))
            ?.TipId;
        var primaryTipId = !string.IsNullOrEmpty(selectedTipId)
            ? selectedTipId
            : Head?.CommitId;
        if (string.IsNullOrEmpty(primaryTipId) ||
            !commitById.ContainsKey(primaryTipId))
        {
            primaryTipId = commits.FirstOrDefault()?.Id;
        }
        var primaryPath = BuildFirstParentPath(primaryTipId, commitById);
        var activeLanes = new List<string?> { primaryTipId };
        var result = new List<LayoutNode>(commits.Length);

        for (var index = 0; index < commits.Length; index++)
        {
            var commit = commits[index];
            var isPrimaryCommit = primaryPath.Contains(commit.Id);
            var lane = isPrimaryCommit
                ? 0
                : FindLane(activeLanes, commit.Id, startIndex: 1);
            if (lane < 0)
            {
                lane = FindFreeLane(activeLanes, startIndex: 1);
                if (lane < 0)
                {
                    lane = activeLanes.Count;
                    activeLanes.Add(null);
                }
            }
            while (activeLanes.Count <= lane)
            {
                activeLanes.Add(null);
            }

            for (var duplicate = 0; duplicate < activeLanes.Count; duplicate++)
            {
                if (duplicate != lane &&
                    string.Equals(
                        activeLanes[duplicate],
                        commit.Id,
                        StringComparison.Ordinal))
                {
                    activeLanes[duplicate] = null;
                }
            }

            var parents = commit.ParentIds
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var firstParent = parents.FirstOrDefault();
            if (firstParent is null ||
                (!isPrimaryCommit && primaryPath.Contains(firstParent)))
            {
                activeLanes[lane] = null;
            }
            else
            {
                for (var duplicate = 0; duplicate < activeLanes.Count; duplicate++)
                {
                    if (duplicate != lane &&
                        string.Equals(
                            activeLanes[duplicate],
                            firstParent,
                            StringComparison.Ordinal))
                    {
                        activeLanes[duplicate] = null;
                    }
                }
                activeLanes[lane] = firstParent;
            }

            foreach (var parent in parents.Skip(1))
            {
                if (primaryPath.Contains(parent) ||
                    activeLanes.Any(id =>
                        string.Equals(id, parent, StringComparison.Ordinal)))
                {
                    continue;
                }

                var freeLane = FindFreeLane(activeLanes, startIndex: 1);
                if (freeLane < 0)
                {
                    activeLanes.Add(parent);
                }
                else
                {
                    activeLanes[freeLane] = parent;
                }
            }

            while (activeLanes.Count > 1 && activeLanes[^1] is null)
            {
                activeLanes.RemoveAt(activeLanes.Count - 1);
            }
            result.Add(new LayoutNode(commit, lane, index));
        }

        layout = result;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static HashSet<string> BuildFirstParentPath(
        string? tipId,
        IReadOnlyDictionary<string, CommitNode> commitById)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var commitId = tipId;
        while (!string.IsNullOrEmpty(commitId) &&
               commitById.TryGetValue(commitId, out var commit) &&
               result.Add(commitId))
        {
            commitId = commit.ParentIds.FirstOrDefault();
        }
        return result;
    }

    private static int FindLane(
        IReadOnlyList<string?> lanes,
        string commitId,
        int startIndex)
    {
        for (var index = startIndex; index < lanes.Count; index++)
        {
            if (string.Equals(lanes[index], commitId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindFreeLane(
        IReadOnlyList<string?> lanes,
        int startIndex)
    {
        for (var index = startIndex; index < lanes.Count; index++)
        {
            if (lanes[index] is null)
            {
                return index;
            }
        }
        return -1;
    }

    internal int? GetLaneForCommit(string commitId) =>
        layout.FirstOrDefault(item =>
            string.Equals(
                item.Commit.Id,
                commitId,
                StringComparison.Ordinal))?.Lane;

    private static FormattedText CreateText(
        string text,
        double size,
        Brush foreground,
        double dpi,
        FontWeight weight) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                weight,
                FontStretches.Normal),
            size,
            foreground,
            dpi);

    private static Pen CreateLanePen(int lane, bool isHovered)
    {
        var pen = new Pen(
            CreateLaneBrush(lane, isHovered ? 1 : 0.72),
            isHovered ? 3.5 : 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateLaneBrush(int lane, double opacity)
    {
        var brush = LaneBrushes[lane % LaneBrushes.Length].Clone();
        brush.Opacity = opacity;
        brush.Freeze();
        return brush;
    }

    private static Color LaneColor(int lane) =>
        ((SolidColorBrush)LaneBrushes[lane % LaneBrushes.Length]).Color;

    private static double LaneX(int lane) => GraphLeft + lane * LaneWidth;
    private static double RowY(int index) => index * RowHeight + RowHeight / 2;

    private sealed record LayoutNode(
        CommitNode Commit,
        int Lane,
        int Index);

    private sealed record BranchBadgeHit(
        Rect Bounds,
        BranchInfo Branch);

    private enum BranchDisplayState
    {
        Current,
        Main,
        Merged,
        Active,
        Remote
    }
}
