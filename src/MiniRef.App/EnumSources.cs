using MiniRef.Core.Models;

namespace MiniRef.App;

/// <summary>Enum value arrays for ComboBox ItemsSource bindings. Optional dropdowns get a
/// leading null entry ("(none)"), rendered by EnumDisplayConverter.</summary>
public static class EnumSources
{
    public static SubjectClassification[] Classifications { get; } = Enum.GetValues<SubjectClassification>();

    public static VisualRetentionType?[] VisualRetentionChoices { get; } =
        [null, .. Enum.GetValues<VisualRetentionType>().Cast<VisualRetentionType?>()];

    public static AudioRetentionType?[] AudioRetentionChoices { get; } =
        [null, .. Enum.GetValues<AudioRetentionType>().Cast<AudioRetentionType?>()];

    public static CameraMotion?[] CameraMotionChoices { get; } =
        [null, .. Enum.GetValues<CameraMotion>().Cast<CameraMotion?>()];

    public static CameraAmplitude?[] CameraAmplitudeChoices { get; } =
        [null, .. Enum.GetValues<CameraAmplitude>().Cast<CameraAmplitude?>()];

    public static CameraSpeed?[] CameraSpeedChoices { get; } =
        [null, .. Enum.GetValues<CameraSpeed>().Cast<CameraSpeed?>()];

    public static VisualStyle?[] VisualStyleChoices { get; } =
        [null, .. Enum.GetValues<VisualStyle>().Cast<VisualStyle?>()];

    public static WorkflowAspectRatio[] AspectRatioChoices { get; } = Enum.GetValues<WorkflowAspectRatio>();
}
