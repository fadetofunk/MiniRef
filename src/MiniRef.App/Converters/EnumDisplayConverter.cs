using System.Globalization;
using System.Windows.Data;
using MiniRef.Core.Models;

namespace MiniRef.App.Converters;

/// <summary>Renders enum values (including our nullable "(none)" dropdown entries) using the
/// same tokens the prompt guide uses, so what you pick in a ComboBox is what ends up in the prompt.</summary>
public class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "(none)",
        SubjectClassification sc => sc.ToDisplay(),
        VisualRetentionType v => v.ToPromptToken(),
        AudioRetentionType a => a.ToPromptToken(),
        TaskType t => t.ToPromptToken(),
        CameraMotion m => m.ToPromptToken(),
        CameraAmplitude am => am.ToPromptToken(),
        CameraSpeed sp => sp.ToPromptToken(),
        VisualStyle vs => vs.ToPromptToken(),
        WorkflowAspectRatio ar => ar.ToPromptToken(),
        _ => value.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
