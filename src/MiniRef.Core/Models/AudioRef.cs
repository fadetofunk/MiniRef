using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>The `&lt;Audio N&gt;` voice/audio reference for a Subject.</summary>
public partial class AudioRef : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();
    [ObservableProperty] private string description = "";
    [ObservableProperty] private AudioRetentionType? retention;
    [ObservableProperty] private string retentionNote = "";

    /// <summary>Local file path chosen for this audio reference, if any. When a ComfyUI root folder
    /// is configured, exporting copies this file into ComfyUI's input folder automatically.</summary>
    [ObservableProperty] private string? filePath;

    /// <summary>The N in &lt;Audio N&gt;, recomputed live from list order by MainWindow
    /// whenever subjects/pictures change. Not persisted -- derived, not authored.</summary>
    [JsonIgnore]
    [ObservableProperty] private int displayNumber;
}
