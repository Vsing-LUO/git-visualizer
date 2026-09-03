using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;

namespace GitVisualizer.App.Dialogs;

public partial class TextPromptWindow : Window, IComponentConnector
{
	public string Value => ValueTextBox.Text.Trim();

	public TextPromptWindow(string title, string prompt, string initialValue = "")
	{
		InitializeComponent();
		base.Title = title;
		PromptText.Text = prompt;
		ValueTextBox.Text = initialValue;
		base.Loaded += delegate
		{
			ValueTextBox.Focus();
			ValueTextBox.SelectAll();
		};
	}

	private void Ok_OnClick(object sender, RoutedEventArgs e)
	{
		if (!string.IsNullOrWhiteSpace(Value))
		{
			base.DialogResult = true;
		}
	}

	private void ValueTextBox_OnKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return && !string.IsNullOrWhiteSpace(Value))
		{
			base.DialogResult = true;
		}
	}
}
