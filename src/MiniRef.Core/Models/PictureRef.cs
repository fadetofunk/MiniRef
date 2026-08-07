using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>A `&lt;Picture N&gt;` reference image slot belonging to a Subject.
/// No file is tracked here -- just a short label so the user can tell which
/// picture they mean when they wire the actual image up in ComfyUI/MiniMax later.</summary>
public partial class PictureRef : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();
    [ObservableProperty] private string description = "";

    /// <summary>Local file path chosen for this picture, if any. When a ComfyUI root folder is
    /// configured, exporting copies this file into ComfyUI's input folder automatically; otherwise
    /// it's left to the placeholder-filename behavior.</summary>
    [ObservableProperty] private string? filePath;

    /// <summary>The N in &lt;Picture N&gt;, recomputed live from list order by MainWindow
    /// whenever subjects/pictures change. Not persisted -- derived, not authored.</summary>
    [JsonIgnore]
    [ObservableProperty] private int displayNumber;
}
