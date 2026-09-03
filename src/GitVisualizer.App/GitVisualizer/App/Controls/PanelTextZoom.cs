using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ICSharpCode.AvalonEdit;

namespace GitVisualizer.App.Controls;

public static class PanelTextZoom
{
	public const double MinimumScale = 1.0;

	public const double MaximumScale = 2.0;

	public const double ScaleStep = 0.1;

	private sealed class ScopeState
	{
		public bool RefreshScheduled { get; set; }

		public bool WatchLayoutUpdates { get; set; }
	}

	private static readonly ConditionalWeakTable<FrameworkElement, ScopeState> Scopes = new();

	private static readonly DependencyProperty OriginalFontSizeProperty = DependencyProperty.RegisterAttached(
		"OriginalFontSize",
		typeof(double),
		typeof(PanelTextZoom),
		new PropertyMetadata(double.NaN));

	private static readonly DependencyProperty AppliedFontSizeProperty = DependencyProperty.RegisterAttached(
		"AppliedFontSize",
		typeof(double),
		typeof(PanelTextZoom),
		new PropertyMetadata(double.NaN));

	public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
		"Scale",
		typeof(double),
		typeof(PanelTextZoom),
		new FrameworkPropertyMetadata(
			MinimumScale,
			FrameworkPropertyMetadataOptions.Inherits,
			OnScaleChanged));

	public static readonly DependencyProperty IsExcludedProperty = DependencyProperty.RegisterAttached(
		"IsExcluded",
		typeof(bool),
		typeof(PanelTextZoom),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

	public static readonly DependencyProperty MaximumElementScaleProperty = DependencyProperty.RegisterAttached(
		"MaximumElementScale",
		typeof(double),
		typeof(PanelTextZoom),
		new FrameworkPropertyMetadata(MaximumScale));

	public static readonly DependencyProperty IsScaleBoundaryProperty = DependencyProperty.RegisterAttached(
		"IsScaleBoundary",
		typeof(bool),
		typeof(PanelTextZoom),
		new FrameworkPropertyMetadata(false));

	public static void SetIsExcluded(DependencyObject element, bool value)
	{
		element.SetValue(IsExcludedProperty, value);
	}

	public static bool GetIsExcluded(DependencyObject element)
	{
		return (bool)element.GetValue(IsExcludedProperty);
	}

	public static void SetMaximumElementScale(DependencyObject element, double value)
	{
		element.SetValue(MaximumElementScaleProperty, NormalizeScale(value));
	}

	public static double GetMaximumElementScale(DependencyObject element)
	{
		return NormalizeScale((double)element.GetValue(MaximumElementScaleProperty));
	}

	public static void SetIsScaleBoundary(DependencyObject element, bool value)
	{
		element.SetValue(IsScaleBoundaryProperty, value);
	}

	public static bool GetIsScaleBoundary(DependencyObject element)
	{
		return (bool)element.GetValue(IsScaleBoundaryProperty);
	}

	public static void Attach(FrameworkElement scope, bool watchLayoutUpdates = false)
	{
		ArgumentNullException.ThrowIfNull(scope);
		if (!Scopes.TryGetValue(scope, out ScopeState? state))
		{
			state = new ScopeState();
			Scopes.Add(scope, state);
			scope.LayoutUpdated += Scope_OnLayoutUpdated;
		}
		state.WatchLayoutUpdates |= watchLayoutUpdates;
		ApplyTree(scope);
		ScheduleRefresh(scope);
	}

	public static void SetScale(FrameworkElement scope, double scale)
	{
		ArgumentNullException.ThrowIfNull(scope);
		Attach(scope);
		scope.SetValue(ScaleProperty, NormalizeScale(scale));
		ApplyTree(scope);
		ScheduleRefresh(scope);
	}

	public static double GetScale(FrameworkElement scope)
	{
		ArgumentNullException.ThrowIfNull(scope);
		return Scopes.TryGetValue(scope, out _) ? (double)scope.GetValue(ScaleProperty) : MinimumScale;
	}

	internal static double CalculateNextScale(double currentScale, int wheelDelta)
	{
		if (wheelDelta == 0)
		{
			return NormalizeScale(currentScale);
		}

		double direction = wheelDelta > 0 ? 1.0 : -1.0;
		return NormalizeScale(currentScale + direction * ScaleStep);
	}

	internal static bool IsZoomAllowed(int tabIndex, bool isDetached)
	{
		return tabIndex is 1 or 2 || isDetached && tabIndex is 0 or 3 or 4;
	}

	internal static bool IsZoomInputAllowed(
		int tabIndex,
		bool isDetached,
		bool hasZoomableEditorContent)
	{
		return IsZoomAllowed(tabIndex, isDetached) &&
			(tabIndex != 1 || hasZoomableEditorContent);
	}

	internal static double GetEffectiveScale(int tabIndex, bool isDetached, double rememberedScale)
	{
		return IsZoomAllowed(tabIndex, isDetached) ? NormalizeScale(rememberedScale) : MinimumScale;
	}

	internal static double NormalizeScale(double scale)
	{
		if (!double.IsFinite(scale))
		{
			return MinimumScale;
		}

		return Math.Round(Math.Clamp(scale, MinimumScale, MaximumScale), 1, MidpointRounding.AwayFromZero);
	}

	private static void OnScaleChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
	{
		if (element is FrameworkElement frameworkElement && e.NewValue is double scale)
		{
			ApplyFontSize(frameworkElement, NormalizeScale(scale));
			if (!frameworkElement.IsLoaded)
			{
				frameworkElement.Dispatcher.BeginInvoke(
					System.Windows.Threading.DispatcherPriority.Loaded,
					new Action(() => ApplyFontSize(
						frameworkElement,
						NormalizeScale((double)frameworkElement.GetValue(ScaleProperty)))));
			}
		}
	}

	private static void Scope_OnLayoutUpdated(object? sender, EventArgs e)
	{
		if (sender is FrameworkElement scope &&
			Scopes.TryGetValue(scope, out ScopeState? state) &&
			state.WatchLayoutUpdates &&
			GetScale(scope) > MinimumScale)
		{
			ScheduleRefresh(scope);
		}
	}

	private static void ScheduleRefresh(FrameworkElement scope)
	{
		if (!Scopes.TryGetValue(scope, out ScopeState? state) || state.RefreshScheduled)
		{
			return;
		}

		state.RefreshScheduled = true;
		scope.Dispatcher.BeginInvoke(
			System.Windows.Threading.DispatcherPriority.ContextIdle,
			new Action(() =>
			{
				state.RefreshScheduled = false;
				ApplyTree(scope);
			}));
	}

	private static DependencyObject? GetParent(DependencyObject element)
	{
		if (element is Visual || element is Visual3D)
		{
			DependencyObject? visualParent = VisualTreeHelper.GetParent(element);
			if (visualParent != null)
			{
				return visualParent;
			}
		}

		return LogicalTreeHelper.GetParent(element);
	}

	private static void ApplyTree(FrameworkElement scope)
	{
		HashSet<DependencyObject> visited = new(ReferenceEqualityComparer.Instance);
		Visit(scope, scope, (double)scope.GetValue(ScaleProperty), visited);
	}

	private static void Visit(DependencyObject scope, DependencyObject element, double scale, HashSet<DependencyObject> visited)
	{
		if (!visited.Add(element))
		{
			return;
		}
		if (!ReferenceEquals(scope, element) && GetIsScaleBoundary(element))
		{
			return;
		}

		if (element is FrameworkElement frameworkElement)
		{
			ApplyFontSize(frameworkElement, scale);
		}

		if (element is Visual || element is Visual3D)
		{
			int childCount = VisualTreeHelper.GetChildrenCount(element);
			for (int index = 0; index < childCount; index++)
			{
				Visit(scope, VisualTreeHelper.GetChild(element, index), scale, visited);
			}
		}

		foreach (object child in LogicalTreeHelper.GetChildren(element))
		{
			if (child is DependencyObject dependencyObject)
			{
				Visit(scope, dependencyObject, scale, visited);
			}
		}
	}

	private static void ApplyFontSize(FrameworkElement element, double scale)
	{
		if (GetIsExcluded(element) || IsInsideButton(element))
		{
			return;
		}

		DependencyProperty? fontSizeProperty = element switch
		{
			TextBlock textBlock when !IsDecorativeGlyph(textBlock) => TextBlock.FontSizeProperty,
			TextBox => Control.FontSizeProperty,
			TextEditor => Control.FontSizeProperty,
			_ => null
		};
		if (fontSizeProperty == null || BindingOperations.IsDataBound(element, fontSizeProperty))
		{
			return;
		}

		double current = (double)element.GetValue(fontSizeProperty);
		double original = (double)element.GetValue(OriginalFontSizeProperty);
		double applied = (double)element.GetValue(AppliedFontSizeProperty);
		if (double.IsFinite(applied) && Math.Abs(current - applied) >= 0.001)
		{
			original = current;
			element.SetValue(OriginalFontSizeProperty, original);
		}
		if (!double.IsFinite(original))
		{
			original = current;
			element.SetValue(OriginalFontSizeProperty, original);
		}

		double elementScale = Math.Min(scale, GetMaximumElementScale(element));
		double scaled = Math.Round(original * elementScale, 2, MidpointRounding.AwayFromZero);
		if (Math.Abs(current - scaled) >= 0.001)
		{
			element.SetValue(fontSizeProperty, scaled);
		}
		element.SetValue(AppliedFontSizeProperty, scaled);
	}

	private static bool IsInsideButton(DependencyObject element)
	{
		DependencyObject? current = GetParent(element);
		while (current != null)
		{
			if (current is ButtonBase)
			{
				return true;
			}

			if (current is FrameworkElement frameworkElement && Scopes.TryGetValue(frameworkElement, out _))
			{
				return false;
			}

			current = GetParent(current);
		}

		return false;
	}

	private static bool IsDecorativeGlyph(TextBlock textBlock)
	{
		if (textBlock.FontFamily.Source.Contains("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) ||
			textBlock.FontFamily.Source.Contains("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		string text = textBlock.Text ?? string.Empty;
		return text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';
	}
}
