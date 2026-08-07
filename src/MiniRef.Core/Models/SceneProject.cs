using System.Collections.ObjectModel;
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
}
