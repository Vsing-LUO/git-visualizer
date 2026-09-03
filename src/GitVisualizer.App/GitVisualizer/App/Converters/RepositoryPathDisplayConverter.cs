using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace GitVisualizer.App.Converters;

public sealed class RepositoryPathDisplayConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not string path || string.IsNullOrWhiteSpace(path))
		{
			return string.Empty;
		}

		try
		{
			string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string folderName = Path.GetFileName(normalized);
			return string.IsNullOrWhiteSpace(folderName) ? path : folderName;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return path;
		}
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		Binding.DoNothing;
}
