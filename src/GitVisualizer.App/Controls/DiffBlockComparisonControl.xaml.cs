using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public partial class DiffBlockComparisonControl : UserControl
{
    private const double CodeViewportWidth = 248;
    private const double EndPadding = 8;
    private const double CodeFontSize = 11;
    private static readonly FontFamily CodeFontFamily = new("Cascadia Mono,Consolas");
    private bool synchronizing;
    private ScrollSide lastDriver = ScrollSide.Old;

    public static readonly DependencyProperty BlockProperty = DependencyProperty.Register(
        nameof(Block),
        typeof(DiffChangeBlock),
        typeof(DiffBlockComparisonControl),
        new PropertyMetadata(null, OnBlockChanged));

    public static readonly DependencyProperty OldLabelProperty = DependencyProperty.Register(
        nameof(OldLabel),
        typeof(string),
        typeof(DiffBlockComparisonControl),
        new PropertyMetadata("修改前"));

    public static readonly DependencyProperty NewLabelProperty = DependencyProperty.Register(
        nameof(NewLabel),
        typeof(string),
        typeof(DiffBlockComparisonControl),
        new PropertyMetadata("修改后"));

    public static readonly DependencyProperty OldTranslationXProperty = DependencyProperty.Register(
        nameof(OldTranslationX),
        typeof(double),
        typeof(DiffBlockComparisonControl),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty NewTranslationXProperty = DependencyProperty.Register(
        nameof(NewTranslationX),
        typeof(double),
        typeof(DiffBlockComparisonControl),
        new PropertyMetadata(0d));

    public DiffBlockComparisonControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateScrollMetrics();
    }

    public DiffChangeBlock? Block
    {
        get => (DiffChangeBlock?)GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public string OldLabel
    {
        get => (string)GetValue(OldLabelProperty);
        set => SetValue(OldLabelProperty, value);
    }

    public string NewLabel
    {
        get => (string)GetValue(NewLabelProperty);
        set => SetValue(NewLabelProperty, value);
    }

    public double OldTranslationX
    {
        get => (double)GetValue(OldTranslationXProperty);
        private set => SetValue(OldTranslationXProperty, value);
    }

    public double NewTranslationX
    {
        get => (double)GetValue(NewTranslationXProperty);
        private set => SetValue(NewTranslationXProperty, value);
    }

    internal static double CalculateFollowerOffset(double driverOffset, double followerMaximum) =>
        Math.Clamp(driverOffset, 0, Math.Max(0, followerMaximum));

    internal static double CalculateMaximumOffset(
        double contentWidth,
        double viewportWidth,
        double endPadding = EndPadding) =>
        Math.Max(0, contentWidth + endPadding - viewportWidth);

    private static void OnBlockChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (DiffBlockComparisonControl)dependencyObject;
        control.ResetOffsets();
        control.Dispatcher.BeginInvoke(
            control.UpdateScrollMetrics,
            DispatcherPriority.Loaded);
    }

    private void UpdateScrollMetrics()
    {
        if (!IsLoaded || Block is null)
        {
            return;
        }

        var oldMaximum = CalculateMaximumOffset(MeasureLongestLine(oldSide: true), CodeViewportWidth);
        var newMaximum = CalculateMaximumOffset(MeasureLongestLine(oldSide: false), CodeViewportWidth);
        synchronizing = true;
        OldHorizontalScrollBar.Maximum = oldMaximum;
        NewHorizontalScrollBar.Maximum = newMaximum;
        OldHorizontalScrollBar.ViewportSize = CodeViewportWidth;
        NewHorizontalScrollBar.ViewportSize = CodeViewportWidth;
        OldHorizontalScrollBar.Visibility = oldMaximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewHorizontalScrollBar.Visibility = newMaximum > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (lastDriver == ScrollSide.Old)
        {
            OldHorizontalScrollBar.Value = Math.Min(OldHorizontalScrollBar.Value, oldMaximum);
            NewHorizontalScrollBar.Value = CalculateFollowerOffset(
                OldHorizontalScrollBar.Value,
                newMaximum);
        }
        else
        {
            NewHorizontalScrollBar.Value = Math.Min(NewHorizontalScrollBar.Value, newMaximum);
            OldHorizontalScrollBar.Value = CalculateFollowerOffset(
                NewHorizontalScrollBar.Value,
                oldMaximum);
        }
        ApplyTranslations();
        synchronizing = false;
    }

    private double MeasureLongestLine(bool oldSide)
    {
        if (Block is null)
        {
            return 0;
        }
        var typeface = new Typeface(
            CodeFontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Block.Rows
            .Select(row => oldSide ? row.OldLine : row.NewLine)
            .Where(line => line is not null && line.Text.Length > 0)
            .Select(line => new FormattedText(
                line!.DisplayText,
                CultureInfo.CurrentUICulture,
                FlowDirection,
                typeface,
                CodeFontSize,
                Brushes.Transparent,
                pixelsPerDip).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0)
            .Max();
    }

    private void OldScrollBar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (synchronizing)
        {
            return;
        }
        lastDriver = ScrollSide.Old;
        SynchronizeFrom(OldHorizontalScrollBar, NewHorizontalScrollBar);
    }

    private void NewScrollBar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (synchronizing)
        {
            return;
        }
        lastDriver = ScrollSide.New;
        SynchronizeFrom(NewHorizontalScrollBar, OldHorizontalScrollBar);
    }

    private void SynchronizeFrom(ScrollBar driver, ScrollBar follower)
    {
        synchronizing = true;
        follower.Value = CalculateFollowerOffset(driver.Value, follower.Maximum);
        ApplyTranslations();
        synchronizing = false;
    }

    private void ApplyTranslations()
    {
        OldTranslationX = -OldHorizontalScrollBar.Value;
        NewTranslationX = -NewHorizontalScrollBar.Value;
    }

    private void ResetOffsets()
    {
        synchronizing = true;
        OldHorizontalScrollBar.Value = 0;
        NewHorizontalScrollBar.Value = 0;
        OldTranslationX = 0;
        NewTranslationX = 0;
        lastDriver = ScrollSide.Old;
        synchronizing = false;
    }

    private void Control_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateScrollMetrics();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateScrollMetrics();
    }

    private void OldPane_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ScrollWithShiftWheel(OldHorizontalScrollBar, e);

    private void NewPane_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ScrollWithShiftWheel(NewHorizontalScrollBar, e);

    private static void ScrollWithShiftWheel(ScrollBar scrollBar, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || scrollBar.Maximum <= 0)
        {
            return;
        }
        var direction = e.Delta > 0 ? -1 : 1;
        scrollBar.Value = Math.Clamp(
            scrollBar.Value + direction * scrollBar.SmallChange * 3,
            scrollBar.Minimum,
            scrollBar.Maximum);
        e.Handled = true;
    }

    private enum ScrollSide
    {
        Old,
        New
    }
}
