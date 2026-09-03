using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public sealed class CommitGraphControl : FrameworkElement
{
	private sealed record LayoutNode(CommitNode Commit, int Lane, int Index);

	private sealed record BranchBadgeHit(Rect Bounds, BranchInfo Branch);

	private enum BranchDisplayState
	{
		Current,
		Main,
		Merged,
		Active,
		Remote
	}

	private const double RowHeight = 50.0;

	private const double LaneWidth = 28.0;

	private const double GraphLeft = 22.0;

	private const double MinimumTextStart = 132.0;

	private const double CollapsedTextStart = 52.0;

	private const double CommitTextDistanceScale = 1.0 / 2.0;

	private const double BadgeHeight = 18.0;

	private static readonly Brush[] LaneBrushes = new Brush[6]
	{
		Brushes.DodgerBlue,
		Brushes.MediumSeaGreen,
		Brushes.MediumPurple,
		Brushes.DarkOrange,
		Brushes.DeepPink,
		Brushes.Teal
	};

	private IReadOnlyList<LayoutNode> layout = Array.Empty<LayoutNode>();

	private readonly List<BranchBadgeHit> branchBadgeHits = new List<BranchBadgeHit>();

	private Rect? headBadgeHit;

	private int? hoveredLane;

	private string? hoveredBranchName;

	private bool isHeadHovered;

	private double graphTextStart = 132.0;

	private int renderedHighestLane;

	public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register("Items", typeof(IEnumerable<CommitNode>), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

	public static readonly DependencyProperty SelectedCommitProperty = DependencyProperty.Register("SelectedCommit", typeof(CommitNode), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	public static readonly DependencyProperty BranchesProperty = DependencyProperty.Register("Branches", typeof(IEnumerable<BranchInfo>), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnReferencesChanged));

	public static readonly DependencyProperty TagsProperty = DependencyProperty.Register("Tags", typeof(IEnumerable<TagInfo>), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnReferencesChanged));

	public static readonly DependencyProperty EventsProperty = DependencyProperty.Register("Events", typeof(IEnumerable<GitHistoryEvent>), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnEventsChanged));

	public static readonly DependencyProperty HeadProperty = DependencyProperty.Register("Head", typeof(HeadInfo), typeof(CommitGraphControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutContextChanged));

	public static readonly DependencyProperty SelectedBranchNameProperty = DependencyProperty.Register("SelectedBranchName", typeof(string), typeof(CommitGraphControl), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutContextChanged));

	public static readonly DependencyProperty IsGraphCollapsedProperty = DependencyProperty.Register("IsGraphCollapsed", typeof(bool), typeof(CommitGraphControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnGraphCollapsedChanged));

	public IEnumerable<CommitNode>? Items
	{
		get
		{
			return (IEnumerable<CommitNode>)GetValue(ItemsProperty);
		}
		set
		{
			SetValue(ItemsProperty, value);
		}
	}

	public CommitNode? SelectedCommit
	{
		get
		{
			return (CommitNode)GetValue(SelectedCommitProperty);
		}
		set
		{
			SetValue(SelectedCommitProperty, value);
		}
	}

	public IEnumerable<BranchInfo>? Branches
	{
		get
		{
			return (IEnumerable<BranchInfo>)GetValue(BranchesProperty);
		}
		set
		{
			SetValue(BranchesProperty, value);
		}
	}

	public IEnumerable<TagInfo>? Tags
	{
		get
		{
			return (IEnumerable<TagInfo>)GetValue(TagsProperty);
		}
		set
		{
			SetValue(TagsProperty, value);
		}
	}

	public IEnumerable<GitHistoryEvent>? Events
	{
		get
		{
			return (IEnumerable<GitHistoryEvent>)GetValue(EventsProperty);
		}
		set
		{
			SetValue(EventsProperty, value);
		}
	}

	public HeadInfo? Head
	{
		get
		{
			return (HeadInfo)GetValue(HeadProperty);
		}
		set
		{
			SetValue(HeadProperty, value);
		}
	}

	public string SelectedBranchName
	{
		get
		{
			return (string)GetValue(SelectedBranchNameProperty);
		}
		set
		{
			SetValue(SelectedBranchNameProperty, value);
		}
	}

	public bool IsGraphCollapsed
	{
		get
		{
			return (bool)GetValue(IsGraphCollapsedProperty);
		}
		set
		{
			SetValue(IsGraphCollapsedProperty, value);
		}
	}

	public event EventHandler<CommitSelectedEventArgs>? CommitSelected;

	public event EventHandler<BranchSelectedEventArgs>? BranchSelected;

	private static void OnItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		CommitGraphControl commitGraphControl = (CommitGraphControl)dependencyObject;
		if (args.OldValue is INotifyCollectionChanged notifyCollectionChanged)
		{
			notifyCollectionChanged.CollectionChanged -= commitGraphControl.OnItemsCollectionChanged;
		}
		if (args.NewValue is INotifyCollectionChanged notifyCollectionChanged2)
		{
			notifyCollectionChanged2.CollectionChanged += commitGraphControl.OnItemsCollectionChanged;
		}
		commitGraphControl.RebuildLayout();
	}

	private static void OnReferencesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		CommitGraphControl commitGraphControl = (CommitGraphControl)dependencyObject;
		if (args.OldValue is INotifyCollectionChanged notifyCollectionChanged)
		{
			notifyCollectionChanged.CollectionChanged -= commitGraphControl.OnReferencesCollectionChanged;
		}
		if (args.NewValue is INotifyCollectionChanged notifyCollectionChanged2)
		{
			notifyCollectionChanged2.CollectionChanged += commitGraphControl.OnReferencesCollectionChanged;
		}
		commitGraphControl.RebuildLayout();
	}

	private static void OnEventsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		CommitGraphControl commitGraphControl = (CommitGraphControl)dependencyObject;
		if (args.OldValue is INotifyCollectionChanged notifyCollectionChanged)
		{
			notifyCollectionChanged.CollectionChanged -= commitGraphControl.OnEventsCollectionChanged;
		}
		if (args.NewValue is INotifyCollectionChanged notifyCollectionChanged2)
		{
			notifyCollectionChanged2.CollectionChanged += commitGraphControl.OnEventsCollectionChanged;
		}
		commitGraphControl.InvalidateVisual();
	}

	private static void OnLayoutContextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		((CommitGraphControl)dependencyObject).RebuildLayout();
	}

	private static void OnGraphCollapsedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		CommitGraphControl obj = (CommitGraphControl)dependencyObject;
		obj.hoveredLane = null;
		obj.Cursor = Cursors.Arrow;
		obj.ToolTip = null;
	}

	private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		RebuildLayout();
	}

	private void OnReferencesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		RebuildLayout();
	}

	private void OnEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		return new Size(double.IsInfinity(availableSize.Width) ? 900.0 : Math.Max(availableSize.Width, 1.0), Math.Max((double)layout.Count * 50.0, 80.0));
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		Brush brush = (TryFindResource(SystemColors.ControlTextBrushKey) as Brush) ?? Brushes.Black;
		Brush brush2 = brush.Clone();
		brush2.Opacity = 0.62;
		Brush brush3 = ((TryFindResource(SystemColors.HighlightBrushKey) as Brush) ?? Brushes.DodgerBlue).Clone();
		brush3.Opacity = 0.14;
		Brush brush4 = ((TryFindResource(SystemColors.HighlightBrushKey) as Brush) ?? Brushes.DodgerBlue).Clone();
		brush4.Opacity = 0.72;
		double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		Dictionary<string, LayoutNode> nodeById = layout.ToDictionary<LayoutNode, string>((LayoutNode item) => item.Commit.Id, StringComparer.Ordinal);
		BranchInfo[] source = Branches?.ToArray() ?? Array.Empty<BranchInfo>();
		TagInfo[] source2 = Tags?.ToArray() ?? Array.Empty<TagInfo>();
		GitHistoryEvent[] source3 = Events?.ToArray() ?? Array.Empty<GitHistoryEvent>();
		Dictionary<string, BranchInfo[]> branchesByTip = source.Where((BranchInfo branch) => !string.IsNullOrEmpty(branch.TipId)).GroupBy<BranchInfo, string>((BranchInfo branch) => branch.TipId, StringComparer.Ordinal).ToDictionary<IGrouping<string, BranchInfo>, string, BranchInfo[]>((IGrouping<string, BranchInfo> group) => group.Key, (IGrouping<string, BranchInfo> group) => group.ToArray(), StringComparer.Ordinal);
		Dictionary<string, TagInfo[]> tagsByTarget = source2.Where((TagInfo tag) => !string.IsNullOrEmpty(tag.TargetId)).GroupBy<TagInfo, string>((TagInfo tag) => tag.TargetId, StringComparer.Ordinal).ToDictionary<IGrouping<string, TagInfo>, string, TagInfo[]>((IGrouping<string, TagInfo> group) => group.Key, (IGrouping<string, TagInfo> group) => group.ToArray(), StringComparer.Ordinal);
		Dictionary<string, GitHistoryEvent[]> eventsByCommit = source3.Where((GitHistoryEvent historyEvent) => !string.IsNullOrEmpty(historyEvent.CommitId)).GroupBy<GitHistoryEvent, string>((GitHistoryEvent historyEvent) => historyEvent.CommitId, StringComparer.Ordinal).ToDictionary<IGrouping<string, GitHistoryEvent>, string, GitHistoryEvent[]>((IGrouping<string, GitHistoryEvent> group) => group.Key, (IGrouping<string, GitHistoryEvent> group) => group.OrderBy((GitHistoryEvent historyEvent) => historyEvent.OccurredAt).ToArray(), StringComparer.Ordinal);
		int num = ((layout.Count != 0) ? layout.Max((LayoutNode item) => item.Lane) : 0);
		graphTextStart = GetCommitTextStart();
		renderedHighestLane = ((!IsGraphCollapsed) ? num : 0);
		branchBadgeHits.Clear();
		headBadgeHit = null;
		drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0.0, 0.0, Math.Max(base.ActualWidth, 1.0), Math.Max((double)layout.Count * 50.0, 1.0)));
		DrawSelection(drawingContext, brush3, brush4);
		if (IsGraphCollapsed)
		{
			DrawCollapsedGraph(drawingContext);
		}
		else
		{
			DrawParentConnections(drawingContext, nodeById);
		}
		foreach (LayoutNode item in layout)
		{
			DrawCommit(drawingContext, item, brush, brush2, branchesByTip, tagsByTarget, eventsByCommit, nodeById, pixelsPerDip);
		}
	}

	private void DrawCollapsedGraph(DrawingContext drawingContext)
	{
		if (layout.Count == 0)
		{
			return;
		}
		double x = LaneX(0);
		Brush brush = CreateLaneBrush(0, 0.82);
		if (layout.Count > 1)
		{
			Pen pen = new Pen(brush, 2.5)
			{
				StartLineCap = PenLineCap.Round,
				EndLineCap = PenLineCap.Round
			};
			pen.Freeze();
			drawingContext.DrawLine(pen, new Point(x, RowY(0)), new Point(x, RowY(layout.Count - 1)));
		}
		foreach (LayoutNode item in layout)
		{
			Point center = new Point(x, RowY(item.Index));
			drawingContext.DrawEllipse(CreateLaneBrush(0, 0.96), new Pen(Brushes.White, 1.5), center, 6.0, 6.0);
			if (string.Equals(item.Commit.Id, SelectedCommit?.Id, StringComparison.Ordinal))
			{
				drawingContext.DrawEllipse(null, new Pen(CreateLaneBrush(0, 1.0), 2.0), center, 10.0, 10.0);
			}
		}
	}

	private void DrawSelection(DrawingContext drawingContext, Brush selectionFill, Brush selectionBorder)
	{
		LayoutNode layoutNode = layout.FirstOrDefault((LayoutNode item) => string.Equals(item.Commit.Id, SelectedCommit?.Id, StringComparison.Ordinal));
		if ((object)layoutNode != null)
		{
			drawingContext.DrawRoundedRectangle(selectionFill, new Pen(selectionBorder, 1.0), new Rect(1.0, (double)layoutNode.Index * 50.0 + 1.0, Math.Max(0.0, base.ActualWidth - 2.0), 48.0), 4.0, 4.0);
		}
	}

	private void DrawParentConnections(DrawingContext drawingContext, IReadOnlyDictionary<string, LayoutNode> nodeById)
	{
		foreach (LayoutNode item in layout)
		{
			double x = LaneX(item.Lane);
			double num = RowY(item.Index);
			foreach (string parentId in item.Commit.ParentIds)
			{
				bool isHovered = hoveredLane == item.Lane;
				Pen pen = CreateLanePen(item.Lane, isHovered);
				if (!nodeById.TryGetValue(parentId, out LayoutNode value))
				{
					drawingContext.DrawLine(pen, new Point(x, num), new Point(x, num + 25.0));
					continue;
				}
				double x2 = LaneX(value.Lane);
				double num2 = RowY(value.Index);
				StreamGeometry streamGeometry = new StreamGeometry();
				using (StreamGeometryContext streamGeometryContext = streamGeometry.Open())
				{
					streamGeometryContext.BeginFigure(new Point(x, num), isFilled: false, isClosed: false);
					double num3 = Math.Max(50.0, num2 - num);
					double y = num + Math.Min(36.0, num3 / 2.0);
					streamGeometryContext.BezierTo(new Point(x, y), new Point(x2, y), new Point(x2, num2), isStroked: true, isSmoothJoin: false);
				}
				streamGeometry.Freeze();
				drawingContext.DrawGeometry(null, pen, streamGeometry);
			}
		}
	}

	private void DrawCommit(DrawingContext drawingContext, LayoutNode item, Brush foreground, Brush secondary, IReadOnlyDictionary<string, BranchInfo[]> branchesByTip, IReadOnlyDictionary<string, TagInfo[]> tagsByTarget, IReadOnlyDictionary<string, GitHistoryEvent[]> eventsByCommit, IReadOnlyDictionary<string, LayoutNode> nodeById, double dpi)
	{
		double x = LaneX(item.Lane);
		double num = RowY(item.Index);
		bool flag = hoveredLane == item.Lane;
		GitHistoryEvent[] source = eventsByCommit.GetValueOrDefault(item.Commit.Id) ?? Array.Empty<GitHistoryEvent>();
		bool flag2 = item.Commit.ParentIds.Count > 1 || source.Any((GitHistoryEvent historyEvent) => historyEvent.Kind == GitHistoryEventKind.Merge);
		bool flag3 = source.Any((GitHistoryEvent historyEvent) => historyEvent.Kind == GitHistoryEventKind.Revert);
		if (!IsGraphCollapsed)
		{
			drawingContext.DrawEllipse(flag3 ? Brushes.White : CreateLaneBrush(item.Lane, flag ? 1.0 : 0.92), new Pen(flag3 ? CreateLaneBrush(item.Lane, 1.0) : Brushes.White, flag ? 2.0 : 1.5), new Point(x, num), flag ? 7 : 6, flag ? 7 : 6);
			if (flag2)
			{
				drawingContext.DrawEllipse(null, new Pen(CreateLaneBrush(item.Lane, 0.92), 2.0), new Point(x, num), 10.0, 10.0);
			}
		}
		FormattedText formattedText = CreateText(item.Commit.Message, 13.0, foreground, dpi, FontWeights.Normal);
		formattedText.MaxTextWidth = Math.Max(100.0, base.ActualWidth - graphTextStart - 24.0);
		formattedText.Trimming = TextTrimming.CharacterEllipsis;
		drawingContext.DrawText(formattedText, new Point(graphTextStart, num - 18.0));
		double num2 = graphTextStart;
		if ((object)Head != null && string.Equals(Head.CommitId, item.Commit.Id, StringComparison.Ordinal))
		{
			num2 = DrawHeadBadge(drawingContext, num2, num + 2.0, dpi);
		}
		foreach (GitHistoryEvent item2 in (from historyEvent in source
			where historyEvent.Kind != GitHistoryEventKind.CommitCreated
			group historyEvent by (Kind: historyEvent.Kind, BranchName: historyEvent.BranchName) into @group
			select @group.Last()).Take(3))
		{
			num2 = DrawEventBadge(drawingContext, item2, num2, num + 2.0, dpi);
		}
		if (branchesByTip.TryGetValue(item.Commit.Id, out BranchInfo[] value))
		{
			foreach (BranchInfo item3 in (from branch in value
				orderby branch.IsCurrent descending, branch.IsRemote
				select branch).ThenBy<BranchInfo, string>((BranchInfo branch) => branch.FriendlyName, StringComparer.CurrentCulture))
			{
				num2 = DrawBranchBadge(drawingContext, item3, ResolveBranchDisplayState(item3, nodeById), item.Lane, num2, num + 2.0, dpi);
			}
		}
		if (tagsByTarget.TryGetValue(item.Commit.Id, out TagInfo[] value2))
		{
			foreach (TagInfo item4 in value2.OrderBy<TagInfo, string>((TagInfo tag) => tag.Name, StringComparer.CurrentCulture))
			{
				num2 = DrawTagBadge(drawingContext, item4, num2, num + 2.0, dpi);
			}
		}
		string value3 = ((item.Commit.ParentIds.Count > 1) ? $"  合并提交 · {item.Commit.ParentIds.Count} 个父提交" : string.Empty);
		FormattedText formattedText2 = CreateText($"{item.Commit.ShortId}  {item.Commit.AuthorName}  {item.Commit.AuthoredAt.LocalDateTime:g}{value3}", 10.5, secondary, dpi, FontWeights.Normal);
		formattedText2.MaxTextWidth = Math.Max(100.0, base.ActualWidth - num2 - 20.0);
		formattedText2.Trimming = TextTrimming.CharacterEllipsis;
		drawingContext.DrawText(formattedText2, new Point(num2, num + 4.0));
	}

	private double DrawHeadBadge(DrawingContext drawingContext, double startX, double top, double dpi)
	{
		HeadInfo? head = Head;
		FormattedText formattedText = CreateText(((object)head != null && head.IsDetached) ? "HEAD（游离）" : "HEAD", 10.0, Brushes.White, dpi, FontWeights.SemiBold);
		double num = Math.Max(44.0, formattedText.Width + 14.0);
		Rect rect = new Rect(startX, top, num, 18.0);
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromRgb(230, 126, 34));
		solidColorBrush.Freeze();
		drawingContext.DrawRoundedRectangle(solidColorBrush, new Pen(solidColorBrush, 1.0), rect, 8.0, 8.0);
		drawingContext.DrawText(formattedText, new Point(startX + 7.0, top + 1.5));
		headBadgeHit = rect;
		return startX + num + 6.0;
	}

	private double DrawBranchBadge(DrawingContext drawingContext, BranchInfo branch, BranchDisplayState state, int lane, double startX, double top, double dpi)
	{
		bool flag = string.Equals(branch.FriendlyName, SelectedBranchName, StringComparison.Ordinal);
		bool flag2 = string.Equals(branch.FriendlyName, hoveredBranchName, StringComparison.Ordinal);
		bool flag3 = branch.IsCurrent | flag | flag2;
		string text = state switch
		{
			BranchDisplayState.Current => "当前",
			BranchDisplayState.Main => "主分支",
			BranchDisplayState.Merged => "已合并",
			BranchDisplayState.Active => "活跃",
			BranchDisplayState.Remote => "远程",
			_ => string.Empty,
		};
		string text2 = (string.IsNullOrEmpty(text) ? branch.FriendlyName : (branch.FriendlyName + " · " + text));
		Brush brush;
		if (flag3)
		{
			brush = Brushes.White;
		}
		else
		{
			bool flag4 = ((state == BranchDisplayState.Merged || state == BranchDisplayState.Remote) ? true : false);
			brush = (flag4 ? Brushes.DimGray : CreateLaneBrush(lane, 1.0));
		}
		Brush foreground = brush;
		FormattedText formattedText = CreateText(text2, 10.0, foreground, dpi, flag3 ? FontWeights.SemiBold : FontWeights.Normal);
		formattedText.MaxTextWidth = 150.0;
		formattedText.Trimming = TextTrimming.CharacterEllipsis;
		double num = Math.Min(164.0, Math.Max(38.0, formattedText.Width + 14.0));
		Rect rect = new Rect(startX, top, num, 18.0);
		object color = state switch
		{
			BranchDisplayState.Remote => Color.FromRgb(108, 117, 125),
			BranchDisplayState.Merged => Color.FromRgb(125, 133, 140),
			_ => LaneColor(lane),
		};
		SolidColorBrush solidColorBrush = new SolidColorBrush((Color)color)
		{
			Opacity = (flag3 ? 0.92 : 0.12)
		};
		SolidColorBrush solidColorBrush2 = new SolidColorBrush((Color)color)
		{
			Opacity = (flag3 ? 1.0 : 0.72)
		};
		solidColorBrush.Freeze();
		solidColorBrush2.Freeze();
		drawingContext.DrawRoundedRectangle(solidColorBrush, new Pen(solidColorBrush2, flag3 ? 1.4 : 1.0), rect, 8.0, 8.0);
		drawingContext.DrawText(formattedText, new Point(startX + 7.0, top + 1.5));
		branchBadgeHits.Add(new BranchBadgeHit(rect, branch));
		return startX + num + 6.0;
	}

	private static double DrawEventBadge(DrawingContext drawingContext, GitHistoryEvent historyEvent, double startX, double top, double dpi)
	{
		(string item, Color item2) = historyEvent.Kind switch
		{
			GitHistoryEventKind.BranchCreated => ("分叉 · " + historyEvent.BranchName, Color.FromRgb(126, 87, 194)),
			GitHistoryEventKind.BranchDeleted => ("已删除 · " + historyEvent.BranchName, Color.FromRgb(176, 75, 75)),
			GitHistoryEventKind.Checkout => ("checkout", Color.FromRgb(44, 123, 229)),
			GitHistoryEventKind.Reset => ("reset", Color.FromRgb(219, 126, 36)),
			GitHistoryEventKind.Merge => ("merge", Color.FromRgb(38, 145, 96)),
			GitHistoryEventKind.Revert => ("revert", Color.FromRgb(204, 82, 82)),
			_ => (historyEvent.Kind.ToString(), Color.FromRgb(108, 117, 125)),
		};
		SolidColorBrush solidColorBrush = new SolidColorBrush(item2);
		solidColorBrush.Freeze();
		FormattedText formattedText = CreateText(item, 10.0, solidColorBrush, dpi, FontWeights.SemiBold);
		formattedText.MaxTextWidth = 140.0;
		formattedText.Trimming = TextTrimming.CharacterEllipsis;
		double num = Math.Min(154.0, Math.Max(44.0, formattedText.Width + 14.0));
		Rect rectangle = new Rect(startX, top, num, 18.0);
		SolidColorBrush solidColorBrush2 = new SolidColorBrush(item2)
		{
			Opacity = 0.12
		};
		SolidColorBrush solidColorBrush3 = new SolidColorBrush(item2)
		{
			Opacity = 0.82
		};
		solidColorBrush2.Freeze();
		solidColorBrush3.Freeze();
		drawingContext.DrawRoundedRectangle(solidColorBrush2, new Pen(solidColorBrush3, 1.0), rectangle, 8.0, 8.0);
		drawingContext.DrawText(formattedText, new Point(startX + 7.0, top + 1.5));
		return startX + num + 6.0;
	}

	private static double DrawTagBadge(DrawingContext drawingContext, TagInfo tag, double startX, double top, double dpi)
	{
		FormattedText formattedText = CreateText("tag: " + tag.Name, 10.0, Brushes.DimGray, dpi, FontWeights.Normal);
		formattedText.MaxTextWidth = 140.0;
		formattedText.Trimming = TextTrimming.CharacterEllipsis;
		double num = Math.Min(154.0, Math.Max(44.0, formattedText.Width + 14.0));
		Rect rectangle = new Rect(startX, top, num, 18.0);
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromArgb(22, 96, 103, 112));
		SolidColorBrush solidColorBrush2 = new SolidColorBrush(Color.FromArgb(150, 108, 117, 125));
		solidColorBrush.Freeze();
		solidColorBrush2.Freeze();
		drawingContext.DrawRoundedRectangle(solidColorBrush, new Pen(solidColorBrush2, 1.0), rectangle, 8.0, 8.0);
		drawingContext.DrawText(formattedText, new Point(startX + 7.0, top + 1.5));
		return startX + num + 6.0;
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		Point position = e.GetPosition(this);
		BranchBadgeHit branchBadgeHit = branchBadgeHits.FirstOrDefault((BranchBadgeHit item) => item.Bounds.Contains(position));
		bool flag = headBadgeHit?.Contains(position) ?? false;
		int? num = null;
		if ((object)branchBadgeHit == null && !flag && position.X >= 12.0 && position.X < graphTextStart)
		{
			int num2 = (int)Math.Round((position.X - 22.0) / 28.0);
			if (num2 >= 0 && num2 <= renderedHighestLane && Math.Abs(position.X - LaneX(num2)) <= 9.0)
			{
				num = num2;
			}
		}
		string a = branchBadgeHit?.Branch.FriendlyName;
		if (string.Equals(a, hoveredBranchName, StringComparison.Ordinal) && num == hoveredLane && flag == isHeadHovered)
		{
			return;
		}
		hoveredBranchName = a;
		hoveredLane = num;
		isHeadHovered = flag;
		base.Cursor = (((object)branchBadgeHit == null) ? Cursors.Arrow : Cursors.Hand);
		object toolTip;
		if ((object)branchBadgeHit == null)
		{
			if (!flag)
			{
				toolTip = (num.HasValue ? $"提交线路 {num + 1}\n颜色仅用于区分提交关系线路" : null);
			}
			else
			{
				HeadInfo? head = Head;
				toolTip = (((object)head != null && head.IsDetached) ? "HEAD 当前直接指向此提交（游离状态）" : ("HEAD 当前附着于分支 " + Head?.BranchName));
			}
		}
		else
		{
			toolTip = branchBadgeHit.Branch.FriendlyName + "\n分支指针指向此提交；单击查看该分支";
		}
		base.ToolTip = toolTip;
		InvalidateVisual();
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		base.OnMouseLeave(e);
		if (hoveredBranchName == null)
		{
			int? num = hoveredLane;
			if (!num.HasValue && !isHeadHovered)
			{
				return;
			}
		}
		hoveredBranchName = null;
		hoveredLane = null;
		isHeadHovered = false;
		base.Cursor = Cursors.Arrow;
		base.ToolTip = null;
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		Point position = e.GetPosition(this);
		BranchBadgeHit branchBadgeHit = branchBadgeHits.FirstOrDefault((BranchBadgeHit item) => item.Bounds.Contains(position));
		if ((object)branchBadgeHit != null)
		{
			BranchSelected?.Invoke(this, new BranchSelectedEventArgs(branchBadgeHit.Branch));
			Focus();
			e.Handled = true;
			return;
		}
		int num = (int)(position.Y / 50.0);
		if (num >= 0 && num < layout.Count)
		{
			CommitNode commit = layout[num].Commit;
			SetCurrentValue(SelectedCommitProperty, commit);
			CommitSelected?.Invoke(this, new CommitSelectedEventArgs(commit));
			Focus();
		}
	}

	protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseRightButtonDown(e);
		int num = (int)(e.GetPosition(this).Y / 50.0);
		if (num >= 0 && num < layout.Count)
		{
			CommitNode commit = layout[num].Commit;
			SetCurrentValue(SelectedCommitProperty, commit);
			CommitSelected?.Invoke(this, new CommitSelectedEventArgs(commit));
			Focus();
		}
	}

	private BranchDisplayState ResolveBranchDisplayState(BranchInfo branch, IReadOnlyDictionary<string, LayoutNode> nodeById)
	{
		if (branch.IsRemote)
		{
			return BranchDisplayState.Remote;
		}
		if (branch.IsCurrent || string.Equals(branch.FriendlyName, Head?.BranchName, StringComparison.Ordinal))
		{
			return BranchDisplayState.Current;
		}
		if (string.Equals(branch.FriendlyName, "main", StringComparison.OrdinalIgnoreCase) || string.Equals(branch.FriendlyName, "master", StringComparison.OrdinalIgnoreCase))
		{
			return BranchDisplayState.Main;
		}
		if ((object)Head != null && IsAncestor(branch.TipId, Head.CommitId, nodeById))
		{
			return BranchDisplayState.Merged;
		}
		return BranchDisplayState.Active;
	}

	private static bool IsAncestor(string ancestorId, string descendantId, IReadOnlyDictionary<string, LayoutNode> nodeById)
	{
		Stack<string> stack = new Stack<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		stack.Push(descendantId);
		while (stack.Count > 0)
		{
			string text = stack.Pop();
			if (!hashSet.Add(text))
			{
				continue;
			}
			if (string.Equals(text, ancestorId, StringComparison.Ordinal))
			{
				return true;
			}
			if (!nodeById.TryGetValue(text, out LayoutNode value))
			{
				continue;
			}
			foreach (string parentId in value.Commit.ParentIds)
			{
				stack.Push(parentId);
			}
		}
		return false;
	}

	private void RebuildLayout()
	{
		CommitNode[] array = Items?.ToArray() ?? Array.Empty<CommitNode>();
		Dictionary<string, CommitNode> dictionary = array.ToDictionary<CommitNode, string>((CommitNode commit) => commit.Id, StringComparer.Ordinal);
		string text = Branches?.FirstOrDefault((BranchInfo branch) => string.Equals(branch.FriendlyName, SelectedBranchName, StringComparison.Ordinal))?.TipId;
		string text2 = ((!string.IsNullOrEmpty(text)) ? text : Head?.CommitId);
		if (string.IsNullOrEmpty(text2) || !dictionary.ContainsKey(text2))
		{
			text2 = array.FirstOrDefault()?.Id;
		}
		HashSet<string> hashSet = BuildFirstParentPath(text2, dictionary);
		List<string> list = new List<string> { text2 };
		List<LayoutNode> list2 = new List<LayoutNode>(array.Length);
		for (int num = 0; num < array.Length; num++)
		{
			CommitNode commitNode = array[num];
			bool flag = hashSet.Contains(commitNode.Id);
			int num2 = ((!flag) ? FindLane(list, commitNode.Id, 1) : 0);
			if (num2 < 0)
			{
				num2 = FindFreeLane(list, 1);
				if (num2 < 0)
				{
					num2 = list.Count;
					list.Add(null);
				}
			}
			while (list.Count <= num2)
			{
				list.Add(null);
			}
			for (int num3 = 0; num3 < list.Count; num3++)
			{
				if (num3 != num2 && string.Equals(list[num3], commitNode.Id, StringComparison.Ordinal))
				{
					list[num3] = null;
				}
			}
			string[] source = commitNode.ParentIds.Distinct<string>(StringComparer.Ordinal).ToArray();
			string text3 = source.FirstOrDefault();
			if (text3 == null || (!flag && hashSet.Contains(text3)))
			{
				list[num2] = null;
			}
			else
			{
				for (int num4 = 0; num4 < list.Count; num4++)
				{
					if (num4 != num2 && string.Equals(list[num4], text3, StringComparison.Ordinal))
					{
						list[num4] = null;
					}
				}
				list[num2] = text3;
			}
			foreach (string parent in source.Skip(1))
			{
				if (!hashSet.Contains(parent) && !list.Any((string id) => string.Equals(id, parent, StringComparison.Ordinal)))
				{
					int num5 = FindFreeLane(list, 1);
					if (num5 < 0)
					{
						list.Add(parent);
					}
					else
					{
						list[num5] = parent;
					}
				}
			}
			while (list.Count > 1)
			{
				if (list[list.Count - 1] != null)
				{
					break;
				}
				list.RemoveAt(list.Count - 1);
			}
			list2.Add(new LayoutNode(commitNode, num2, num));
		}
		layout = list2;
		InvalidateMeasure();
		InvalidateVisual();
	}

	private static HashSet<string> BuildFirstParentPath(string? tipId, IReadOnlyDictionary<string, CommitNode> commitById)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		string text = tipId;
		CommitNode value;
		while (!string.IsNullOrEmpty(text) && commitById.TryGetValue(text, out value) && hashSet.Add(text))
		{
			text = value.ParentIds.FirstOrDefault();
		}
		return hashSet;
	}

	private static int FindLane(IReadOnlyList<string?> lanes, string commitId, int startIndex)
	{
		for (int i = startIndex; i < lanes.Count; i++)
		{
			if (string.Equals(lanes[i], commitId, StringComparison.Ordinal))
			{
				return i;
			}
		}
		return -1;
	}

	private static int FindFreeLane(IReadOnlyList<string?> lanes, int startIndex)
	{
		for (int i = startIndex; i < lanes.Count; i++)
		{
			if (lanes[i] == null)
			{
				return i;
			}
		}
		return -1;
	}

	internal int? GetLaneForCommit(string commitId)
	{
		return layout.FirstOrDefault((LayoutNode item) => string.Equals(item.Commit.Id, commitId, StringComparison.Ordinal))?.Lane;
	}

	internal int? GetRenderedLaneForCommit(string commitId)
	{
		int? laneForCommit = GetLaneForCommit(commitId);
		if (laneForCommit.HasValue && IsGraphCollapsed)
		{
			return 0;
		}
		return laneForCommit;
	}

	internal double GetCommitTextStart()
	{
		if (IsGraphCollapsed)
		{
			return 52.0;
		}
		int highestLane = ((layout.Count != 0) ? layout.Max((LayoutNode item) => item.Lane) : 0);
		double farthestLaneX = LaneX(highestLane);
		double previousTextStart = Math.Max(132.0, 22.0 + (double)(highestLane + 1) * 28.0 + 24.0);
		return farthestLaneX + (previousTextStart - farthestLaneX) * CommitTextDistanceScale;
	}

	private static FormattedText CreateText(string text, double size, Brush foreground, double dpi, FontWeight weight)
	{
		return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, foreground, dpi);
	}

	private static Pen CreateLanePen(int lane, bool isHovered)
	{
		Pen pen = new Pen(CreateLaneBrush(lane, isHovered ? 1.0 : 0.72), isHovered ? 3.5 : 2.0);
		pen.StartLineCap = PenLineCap.Round;
		pen.EndLineCap = PenLineCap.Round;
		pen.LineJoin = PenLineJoin.Round;
		pen.Freeze();
		return pen;
	}

	private static Brush CreateLaneBrush(int lane, double opacity)
	{
		Brush brush = LaneBrushes[lane % LaneBrushes.Length].Clone();
		brush.Opacity = opacity;
		brush.Freeze();
		return brush;
	}

	private static Color LaneColor(int lane)
	{
		return ((SolidColorBrush)LaneBrushes[lane % LaneBrushes.Length]).Color;
	}

	private static double LaneX(int lane)
	{
		return 22.0 + (double)lane * 28.0;
	}

	private static double RowY(int index)
	{
		return (double)index * 50.0 + 25.0;
	}
}
