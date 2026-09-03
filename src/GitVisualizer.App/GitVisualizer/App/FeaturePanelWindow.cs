using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GitVisualizer.App;

public partial class FeaturePanelWindow : Window
{
	private const int DwmWindowAttributeCaptionColor = 35;

	private const int DwmWindowAttributeTextColor = 36;

	private const int BlackColorRef = 0;

	private const int WhiteColorRef = 16777215;

	public FeaturePanelWindow()
	{
		InitializeComponent();
		SourceInitialized += FeaturePanelWindow_OnSourceInitialized;
	}

	public UIElement? PanelContent
	{
		get => PanelContentHost.Content as UIElement;
		set => PanelContentHost.Content = value;
	}

	public event EventHandler? EscapeRequested;

	private void FeaturePanelWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape)
		{
			return;
		}

		e.Handled = true;
		EscapeRequested?.Invoke(this, EventArgs.Empty);
	}

	private void FeaturePanelWindow_OnSourceInitialized(object? sender, EventArgs e)
	{
		nint handle = new WindowInteropHelper(this).Handle;
		int captionColor = BlackColorRef;
		int textColor = WhiteColorRef;
		DwmSetWindowAttribute(handle, DwmWindowAttributeCaptionColor, ref captionColor, Marshal.SizeOf<int>());
		DwmSetWindowAttribute(handle, DwmWindowAttributeTextColor, ref textColor, Marshal.SizeOf<int>());
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
