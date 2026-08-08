using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MiniRef.App.Views;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string ComfyTemplateRelativePath = "Assets/video_minimax_h3_r2v.template.json";

    // Windows' common file dialog persists its own "last visited folder" per ClientGuid, and that
    // persisted folder wins over InitialDirectory after the first use. Without a distinct ClientGuid
    // per dialog purpose, every OpenFileDialog/SaveFileDialog in the app shares one OS-level "last
    // folder" bucket, which made the separate LastPictureFolder/LastAudioFolder/etc. settings below
    // appear to bleed into each other.
    private static readonly Guid PictureDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a01");
    private static readonly Guid AudioDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a02");
    private static readonly Guid VideoDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a03");
    private static readonly Guid ProjectDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a04");
    private static readonly Guid ComfyExportDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a05");
    private static readonly Guid ComfyImportDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a06");

    [ObservableProperty] private ObservableCollection<ProjectTab> openProjects = [];
    [ObservableProperty] private ProjectTab? activeTab;
    [ObservableProperty] private AppSettings settings = SettingsStore.Load();

    private readonly SceneProject _fallbackProject = new();
    private readonly DispatcherTimer _autosaveTimer;

    public MainViewModel()
    {
        LoadSessionOrDefault();

        // Backstops SaveSession's other call sites (tab add/remove/rename, Save, app exit) in case
        // the process ends without a clean shutdown -- e.g. a crash, or Windows forcing the app
        // closed -- so a long editing session without any of those events still isn't a total loss.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => SaveSession();
        _autosaveTimer.Start();
    }

    /// <summary>The active tab's project. Kept as a passthrough property -- rather than rewriting
    /// every "Project.X" binding and usage throughout the app to "ActiveTab.Project.X" -- so
    /// switching to multiple open projects didn't require reshaping the rest of the app. Falls back
    /// to a private throwaway instance if there's ever no active tab, which shouldn't happen once
    /// the constructor finishes (OpenProjects always has at least one tab).</summary>
    public SceneProject Project => ActiveTab?.Project ?? _fallbackProject;

    public string? CurrentFilePath => ActiveTab?.FilePath;

    public bool TaskKeyframeCompletion
    {
        get => Project.TaskTypes.HasFlag(TaskType.KeyframeCompletion);
        set => SetTaskFlag(TaskType.KeyframeCompletion, value);
    }

    public bool TaskReferenceGeneration
    {
        get => Project.TaskTypes.HasFlag(TaskType.ReferenceGeneration);
        set => SetTaskFlag(TaskType.ReferenceGeneration, value);
    }

    public bool TaskVideoEditing
    {
        get => Project.TaskTypes.HasFlag(TaskType.VideoEditing);
        set => SetTaskFlag(TaskType.VideoEditing, value);
    }

    public bool TaskVideoContinuation
    {
        get => Project.TaskTypes.HasFlag(TaskType.VideoContinuation);
        set => SetTaskFlag(TaskType.VideoContinuation, value);
    }

    public bool TaskAudioReuse
    {
        get => Project.TaskTypes.HasFlag(TaskType.AudioReuse);
        set => SetTaskFlag(TaskType.AudioReuse, value);
    }

    public bool TaskAudioReference
    {
        get => Project.TaskTypes.HasFlag(TaskType.AudioReference);
        set => SetTaskFlag(TaskType.AudioReference, value);
    }

    private void SetTaskFlag(TaskType flag, bool value)
    {
        Project.TaskTypes = value ? Project.TaskTypes | flag : Project.TaskTypes & ~flag;
        OnPropertyChanged(nameof(TaskKeyframeCompletion));
        OnPropertyChanged(nameof(TaskReferenceGeneration));
        OnPropertyChanged(nameof(TaskVideoEditing));
        OnPropertyChanged(nameof(TaskVideoContinuation));
        OnPropertyChanged(nameof(TaskAudioReuse));
        OnPropertyChanged(nameof(TaskAudioReference));
    }

    partial void OnActiveTabChanged(ProjectTab? value)
    {
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(TaskKeyframeCompletion));
        OnPropertyChanged(nameof(TaskReferenceGeneration));
        OnPropertyChanged(nameof(TaskVideoEditing));
        OnPropertyChanged(nameof(TaskVideoContinuation));
        OnPropertyChanged(nameof(TaskAudioReuse));
        OnPropertyChanged(nameof(TaskAudioReference));
    }

    [RelayCommand]
    private void AddSubject() => Project.Subjects.Add(new Subject());

    [RelayCommand]
    private void RemoveSubject(Subject subject) => Project.Subjects.Remove(subject);

    [RelayCommand]
    private void MoveSubjectUp(Subject subject) => Move(Project.Subjects, subject, -1);

    [RelayCommand]
    private void MoveSubjectDown(Subject subject) => Move(Project.Subjects, subject, 1);

    [RelayCommand]
    private void AddPicture(Subject subject) => subject.Pictures.Add(new PictureRef());

    [RelayCommand]
    private void RemovePicture(PictureRef picture)
    {
        foreach (var subject in Project.Subjects)
            subject.Pictures.Remove(picture);
    }

    [RelayCommand]
    private void ToggleAudio(Subject subject) => subject.Audio = subject.Audio is null ? new AudioRef() : null;

    [RelayCommand]
    private void BrowsePictureFile(PictureRef picture)
    {
        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*", ClientGuid = PictureDialogGuid };
        if (Directory.Exists(Settings.LastPictureFolder)) dialog.InitialDirectory = Settings.LastPictureFolder;
        if (dialog.ShowDialog() != true) return;

        picture.FilePath = dialog.FileName;
        RememberFolder(f => Settings.LastPictureFolder = f, dialog.FileName);
    }

    [RelayCommand]
    private void BrowseAudioFile(AudioRef audio)
    {
        var dialog = new OpenFileDialog { Filter = "Audio files|*.mp3;*.wav;*.flac;*.ogg;*.m4a|All files|*.*", ClientGuid = AudioDialogGuid };
        if (Directory.Exists(Settings.LastAudioFolder)) dialog.InitialDirectory = Settings.LastAudioFolder;
        if (dialog.ShowDialog() != true) return;

        audio.FilePath = dialog.FileName;
        RememberFolder(f => Settings.LastAudioFolder = f, dialog.FileName);
    }

    [RelayCommand]
    private void BrowseVideoFile(VideoRef video)
    {
        var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All files|*.*", ClientGuid = VideoDialogGuid };
        if (Directory.Exists(Settings.LastVideoFolder)) dialog.InitialDirectory = Settings.LastVideoFolder;
        if (dialog.ShowDialog() != true) return;

        video.FilePath = dialog.FileName;
        RememberFolder(f => Settings.LastVideoFolder = f, dialog.FileName);
    }

    [RelayCommand]
    private void OpenSettings() => ShowSettingsWindow(isFirstRun: false);

    /// <summary>Called once from MainWindow's Loaded handler when no ComfyUI root is configured
    /// yet, so a new user is guided straight to setting it up (with folder validation) instead
    /// of silently hitting broken exports later.</summary>
    public void ShowSettingsWindowForFirstRun() => ShowSettingsWindow(isFirstRun: true);

    private void ShowSettingsWindow(bool isFirstRun)
    {
        var window = new SettingsWindow(Settings, TryLoadTemplate(), isFirstRun) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private void AddShot() => Project.Shots.Add(new Shot());

    [RelayCommand]
    private void RemoveShot(Shot shot) => Project.Shots.Remove(shot);

    [RelayCommand]
    private void MoveShotUp(Shot shot) => Move(Project.Shots, shot, -1);

    [RelayCommand]
    private void MoveShotDown(Shot shot) => Move(Project.Shots, shot, 1);

    [RelayCommand]
    private void AddSourceVideo() => Project.SourceVideos.Add(new VideoRef());

    [RelayCommand]
    private void RemoveSourceVideo(VideoRef video) => Project.SourceVideos.Remove(video);

    [RelayCommand]
    private void NewProject()
    {
        var dialog = new NewProjectDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var tab = new ProjectTab(new SceneProject { Name = dialog.ProjectName });
        OpenProjects.Add(tab);
        ActiveTab = tab;
        SaveSession();
    }

    [RelayCommand]
    private void OpenProject()
    {
        var dialog = new OpenFileDialog { Filter = "MiniRef project (*.mmref.json)|*.mmref.json|All files (*.*)|*.*", ClientGuid = ProjectDialogGuid };
        if (Directory.Exists(Settings.LastProjectFolder)) dialog.InitialDirectory = Settings.LastProjectFolder;
        if (dialog.ShowDialog() != true) return;

        OpenProjectFile(dialog.FileName);
        RememberFolder(f => Settings.LastProjectFolder = f, dialog.FileName);
    }

    /// <summary>Switches to the file's tab instead of opening a duplicate if it's already open.</summary>
    private void OpenProjectFile(string filePath)
    {
        var existing = OpenProjects.FirstOrDefault(t =>
            t.FilePath is not null && string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new ProjectTab(ProjectStore.Load(filePath), filePath);
        OpenProjects.Add(tab);
        ActiveTab = tab;
        SaveSession();
    }

    [RelayCommand]
    private void SaveProject() => SaveTab(ActiveTab);

    /// <returns>False if there was no tab to save, or the user cancelled a Save As dialog that a
    /// never-saved-before tab needed -- callers use this to decide whether to proceed with whatever
    /// prompted the save (e.g. closing a tab that turned out to still have unsaved changes).</returns>
    private bool SaveTab(ProjectTab? tab)
    {
        if (tab is null) return false;
        if (tab.FilePath is null) return SaveTabAs(tab);

        ProjectStore.Save(tab.Project, tab.FilePath);
        tab.MarkSaved();
        SaveSession();
        return true;
    }

    [RelayCommand]
    private void SaveProjectAs() => SaveTabAs(ActiveTab);

    private bool SaveTabAs(ProjectTab? tab)
    {
        if (tab is null) return false;

        var dialog = new SaveFileDialog
        {
            Filter = "MiniRef project (*.mmref.json)|*.mmref.json|All files (*.*)|*.*",
            FileName = tab.Project.Name + ProjectStore.FileExtension,
            ClientGuid = ProjectDialogGuid
        };
        if (Directory.Exists(Settings.LastProjectFolder)) dialog.InitialDirectory = Settings.LastProjectFolder;
        if (dialog.ShowDialog() != true) return false;

        ProjectStore.Save(tab.Project, dialog.FileName);
        tab.FilePath = dialog.FileName;
        tab.MarkSaved();
        RememberFolder(f => Settings.LastProjectFolder = f, dialog.FileName);
        SaveSession();
        return true;
    }

    /// <summary>Closes one project tab from the project switcher, prompting to save first if it has
    /// unsaved changes. Always leaves at least one tab open -- closing the last one opens a fresh
    /// blank project rather than leaving the app with nothing to show.</summary>
    [RelayCommand]
    private void CloseProject(ProjectTab tab)
    {
        if (!ConfirmDiscard(tab)) return;

        var index = OpenProjects.IndexOf(tab);
        OpenProjects.Remove(tab);

        if (OpenProjects.Count == 0)
            OpenProjects.Add(new ProjectTab(new SceneProject()));

        if (ActiveTab == tab)
            ActiveTab = OpenProjects[Math.Clamp(index, 0, OpenProjects.Count - 1)];

        SaveSession();
    }

    /// <returns>False if the caller should abort whatever it was about to do -- the user hit
    /// Cancel, or chose to save first but a required Save As dialog got cancelled.</returns>
    private bool ConfirmDiscard(ProjectTab tab)
    {
        if (!tab.IsDirty) return true;

        var result = MessageBox.Show(
            $"Save changes to \"{tab.Project.Name}\" before closing?",
            "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => SaveTab(tab),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    /// <summary>Called from MainWindow's Closing handler -- checks every open tab, not just the
    /// active one, since closing the whole app risks losing changes sitting in background tabs too.</summary>
    public bool ConfirmExit()
    {
        foreach (var tab in OpenProjects)
        {
            if (!ConfirmDiscard(tab)) return false;
        }

        SaveSession();
        return true;
    }

    [RelayCommand]
    private void ExportComfyWorkflow()
    {
        if (Settings.SaveOnExport) SaveProject();

        var templateJson = TryLoadTemplate();
        if (templateJson is null)
        {
            MessageBox.Show(
                $"Couldn't find the bundled ComfyUI template at:\n{Path.Combine(AppContext.BaseDirectory, ComfyTemplateRelativePath)}",
                "Export ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var root = Settings.ComfyUiRootFolder;
        var hasRoot = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        var workflowJson = ComfyWorkflowExporter.Export(
            templateJson, Project,
            resolvePictureFilename: (_, picture) => ResolveComfyInputFilename(picture.FilePath, hasRoot ? root : null),
            resolveAudioFilename: (_, audio) => ResolveComfyInputFilename(audio.FilePath, hasRoot ? root : null),
            resolveVideoPath: video => string.IsNullOrWhiteSpace(video.FilePath) || !File.Exists(video.FilePath)
                ? null
                : video.FilePath,
            modelOverrides: Settings.ModelOverrides);

        string outputPath;
        if (hasRoot)
        {
            var workflowsFolder = Path.Combine(root, "user", "default", "workflows");
            Directory.CreateDirectory(workflowsFolder);
            outputPath = GetNextAvailablePath(workflowsFolder, SanitizeFileName(Project.Name), ".json");
        }
        else
        {
            var dialog = new SaveFileDialog
            {
                Filter = "ComfyUI workflow (*.json)|*.json|All files (*.*)|*.*",
                FileName = Project.Name + "_comfy_workflow.json",
                ClientGuid = ComfyExportDialogGuid
            };
            if (dialog.ShowDialog() != true) return;
            outputPath = dialog.FileName;
        }

        File.WriteAllText(outputPath, workflowJson);

        MessageBox.Show(
            $"Workflow exported to:\n{outputPath}\n\n" +
            "LoadImage/LoadAudio/VHS_LoadVideoPath nodes are titled with their <Picture N>/<Audio N>/<Video N> " +
            "tag and character name. Pictures/audio with a file chosen were copied into ComfyUI's input folder " +
            "automatically; anything without a file picked still shows a placeholder to fill in by hand.\n\n" +
            "Video nodes use VHS_LoadVideoPath (ComfyUI-VideoHelperSuite) -- make sure that custom node pack " +
            "is installed, or those nodes won't resolve.",
            "Export ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Reconstructs a project from a ComfyUI workflow this tool previously exported --
    /// best-effort, since it's reading the composed prompt text and node titles back rather than
    /// any dedicated round-trip format. Always opens as a new tab; never replaces what's open.</summary>
    [RelayCommand]
    private void ImportComfyWorkflow()
    {
        var root = Settings.ComfyUiRootFolder;
        var hasRoot = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        var dialog = new OpenFileDialog
        {
            Filter = "ComfyUI workflow (*.json)|*.json|All files (*.*)|*.*",
            ClientGuid = ComfyImportDialogGuid
        };
        var workflowsFolder = hasRoot ? Path.Combine(root, "user", "default", "workflows") : null;
        if (workflowsFolder is not null && Directory.Exists(workflowsFolder))
            dialog.InitialDirectory = workflowsFolder;
        else if (hasRoot)
            dialog.InitialDirectory = root;

        if (dialog.ShowDialog() != true) return;

        string workflowJson;
        try
        {
            workflowJson = File.ReadAllText(dialog.FileName);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Couldn't read that file:\n{ex.Message}", "Import ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SceneProject imported;
        try
        {
            imported = ComfyWorkflowImporter.Import(workflowJson, hasRoot ? Path.Combine(root, "input") : null);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(
                $"Couldn't import that workflow:\n{ex.Message}\n\nImport only works on a workflow this tool itself exported.",
                "Import ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        imported.Name = GuessProjectName(dialog.FileName);
        var tab = new ProjectTab(imported);
        OpenProjects.Add(tab);
        ActiveTab = tab;
        SaveSession();

        MessageBox.Show(
            "Workflow imported as a new project. This is a best-effort reconstruction from the exported prompt text " +
            "and node titles, not a dedicated round-trip format -- a few UI-only details (subject classification, and " +
            "task type checkboxes if the summary was left blank when it was exported) don't carry over and are left " +
            "at their defaults. Double-check the result before continuing.",
            "Import ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Undoes the filename shape Export leaves behind (SanitizeFileName'd project name,
    /// optionally with a "_comfy_workflow" or numeric collision suffix) to recover a reasonable
    /// starting project name -- project.Name itself never makes it into the exported workflow.</summary>
    private static string GuessProjectName(string workflowFilePath)
    {
        var name = Path.GetFileNameWithoutExtension(workflowFilePath);

        const string exportSuffix = "_comfy_workflow";
        if (name.EndsWith(exportSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^exportSuffix.Length];

        return string.IsNullOrWhiteSpace(name) ? "Untitled Scene" : name;
    }

    private static string? TryLoadTemplate()
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, ComfyTemplateRelativePath);
        return File.Exists(templatePath) ? File.ReadAllText(templatePath) : null;
    }

    /// <summary>Records the folder a file-browse dialog was just used in and persists it right
    /// away, so the remembered location survives even if the user never opens Settings.</summary>
    private void RememberFolder(Action<string> assign, string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return;

        assign(folder);
        SettingsStore.Save(Settings);
    }

    /// <summary>Returns the bare filename ComfyUI's LoadImage/LoadAudio widgets expect. If a ComfyUI
    /// root is configured, the file is copied into "{root}\input" (skipped if it's already there);
    /// otherwise falls back to just the original filename, best-effort, for the user to place by hand.
    ///
    /// ComfyUI's input folder is shared across every workflow on the machine, and source pictures
    /// often carry generic camera/download names (IMG_1234.jpg) that collide across unrelated
    /// projects -- a later export can silently overwrite an earlier one's file out from under it,
    /// which is exactly the failure this was hitting. Copies are prefixed with
    /// "miniref-{workflow-name}-" so each project's files stay distinct in that shared folder.</summary>
    private string? ResolveComfyInputFilename(string? filePath, string? comfyRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        if (string.IsNullOrWhiteSpace(comfyRoot)) return Path.GetFileName(filePath);

        var inputFolder = Path.Combine(comfyRoot, "input");
        Directory.CreateDirectory(inputFolder);

        var fullSource = Path.GetFullPath(filePath);
        var fullInput = Path.GetFullPath(inputFolder);
        if (fullSource.StartsWith(fullInput, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(fullInput, fullSource);

        var targetName = BuildComfyInputFileName(filePath);
        File.Copy(fullSource, Path.Combine(inputFolder, targetName), overwrite: true);
        return targetName;
    }

    private string BuildComfyInputFileName(string sourceFilePath)
    {
        var workflowSlug = SanitizeFileName(Project.Name).Replace(' ', '-');
        return $"miniref-{workflowSlug}-{Path.GetFileName(sourceFilePath)}";
    }

    private static string GetNextAvailablePath(string folder, string baseName, string extension)
    {
        var candidate = Path.Combine(folder, baseName + extension);
        if (!File.Exists(candidate)) return candidate;

        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(folder, $"{baseName}_{n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Untitled Scene" : cleaned;
    }

    private static void Move<T>(ObservableCollection<T> list, T item, int offset)
    {
        var index = list.IndexOf(item);
        var newIndex = index + offset;
        if (index < 0 || newIndex < 0 || newIndex >= list.Count) return;
        list.Move(index, newIndex);
    }

    /// <summary>Restores every project that was open last session (each from its own cached
    /// snapshot, which may hold edits never explicitly saved to its file), or falls back to one
    /// fresh blank project on first run or if there's no session to restore.</summary>
    private void LoadSessionOrDefault()
    {
        var loaded = SessionStore.Load(out var activeIndex);
        if (loaded is { Count: > 0 })
        {
            foreach (var (project, filePath) in loaded)
                OpenProjects.Add(new ProjectTab(project, filePath));
            ActiveTab = OpenProjects[Math.Clamp(activeIndex, 0, OpenProjects.Count - 1)];
        }
        else
        {
            var tab = new ProjectTab(new SceneProject());
            OpenProjects.Add(tab);
            ActiveTab = tab;
        }
    }

    private void SaveSession()
    {
        var activeIndex = ActiveTab is null ? 0 : Math.Max(0, OpenProjects.IndexOf(ActiveTab));
        SessionStore.Save(OpenProjects.Select(t => (t.Id, t.Project, t.FilePath)).ToList(), activeIndex);
    }
}
