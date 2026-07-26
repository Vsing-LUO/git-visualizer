using System.Windows;
using System.Windows.Input;

namespace GitVisualizer.App.Dialogs;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value => ValueTextBox.Text.Trim();

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Value))
        {
            DialogResult = true;
        }
    }

    private void ValueTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(Value))
        {
            DialogResult = true;
        }
    }
}
