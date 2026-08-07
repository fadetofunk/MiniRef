using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>App-wide preferences, independent of any single scene project.</summary>
public partial class AppSettings : ObservableObject
{
    /// <summary>Root install folder of the user's ComfyUI instance, e.g. "C:\ComfyUI".
    /// Workflows export to "{root}\user\default\workflows" and picture/audio files copy into "{root}\input".</summary>
    [ObservableProperty] private string comfyUiRootFolder = "";

    /// <summary>Per-model-slot filename overrides for the exported workflow, keyed by the slot's
    /// stable Key (see <see cref="Services.ComfyWorkflowExporter.ComfyModelSlot"/>), e.g.
    /// "DiffusionModel" -> "my_other_checkpoint.safetensors". A missing/empty entry means "use
    /// whatever the bundled template ships with".</summary>
    [ObservableProperty] private Dictionary<string, string> modelOverrides = new();

    /// <summary>Last folder each file-browse dialog was used in, remembered per dialog type so
    /// e.g. picking a picture doesn't reset where the video browse dialog opens next time.</summary>
    [ObservableProperty] private string? lastPictureFolder;
    [ObservableProperty] private string? lastAudioFolder;
    [ObservableProperty] private string? lastVideoFolder;
    [ObservableProperty] private string? lastProjectFolder;
}
