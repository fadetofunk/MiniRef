using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MiniRef.App.Converters;

/// <summary>Visible when both bound values are the same reference -- used to mark which row in the
/// project switcher's dropdown is the currently active project.</summary>
public class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
