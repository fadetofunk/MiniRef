using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace MiniRef.App.Converters;

/// <summary>Shows just the filename of a chosen reference file, or a placeholder when none is set.</summary>
public class PathToFileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? Path.GetFileName(path) : "(no file chosen)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
