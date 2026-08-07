using System.Globalization;
using System.Windows.Data;

namespace MiniRef.App.Converters;

/// <summary>Converts an ItemsControl.AlternationIndex (0-based) to a 1-based display number,
/// used to render "&lt;Subject N&gt;" / "[Shot N]" badges without the model needing to know its own position.</summary>
public class IndexPlusOneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i + 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
