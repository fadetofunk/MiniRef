using System.IO;
using System.Windows;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

/// <summary>Standalone editor for one Subject's Character Pod content -- same fields as editing a
/// Subject in the main Cast &amp; Setting list (via SubjectFieldsEditor), but for authoring a
/// portable, reusable pod rather than a subject already in a project. Used for both "Create New
/// Character Pod..." (existingPodFilePath is null) and "Edit Existing Pod..." (existingPodFilePath
/// is the pod being edited, so Save overwrites it by default instead of prompting into a new file).</summary>
public partial class CharacterPodEditorWindow : Window
{
    private readonly Subject _subject;
    private readonly string? _existingPodFilePath;
    private readonly string _podsFolder;

    public Subject SavedSubject { get; private set; } = null!;

    public CharacterPodEditorWindow(Subject subject, string? existingPodFilePath, string podsFolder)
    {
        InitializeComponent();
        _subject = subject;
        _existingPodFilePath = existingPodFilePath;
        _podsFolder = podsFolder;
        DataContext = subject;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var promptLabel = _subject.Classification == SubjectClassification.SceneOrSetting
            ? "Name the Setting" : "Name the Character";

        var nameDialog = new NamePodDialog(promptLabel, _subject.Name) { Owner = this };
        if (nameDialog.ShowDialog() != true) return;

        var targetPath = Path.Combine(_podsFolder, CharacterPodStore.SanitizeFileName(nameDialog.PodName) + CharacterPodStore.FileExtension);
        var isSameFile = _existingPodFilePath is not null &&
            string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(_existingPodFilePath), StringComparison.OrdinalIgnoreCase);

        if (!isSameFile && File.Exists(targetPath))
        {
            var overwrite = MessageBox.Show(
                $"A pod named \"{nameDialog.PodName}\" already exists. Overwrite it?",
                "Character Pod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes) return;
        }

        _subject.Name = nameDialog.PodName;

        try
        {
            CharacterPodStore.Save(_subject, targetPath);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Couldn't save that pod:\n{ex.Message}", "Character Pod", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SavedSubject = _subject;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
