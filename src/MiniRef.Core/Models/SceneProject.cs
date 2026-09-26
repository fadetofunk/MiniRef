using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>The root aggregate for one scene/prompt project, saved as a single JSON file.</summary>
public partial class SceneProject : ObservableObject
{
    [ObservableProperty] private string name = "Untitled Scene";
    [ObservableProperty] private TaskType taskTypes = TaskType.ReferenceGeneration;

    [ObservableProperty] private ObservableCollection<Subject> subjects = [];
    [ObservableProperty] private ObservableCollection<VideoRef> sourceVideos = [];
    [ObservableProperty] private ObservableCollection<Shot> shots = [];

    /// <summary>Free text describing the target video, appended after the auto-generated
    /// "[task type] " prefix in the summary section.</summary>
    [ObservableProperty] private string summary = "";

    [ObservableProperty] private string overallSoundscape = "";
    [ObservableProperty] private string nonDiegeticMusic = "";
    [ObservableProperty] private VisualStyle? visualStyle;

    /// <summary>Target video length in seconds, fed to the workflow's "Duration" node
    /// (converted to a frame count by the workflow's own math-expression node).</summary>
    [ObservableProperty] private double durationSeconds = 5.0;

    /// <summary>Feeds the workflow's Resolution Selector node -- must match one of the exact
    /// strings ComfyUI's core ResolutionSelector node registers, see WorkflowAspectRatio.</summary>
    [ObservableProperty] private WorkflowAspectRatio aspectRatio = WorkflowAspectRatio.Widescreen16x9;

    /// <summary>Target total resolution in megapixels, feeding the same Resolution Selector node.</summary>
    [ObservableProperty] private double megapixels = 0.5;

    /// <summary>Continuation segments 2..N, each generated as its own clip that continues from the
    /// end of the previous one. The project's own shots/summary/duration above are segment 1, so a
    /// project with no continuations behaves exactly as a plain single-clip project always has (and
    /// files saved before segments existed load unchanged). Subjects, pictures, audio references,
    /// visual style, aspect ratio, and megapixels are shared by every segment.</summary>
    [ObservableProperty] private ObservableCollection<SceneSegment> continuations = [];

    /// <summary>Total clips in the chain: this project's own (segment 1) plus every continuation.</summary>
    [JsonIgnore]
    public int SegmentCount => 1 + Continuations.Count;

    /// <summary>A composable, exportable SceneProject for one segment (0-based): the project itself
    /// for 0, otherwise a lightweight view over the shared cast with that segment's own shots,
    /// text, duration, and task types. The view's only source video is the segment's
    /// <see cref="SceneSegment.PreviousVideo"/> (&lt;Video 1&gt;), and "video continuation" -- plus
    /// "audio reuse"/"audio reference" when that clip's soundtrack is used -- is added to its task
    /// types. Subjects, shots, and videos are the same live instances, not copies, so PromptComposer,
    /// ReferenceNumberer, and the exporter all work on a view unchanged.</summary>
    public SceneProject ForSegment(int index)
    {
        if (index == 0) return this;
        if (index < 0 || index > Continuations.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Segment {index + 1} doesn't exist -- this project has {SegmentCount}.");

        var segment = Continuations[index - 1];

        var taskTypes = segment.TaskTypes | TaskType.VideoContinuation;
        taskTypes |= segment.PreviousVideo.AudioUse switch
        {
            VideoAudioUse.Reuse => TaskType.AudioReuse,
            VideoAudioUse.Reference => TaskType.AudioReference,
            _ => TaskType.None
        };

        return new SceneProject
        {
            Name = $"{Name} (segment {index + 1})",
            TaskTypes = taskTypes,
            Subjects = Subjects,
            SourceVideos = segment.VideoList,
            Shots = segment.Shots,
            Summary = segment.Summary,
            OverallSoundscape = segment.OverallSoundscape,
            NonDiegeticMusic = segment.NonDiegeticMusic,
            VisualStyle = VisualStyle,
            DurationSeconds = segment.DurationSeconds,
            AspectRatio = AspectRatio,
            Megapixels = Megapixels
        };
    }
}
