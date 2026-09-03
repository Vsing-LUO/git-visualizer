using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GitVisualizer.App.Dialogs;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        ShowCore(null, messageBoxText, "Git 可视化", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        ShowCore(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button) =>
        ShowCore(null, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        ShowCore(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(Window owner, string messageBoxText) =>
        ShowCore(owner, messageBoxText, "Git 可视化", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption) =>
        ShowCore(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button) =>
        ShowCore(owner, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        ShowCore(owner, messageBoxText, caption, button, icon);

    private static MessageBoxResult ShowCore(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(
                () => ShowCore(owner, messageBoxText, caption, button, icon));
        }

        var dialog = new ThemedMessageBoxWindow(
            messageBoxText,
            caption,
            button,
            icon);
        var effectiveOwner = owner ?? Application.Current?.MainWindow;
        if (effectiveOwner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = effectiveOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}

public sealed partial class ThemedMessageBoxWindow : Window
{
    private MessageBoxResult result;

    internal MessageBoxResult Result => result;

    internal ThemedMessageBoxWindow(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(caption) ? "Git 可视化" : caption;
        CaptionText.Text = Title;
        MessageText.Text = message ?? string.Empty;
        result = GetCloseResult(buttons);
        ConfigureIcon(icon);
        ConfigureButtons(buttons, icon);
    }

    internal static MessageBoxResult GetCloseResult(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };

    private void ConfigureButtons(MessageBoxButton buttons, MessageBoxImage icon)
    {
        TertiaryActionButton.Visibility = Visibility.Collapsed;
        SecondaryActionButton.Visibility = Visibility.Collapsed;

        switch (buttons)
        {
            case MessageBoxButton.OK:
                ConfigureButton(PrimaryActionButton, "确定", MessageBoxResult.OK);
                break;
            case MessageBoxButton.OKCancel:
                ConfigureButton(SecondaryActionButton, "取消", MessageBoxResult.Cancel, true);
                ConfigureButton(PrimaryActionButton, "确定", MessageBoxResult.OK);
                break;
            case MessageBoxButton.YesNo:
                ConfigureButton(SecondaryActionButton, "否", MessageBoxResult.No, true);
                ConfigureButton(PrimaryActionButton, "是", MessageBoxResult.Yes);
                break;
            case MessageBoxButton.YesNoCancel:
                ConfigureButton(TertiaryActionButton, "取消", MessageBoxResult.Cancel, true);
                ConfigureButton(SecondaryActionButton, "否", MessageBoxResult.No);
                ConfigureButton(PrimaryActionButton, "是", MessageBoxResult.Yes);
                break;
            default:
                ConfigureButton(PrimaryActionButton, "确定", MessageBoxResult.OK);
                break;
        }

        if ((int)icon == (int)MessageBoxImage.Warning &&
            buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {
            PrimaryActionButton.Style = (Style)FindResource("DangerDialogButton");
        }
    }

    private static void ConfigureButton(
        Button button,
        string label,
        MessageBoxResult buttonResult,
        bool isCancel = false)
    {
        button.Content = label;
        button.Tag = buttonResult;
        button.IsCancel = isCancel;
        button.Visibility = Visibility.Visible;
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var (glyph, foreground, background, border) = (int)icon switch
        {
            (int)MessageBoxImage.Error => (
                "×",
                Color.FromRgb(248, 113, 113),
                Color.FromArgb(38, 239, 68, 68),
                Color.FromArgb(128, 239, 68, 68)),
            (int)MessageBoxImage.Question => (
                "?",
                Color.FromRgb(167, 139, 250),
                Color.FromArgb(38, 139, 92, 246),
                Color.FromArgb(128, 139, 92, 246)),
            (int)MessageBoxImage.Warning => (
                "!",
                Color.FromRgb(251, 191, 36),
                Color.FromArgb(38, 245, 158, 11),
                Color.FromArgb(128, 245, 158, 11)),
            _ => (
                "i",
                Color.FromRgb(96, 165, 250),
                Color.FromArgb(38, 30, 107, 255),
                Color.FromArgb(128, 59, 130, 246))
        };

        MessageIconText.Text = glyph;
        MessageIconText.Foreground = new SolidColorBrush(foreground);
        MessageIconBorder.Background = new SolidColorBrush(background);
        MessageIconBorder.BorderBrush = new SolidColorBrush(border);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PrimaryActionButton.Focus();
    }

    private void ActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessageBoxResult selectedResult })
        {
            result = selectedResult;
            DialogResult = true;
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
