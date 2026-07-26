using System.Windows;
using ICSharpCode.AvalonEdit;

namespace GitVisualizer.App.Controls;

public static class AvalonEditBinding
{
    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating", typeof(bool), typeof(AvalonEditBinding), new PropertyMetadata(false));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(AvalonEditBinding),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnTextChanged));

    public static string GetText(DependencyObject element) =>
        (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) =>
        element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TextEditor editor)
        {
            return;
        }

        editor.TextChanged -= EditorOnTextChanged;
        if (!(bool)editor.GetValue(IsUpdatingProperty))
        {
            editor.Text = e.NewValue as string ?? string.Empty;
        }
        editor.TextChanged += EditorOnTextChanged;
    }

    private static void EditorOnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextEditor editor)
        {
            return;
        }

        editor.SetValue(IsUpdatingProperty, true);
        SetText(editor, editor.Text);
        editor.SetValue(IsUpdatingProperty, false);
    }
}
