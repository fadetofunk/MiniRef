using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>A project-level `&lt;Video N&gt;` source video reference, used for
/// video editing / video continuation task types.</summary>
public partial class VideoRef : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();
    [ObservableProperty] private string description = "";

    /// <summary>Local file path chosen for this reference video, if any. Used directly as the
    /// VHS_LoadVideoPath widget value on export -- unlike pictures/audio, no copy into ComfyUI's
    /// input folder is needed since that loader takes an absolute path.</summary>
    [ObservableProperty] private string? filePath;

    /// <summary>Per the guide, &lt;Video N&gt; gets its own retention_analysis line using the
    /// same visual markers as &lt;Subject N&gt; (e.g. "weak_reference - cut and pacing structure
    /// only"), separate from any subject that might appear within the video.</summary>
    [ObservableProperty] private VisualRetentionType? retention;
    [ObservableProperty] private string retentionNote = "";
}
