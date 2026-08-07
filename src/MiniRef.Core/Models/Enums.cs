namespace MiniRef.Core.Models;

public enum SubjectClassification
{
    Person,
    Animal,
    Object,
    SceneOrSetting,
    Clothing,
    Style,
    Action
}

public enum VisualRetentionType
{
    FullyPreserved,
    PartiallyPreserved,
    AttributeTransfer,
    WeakReference
}

public enum AudioRetentionType
{
    FullyCopy,
    PartiallyCopy,
    Reference,
    WeakReference
}

[Flags]
public enum TaskType
{
    None = 0,
    KeyframeCompletion = 1 << 0,
    ReferenceGeneration = 1 << 1,
    VideoEditing = 1 << 2,
    VideoContinuation = 1 << 3,
    AudioReuse = 1 << 4,
    AudioReference = 1 << 5
}

public enum CameraMotion
{
    ZoomIn,
    ZoomOut,
    PushIn,
    PullOut,
    PanLeft,
    PanRight,
    TruckLeft,
    TruckRight,
    TiltUp,
    TiltDown,
    PedestalUp,
    PedestalDown,
    ArcShot,
    TrackingShot,
    StaticShot,
    ShakeSlightly,
    ShakeStrongly,
    Pov,
    RollClockwise,
    RollCounterclockwise
}

public enum CameraAmplitude
{
    Small,
    Large
}

public enum CameraSpeed
{
    Slow,
    Fast
}

public enum VisualStyle
{
    Cinematic,
    LiveAction,
    TwoDAnimated,
    ThreeDCg,
    Claymation,
    Watercolor,
    VintageFilm
}

/// <summary>The exact 8 aspect_ratio choices ComfyUI's core ResolutionSelector node registers
/// (confirmed against ComfyUI's node docs) -- these must match verbatim or the workflow's
/// dropdown widget won't recognize the value.</summary>
public enum WorkflowAspectRatio
{
    Square1x1,
    Portrait2x3,
    Photo3x2,
    PortraitStandard3x4,
    Standard4x3,
    PortraitWidescreen9x16,
    Widescreen16x9,
    Ultrawide21x9
}
