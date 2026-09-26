namespace MiniRef.Core.Models;

/// <summary>Maps enum values to the literal tokens/phrases used by the MiniMax H3 prompt guides.</summary>
public static class EnumFormatting
{
    public static string ToDisplay(this SubjectClassification value) => value switch
    {
        SubjectClassification.Person => "Person",
        SubjectClassification.Animal => "Animal",
        SubjectClassification.Object => "Object / Prop",
        SubjectClassification.SceneOrSetting => "Scene / Setting",
        SubjectClassification.Clothing => "Clothing",
        SubjectClassification.Style => "Style",
        SubjectClassification.Action => "Action",
        _ => value.ToString()
    };

    public static string ToDisplay(this PreviousClipHandoff value) => value switch
    {
        PreviousClipHandoff.PinEnding => "Pin its ending onto the start of this clip",
        PreviousClipHandoff.ReferenceVideo => "Use it as a reference video",
        _ => value.ToString()
    };

    public static string ToDisplay(this VideoAudioUse value) => value switch
    {
        VideoAudioUse.None => "Don't use its audio",
        VideoAudioUse.Reuse => "Reuse its audio",
        VideoAudioUse.Reference => "Continue its audio (reference)",
        _ => value.ToString()
    };

    public static string ToPromptToken(this VisualRetentionType value) => value switch
    {
        VisualRetentionType.FullyPreserved => "fully_preserved",
        VisualRetentionType.PartiallyPreserved => "partially_preserved",
        VisualRetentionType.AttributeTransfer => "attribute_transfer",
        VisualRetentionType.WeakReference => "weak_reference",
        _ => value.ToString()
    };

    public static string ToPromptToken(this AudioRetentionType value) => value switch
    {
        AudioRetentionType.FullyCopy => "fully_copy",
        AudioRetentionType.PartiallyCopy => "partially_copy",
        AudioRetentionType.Reference => "reference",
        AudioRetentionType.WeakReference => "weak_reference",
        _ => value.ToString()
    };

    public static string ToPromptToken(this TaskType value) => value switch
    {
        TaskType.KeyframeCompletion => "keyframe completion",
        TaskType.ReferenceGeneration => "reference generation",
        TaskType.VideoEditing => "video editing",
        TaskType.VideoContinuation => "video continuation",
        TaskType.AudioReuse => "audio reuse",
        TaskType.AudioReference => "audio reference",
        _ => value.ToString()
    };

    /// <summary>Splits a [Flags] TaskType into its individual set bits, in declaration order.</summary>
    public static IEnumerable<TaskType> Split(this TaskType value)
    {
        foreach (TaskType flag in Enum.GetValues<TaskType>())
        {
            if (flag != TaskType.None && value.HasFlag(flag))
                yield return flag;
        }
    }

    public static string ToPromptToken(this CameraMotion value) => value switch
    {
        CameraMotion.ZoomIn => "Zoom In",
        CameraMotion.ZoomOut => "Zoom Out",
        CameraMotion.PushIn => "Push In",
        CameraMotion.PullOut => "Pull Out",
        CameraMotion.PanLeft => "Pan Left",
        CameraMotion.PanRight => "Pan Right",
        CameraMotion.TruckLeft => "Truck Left",
        CameraMotion.TruckRight => "Truck Right",
        CameraMotion.TiltUp => "Tilt Up",
        CameraMotion.TiltDown => "Tilt Down",
        CameraMotion.PedestalUp => "Pedestal Up",
        CameraMotion.PedestalDown => "Pedestal Down",
        CameraMotion.ArcShot => "Arc Shot",
        CameraMotion.TrackingShot => "Tracking Shot",
        CameraMotion.StaticShot => "Static Shot",
        CameraMotion.ShakeSlightly => "Shake Slightly",
        CameraMotion.ShakeStrongly => "Shake Strongly",
        CameraMotion.Pov => "POV",
        CameraMotion.RollClockwise => "Roll Clockwise",
        CameraMotion.RollCounterclockwise => "Roll Counterclockwise",
        _ => value.ToString()
    };

    public static string ToPromptToken(this CameraAmplitude value) => value switch
    {
        CameraAmplitude.Small => "small",
        CameraAmplitude.Large => "large",
        _ => value.ToString()
    };

    public static string ToPromptToken(this CameraSpeed value) => value switch
    {
        CameraSpeed.Slow => "slow",
        CameraSpeed.Fast => "fast",
        _ => value.ToString()
    };

    /// <summary>The literal phrase text, lowercase and with no trailing punctuation since it's
    /// meant to continue the sentence the composer already opened with "At {timestamp}, ".</summary>
    public static string ToPromptToken(this ShotTransition value) => value switch
    {
        ShotTransition.CameraCutsTo => "the camera cuts to",
        ShotTransition.ShotCutsTo => "the shot cuts to",
        ShotTransition.ShotTransitionsTo => "the shot transitions to",
        ShotTransition.ShotChangesTo => "the shot changes to",
        ShotTransition.ShotSwitchesTo => "the shot switches to",
        _ => value.ToString()
    };

    public static string ToPromptToken(this VisualStyle value) => value switch
    {
        VisualStyle.Cinematic => "Cinematic",
        VisualStyle.LiveAction => "live-action",
        VisualStyle.TwoDAnimated => "2D-animated",
        VisualStyle.ThreeDCg => "3D CG",
        VisualStyle.Claymation => "claymation",
        VisualStyle.Watercolor => "watercolor",
        VisualStyle.VintageFilm => "vintage film",
        _ => value.ToString()
    };

    public static string ToPromptToken(this WorkflowAspectRatio value) => value switch
    {
        WorkflowAspectRatio.Square1x1 => "1:1 (Square)",
        WorkflowAspectRatio.Portrait2x3 => "2:3 (Portrait Photo)",
        WorkflowAspectRatio.Photo3x2 => "3:2 (Photo)",
        WorkflowAspectRatio.PortraitStandard3x4 => "3:4 (Portrait Standard)",
        WorkflowAspectRatio.Standard4x3 => "4:3 (Standard)",
        WorkflowAspectRatio.PortraitWidescreen9x16 => "9:16 (Portrait Widescreen)",
        WorkflowAspectRatio.Widescreen16x9 => "16:9 (Widescreen)",
        WorkflowAspectRatio.Ultrawide21x9 => "21:9 (Ultrawide)",
        _ => value.ToString()
    };

    // The Try* methods below invert their ToPromptToken() counterpart above, for
    // ComfyWorkflowImporter reconstructing a project from an exported workflow's literal text.
    // Each does a simple linear scan over the enum's own values rather than a static reverse
    // dictionary, since these enums are small and it keeps every token pair defined in exactly
    // one place (the switch above) instead of two that could drift apart.

    public static bool TryParseVisualRetentionToken(string token, out VisualRetentionType value) =>
        TryParseToken(Enum.GetValues<VisualRetentionType>(), v => v.ToPromptToken(), token, out value);

    public static bool TryParseAudioRetentionToken(string token, out AudioRetentionType value) =>
        TryParseToken(Enum.GetValues<AudioRetentionType>(), v => v.ToPromptToken(), token, out value);

    public static bool TryParseTaskTypeToken(string token, out TaskType value) =>
        TryParseToken(Enum.GetValues<TaskType>().Where(t => t != TaskType.None), v => v.ToPromptToken(), token, out value);

    public static bool TryParseVisualStyleToken(string token, out VisualStyle value) =>
        TryParseToken(Enum.GetValues<VisualStyle>(), v => v.ToPromptToken(), token, out value);

    public static bool TryParseAspectRatioToken(string token, out WorkflowAspectRatio value) =>
        TryParseToken(Enum.GetValues<WorkflowAspectRatio>(), v => v.ToPromptToken(), token, out value);

    private static bool TryParseToken<T>(IEnumerable<T> values, Func<T, string> toToken, string token, out T value) where T : struct
    {
        foreach (var candidate in values)
        {
            if (!string.Equals(toToken(candidate), token, StringComparison.OrdinalIgnoreCase)) continue;
            value = candidate;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Renders a camera motion phrase the way the base guide's examples do,
    /// e.g. "The camera pushes in with small amplitude at slow speed."</summary>
    public static string ToMotionSentence(CameraMotion motion, CameraAmplitude? amplitude, CameraSpeed? speed)
    {
        var verb = motion.ToPromptToken();
        var qualifiers = new List<string>();
        if (amplitude is { } a) qualifiers.Add($"with {a.ToPromptToken()} amplitude");
        if (speed is { } s) qualifiers.Add($"at {s.ToPromptToken()} speed");
        var suffix = qualifiers.Count > 0 ? " " + string.Join(" ", qualifiers) : "";
        return $"The camera performs a {verb}{suffix}.";
    }
}
