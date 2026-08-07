using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniRef.Core.Models;

/// <summary>A single spoken line, rendered as "(S{n}): [Language] Text" when inserted into a shot.</summary>
public partial class DialogueLine : ObservableObject
{
    [ObservableProperty] private Guid speakerSubjectId;
    [ObservableProperty] private string language = "English";
    [ObservableProperty] private string text = "";
}
