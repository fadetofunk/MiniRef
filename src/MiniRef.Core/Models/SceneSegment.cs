using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>One continuation segment (segment 2, 3, ...) of a <see cref="SceneProject"/> that is
/// generated as a chain of clips, each continuing from the end of the one before it. Holds only
/// what differs per clip -- shots, summary, ambient/music text, duration, task types, and how the
/// previous clip is used. The cast (subjects, pictures, voice references), style, and resolution
/// are shared from the owning project, so reference pictures are set once and reused by every
/// segment. The project's own shots/summary/duration are segment 1 and are not represented here.
/// <see cref="SceneProject.ForSegment"/> turns one of these into a composable SceneProject.</summary>
public partial class SceneSegment : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();

    [ObservableProperty] private ObservableCollection<Shot> shots = [];
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string overallSoundscape = "";
    [ObservableProperty] private string nonDiegeticMusic = "";

    /// <summary>Target length of this clip in seconds, fed to this segment's own duration node.</summary>
    [ObservableProperty] private double durationSeconds = 12.0;

    /// <summary>Task types the author ticked for this segment. "video continuation" -- and "audio
    /// reuse"/"audio reference" when the previous clip's soundtrack is used -- are always added at
    /// compose time (see <see cref="SceneProject.ForSegment"/>), so they needn't be ticked here.</summary>
    [ObservableProperty] private TaskType taskTypes = TaskType.ReferenceGeneration;

    /// <summary>The previous segment's finished clip, as this segment's &lt;Video 1&gt;. It has no
    /// file: in the exported workflow it's fed straight from the previous segment's decoded frames
    /// and audio. Its description, audio use, and retention are editable like any source video.</summary>
    [ObservableProperty] private VideoRef previousVideo = new()
    {
        FromPreviousSegment = true,
        Description = "the source video, which the target video continues from the end of",
        AudioUse = VideoAudioUse.Reference
    };

    private ObservableCollection<VideoRef>? _videoList;

    /// <summary>A one-item list holding <see cref="PreviousVideo"/>, so the UI's source-video list
    /// and insert-tag chips can treat this segment's videos the same way as segment 1's. Not persisted.</summary>
    [JsonIgnore]
    public ObservableCollection<VideoRef> VideoList => _videoList ??= [PreviousVideo];
}
