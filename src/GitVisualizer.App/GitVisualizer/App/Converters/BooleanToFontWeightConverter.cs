using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GitVisualizer.App.Converters;

public sealed class BooleanToFontWeightConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return (value is bool && (bool)value) ? FontWeights.SemiBold : FontWeights.Normal;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
