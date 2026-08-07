using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>One `[Shot N]` in the detailed_description timeline.
/// Camera motion, dialogue, and reference tags are all authored as plain text inside
/// <see cref="Text"/> via insert-at-cursor helper buttons in the UI -- there's a single
/// free-form body per shot rather than separate structured fields for each concern.</summary>
public partial class Shot : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();

    /// <summary>Optional timestamp, e.g. "00:03.500". When set, the composer auto-prefixes
    /// the shot with "At {Timestamp}, " so it doesn't need to be typed by hand.</summary>
    [ObservableProperty] private string timestamp = "";

    /// <summary>Free-form narrative text, including any inserted &lt;Subject N&gt; /
    /// &lt;Picture N&gt; / &lt;Audio N&gt; tags, camera-motion sentences, and dialogue lines.</summary>
    [ObservableProperty] private string text = "";

    /// <summary>Tracks which subjects speak in this shot (for appears-in-shots bookkeeping
    /// and for the dialogue-insert helper); the actual "(Sn): [Language] text" string is
    /// inserted directly into <see cref="Text"/> when a line is added.</summary>
    [ObservableProperty] private ObservableCollection<DialogueLine> dialogue = [];

    /// <summary>Transient UI state (last known caret position in the shot's text box),
    /// used by the insert-at-cursor chip buttons. Not persisted.</summary>
    [JsonIgnore]
    [ObservableProperty] private int caretIndex;
}
