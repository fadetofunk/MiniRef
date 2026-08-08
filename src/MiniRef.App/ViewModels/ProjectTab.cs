using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.ViewModels;

/// <summary>One open project "tab" in the project switcher -- a SceneProject plus the on-disk file
/// it's saved to (if ever) and enough state to know whether it has changes that would be lost if
/// closed without saving.</summary>
public partial class ProjectTab : ObservableObject
{
    /// <summary>Stable for this tab's lifetime; used as its session-cache filename so repeated
    /// autosaves overwrite the same file instead of accumulating new ones.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty] private SceneProject project;
    [ObservableProperty] private string? filePath;

    /// <summary>Transient UI state -- whether this tab's row in the project switcher is currently
    /// showing its rename TextBox instead of the plain name label. Not persisted.</summary>
    [ObservableProperty] private bool isEditingName;

    private string _lastSavedJson;

    public ProjectTab(SceneProject project, string? filePath = null)
    {
        this.project = project;
        this.filePath = filePath;
        _lastSavedJson = Serialize(project);
    }

    /// <summary>True once the project differs from whatever it looked like when this tab was
    /// created, loaded, or last explicitly saved -- i.e. closing it right now would lose something
    /// not already sitting in <see cref="FilePath"/>. A tab restored from the session cache starts
    /// clean relative to that cached state (not relative to FilePath's on-disk content), since the
    /// cache itself is what keeps genuinely-unsaved work from being lost across restarts.</summary>
    public bool IsDirty => Serialize(Project) != _lastSavedJson;

    public void MarkSaved() => _lastSavedJson = Serialize(Project);

    private static string Serialize(SceneProject project) => JsonSerializer.Serialize(project, ProjectStore.Options);
}
