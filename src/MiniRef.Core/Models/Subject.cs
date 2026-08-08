using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>A `&lt;Subject N&gt;` -- a character, animal, prop, setting, clothing item,
/// style, or action that can be referenced repeatedly across the scene.</summary>
public partial class Subject : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();
    [ObservableProperty] private SubjectClassification classification = SubjectClassification.Person;

    /// <summary>Short label used only in the UI, e.g. "Sarah Connor".</summary>
    [ObservableProperty] private string name = "";

    /// <summary>Appearance / identity text that becomes the subject's sentence in subject_definitions.</summary>
    [ObservableProperty] private string description = "";

    [ObservableProperty] private ObservableCollection<PictureRef> pictures = [];
    [ObservableProperty] private AudioRef? audio;

    [ObservableProperty] private VisualRetentionType? retention;
    [ObservableProperty] private string retentionNote = "";

    /// <summary>Transient UI state -- whether the card is expanded in the Cast &amp; Setting
    /// list. Not persisted.</summary>
    [JsonIgnore]
    [ObservableProperty] private bool isExpanded = true;
}
