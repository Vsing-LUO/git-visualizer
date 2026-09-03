using System.Windows;
using System.Windows.Markup;
using GitVisualizer.App.Services;

namespace GitVisualizer.App.Dialogs;

public partial class EditorSafetyWindow : Window, IComponentConnector
{
	private readonly EditorSafetyAction primaryAction;
	private readonly EditorSafetyAction secondaryAction;

	public EditorSafetyAction Action { get; private set; } = EditorSafetyAction.Cancel;

	public EditorSafetyWindow(
		string title,
		string message,
		string primaryText,
		EditorSafetyAction primaryAction,
		string secondaryText,
		EditorSafetyAction secondaryAction)
	{
		InitializeComponent();
		Title = title;
		TitleText.Text = title;
		MessageText.Text = message;
		PrimaryButton.Content = primaryText;
		SecondaryButton.Content = secondaryText;
		this.primaryAction = primaryAction;
		this.secondaryAction = secondaryAction;
	}

	private void Primary_OnClick(object sender, RoutedEventArgs e)
	{
		Action = primaryAction;
		DialogResult = true;
	}

	private void Secondary_OnClick(object sender, RoutedEventArgs e)
	{
		Action = secondaryAction;
		DialogResult = true;
	}
}
