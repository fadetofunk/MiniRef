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

    /// <summary>Whether this video's own soundtrack is fed to the reference node's paired
    /// ref_video_audios input, and how the prompt describes it. When not None the video also
    /// takes an &lt;Audio N&gt; number (after every subject audio) for that soundtrack.</summary>
    [ObservableProperty] private VideoAudioUse audioUse = VideoAudioUse.None;

    /// <summary>True for a continuation segment's &lt;Video 1&gt;: the previous segment's output,
    /// fed in-graph (decoded frames + audio) rather than loaded from <see cref="FilePath"/>.</summary>
    [ObservableProperty] private bool fromPreviousSegment;

    /// <summary>For a <see cref="FromPreviousSegment"/> video only: feed the reference node just this
    /// many seconds from the END of the previous clip (0 = all of it). The ending motion and audio tail
    /// are what a continuation needs, and every reference frame is extra tokens on every sampling step,
    /// so a short tail is far cheaper than the whole clip. Snapped to a valid clip length (17k + 5
    /// frames) on export, and never longer than the new clip itself. Defaults to 3 -- also what a saved
    /// segment from before this setting existed loads as, rather than silently meaning "the whole clip".
    /// Ignored for ordinary file videos.</summary>
    [ObservableProperty] private double useLastSeconds = 3.0;
}
