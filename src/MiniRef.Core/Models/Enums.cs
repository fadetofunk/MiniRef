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

/// <summary>How a source video's own synchronized soundtrack -- the reference node's paired
/// ref_video_audios input -- is used in the target video. Reuse: the original audio stays audible
/// (the guide's "audio reuse"). Reference: the new audio only carries on the original track's
/// audible characteristics ("audio reference"), which is the usual fit for continuing a clip.</summary>
public enum VideoAudioUse
{
    None,
    Reuse,
    Reference
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

/// <summary>The base guide's canned phrases for cutting into a shot from the previous one,
/// e.g. "[Shot 2] At 00:05.000, the camera cuts to a close-up of steam rising..." -- meant to
/// be inserted at the very start of a shot's text, continuing the auto-prefixed "At TS," clause.</summary>
public enum ShotTransition
{
    CameraCutsTo,
    ShotCutsTo,
    ShotTransitionsTo,
    ShotChangesTo,
    ShotSwitchesTo
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
