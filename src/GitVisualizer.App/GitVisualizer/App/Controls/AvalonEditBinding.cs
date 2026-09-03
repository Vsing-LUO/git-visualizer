using System;
using System.Windows;
using ICSharpCode.AvalonEdit;

namespace GitVisualizer.App.Controls;

public static class AvalonEditBinding
{
	private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(AvalonEditBinding), new PropertyMetadata(false));

	public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(AvalonEditBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

	public static string GetText(DependencyObject element)
	{
		return (element.GetValue(TextProperty) as string) ?? string.Empty;
	}

	public static void SetText(DependencyObject element, string value)
	{
		element.SetValue(TextProperty, value);
	}

	private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
	{
		if (dependencyObject is TextEditor textEditor)
		{
			textEditor.TextChanged -= EditorOnTextChanged;
			if (!(bool)textEditor.GetValue(IsUpdatingProperty))
			{
				textEditor.Text = (e.NewValue as string) ?? string.Empty;
			}
			textEditor.TextChanged += EditorOnTextChanged;
		}
	}

	private static void EditorOnTextChanged(object? sender, EventArgs e)
	{
		if (sender is TextEditor textEditor)
		{
			textEditor.SetValue(IsUpdatingProperty, true);
			textEditor.SetCurrentValue(TextProperty, textEditor.Text);
			textEditor.GetBindingExpression(TextProperty)?.UpdateSource();
			textEditor.SetValue(IsUpdatingProperty, false);
		}
	}
}
