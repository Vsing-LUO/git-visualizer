using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Controls;

public partial class DiffBlockComparisonControl : UserControl, IComponentConnector
{
	public static readonly DependencyProperty BlockProperty = DependencyProperty.Register("Block", typeof(DiffChangeBlock), typeof(DiffBlockComparisonControl), new PropertyMetadata(null));

	public static readonly DependencyProperty OldLabelProperty = DependencyProperty.Register("OldLabel", typeof(string), typeof(DiffBlockComparisonControl), new PropertyMetadata("修改前"));

	public static readonly DependencyProperty NewLabelProperty = DependencyProperty.Register("NewLabel", typeof(string), typeof(DiffBlockComparisonControl), new PropertyMetadata("修改后"));

	public DiffChangeBlock? Block
	{
		get
		{
			return (DiffChangeBlock)GetValue(BlockProperty);
		}
		set
		{
			SetValue(BlockProperty, value);
		}
	}

	public string OldLabel
	{
		get
		{
			return (string)GetValue(OldLabelProperty);
		}
		set
		{
			SetValue(OldLabelProperty, value);
		}
	}

	public string NewLabel
	{
		get
		{
			return (string)GetValue(NewLabelProperty);
		}
		set
		{
			SetValue(NewLabelProperty, value);
		}
	}

	public DiffBlockComparisonControl()
	{
		InitializeComponent();
	}
}
