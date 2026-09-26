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
    // Picture/AudioDialogGuid are internal rather than private -- SubjectFieldsEditor (used both in
    // the main Cast & Setting list and standalone in CharacterPodEditorWindow) opens the same
    // picture/audio browse dialogs itself and needs to share the same per-purpose identity.
    internal static readonly Guid PictureDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a01");
    internal static readonly Guid AudioDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a02");
    private static readonly Guid VideoDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a03");
    private static readonly Guid ProjectDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a04");
    private static readonly Guid ComfyExportDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a05");
    private static readonly Guid ComfyImportDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a06");
    private static readonly Guid CharacterPodDialogGuid = new("f3b1b7b0-4b7a-4b8e-9b0a-1f7a8f4b5a07");

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
        get => EffectiveTaskTypes.HasFlag(TaskType.KeyframeCompletion);
        set => SetTaskFlag(TaskType.KeyframeCompletion, value);
    }

    public bool TaskReferenceGeneration
    {
        get => EffectiveTaskTypes.HasFlag(TaskType.ReferenceGeneration);
        set => SetTaskFlag(TaskType.ReferenceGeneration, value);
    }

    public bool TaskVideoEditing
    {
        get => EffectiveTaskTypes.HasFlag(TaskType.VideoEditing);
        set => SetTaskFlag(TaskType.VideoEditing, value);
    }

    public bool TaskVideoContinuation
    {
        get => EffectiveTaskTypes.HasFlag(TaskType.VideoContinuation);
        set => SetTaskFlag(TaskType.VideoContinuation, value);
    }

    public bool TaskAudioReuse
    {
        get => EffectiveTaskTypes.HasFlag(TaskType.AudioReuse);
        set => SetTaskFlag(TaskType.AudioReuse, value);
    }

    public bool TaskAudioReference
    {
        get => EffectiveTaskTypes.HasFlag(TaskType.AudioReference);
        set => SetTaskFlag(TaskType.AudioReference, value);
    }

    /// <summary>The task types in force for the segment being edited. For a continuation segment
    /// that includes the ones added automatically (video continuation, and audio reuse/reference
    /// from the previous clip's audio setting), which is why those boxes are shown ticked but locked.</summary>
    private TaskType EffectiveTaskTypes => Project.ForSegment(SafeSegmentIndex).TaskTypes;

    private void SetTaskFlag(TaskType flag, bool value)
    {
        if (SafeSegmentIndex == 0)
        {
            Project.TaskTypes = value ? Project.TaskTypes | flag : Project.TaskTypes & ~flag;
        }
        else
        {
            var segment = Project.Continuations[SafeSegmentIndex - 1];
            segment.TaskTypes = value ? segment.TaskTypes | flag : segment.TaskTypes & ~flag;
        }
        OnPropertyChanged(nameof(TaskKeyframeCompletion));
        OnPropertyChanged(nameof(TaskReferenceGeneration));
        OnPropertyChanged(nameof(TaskVideoEditing));
        OnPropertyChanged(nameof(TaskVideoContinuation));
        OnPropertyChanged(nameof(TaskAudioReuse));
        OnPropertyChanged(nameof(TaskAudioReference));
    }

    // ---- Continuation segments ----
    // The project's own shots/summary/duration are segment 0; Project.Continuations are 1..N. The
    // editing panes bind to the "Current*" members below so they follow whichever segment is
    // selected, while the cast, style, and resolution stay shared and are still bound via Project.

    [ObservableProperty] private int currentSegmentIndex;

    /// <summary>"Segment 1".."Segment N" for the selector. Rebuilt in place when a segment is added or
    /// removed or the active project changes; <see cref="_rebuildingSegmentLabels"/> stops the
    /// ListBox's transient "nothing selected" during the rebuild from clobbering the selection.</summary>
    public ObservableCollection<string> SegmentLabels { get; } = [];
    private bool _rebuildingSegmentLabels;

    [ObservableProperty] private string segmentWarnings = "";

    public bool HasSegmentWarnings => SegmentWarnings.Length > 0;
    public bool IsContinuationSegment => SafeSegmentIndex > 0;
    public bool IsNotContinuationSegment => SafeSegmentIndex == 0;

    private int SafeSegmentIndex => Math.Clamp(CurrentSegmentIndex, 0, Project.Continuations.Count);

    /// <summary>The object whose Summary/OverallSoundscape/NonDiegeticMusic/DurationSeconds the scene
    /// fields bind to: the SceneProject itself for segment 1, otherwise that continuation's
    /// SceneSegment. Both expose the same property names, so the XAML doesn't care which it is.</summary>
    public object CurrentSegment => SafeSegmentIndex == 0 ? Project : Project.Continuations[SafeSegmentIndex - 1];

    public ObservableCollection<Shot> CurrentShots =>
        SafeSegmentIndex == 0 ? Project.Shots : Project.Continuations[SafeSegmentIndex - 1].Shots;

    /// <summary>Segment 1's source-video list, or for a continuation the single previous-clip video.</summary>
    public ObservableCollection<VideoRef> CurrentVideos =>
        SafeSegmentIndex == 0 ? Project.SourceVideos : Project.Continuations[SafeSegmentIndex - 1].VideoList;

    public string CurrentSummary
    {
        get => SafeSegmentIndex == 0 ? Project.Summary : Project.Continuations[SafeSegmentIndex - 1].Summary;
        set
        {
            if (SafeSegmentIndex == 0) Project.Summary = value;
            else Project.Continuations[SafeSegmentIndex - 1].Summary = value;
        }
    }

    partial void OnCurrentSegmentIndexChanged(int value)
    {
        if (_rebuildingSegmentLabels) return;
        RaiseSegmentChanged();
    }

    private void RaiseSegmentChanged()
    {
        OnPropertyChanged(nameof(CurrentSegment));
        OnPropertyChanged(nameof(CurrentShots));
        OnPropertyChanged(nameof(CurrentVideos));
        OnPropertyChanged(nameof(CurrentSummary));
        OnPropertyChanged(nameof(IsContinuationSegment));
        OnPropertyChanged(nameof(IsNotContinuationSegment));
        OnPropertyChanged(nameof(TaskKeyframeCompletion));
        OnPropertyChanged(nameof(TaskReferenceGeneration));
        OnPropertyChanged(nameof(TaskVideoEditing));
        OnPropertyChanged(nameof(TaskVideoContinuation));
        OnPropertyChanged(nameof(TaskAudioReuse));
        OnPropertyChanged(nameof(TaskAudioReference));
        RefreshSegmentWarnings();
    }

    private void RebuildSegmentLabels()
    {
        var keep = SafeSegmentIndex;
        _rebuildingSegmentLabels = true;
        try
        {
            SegmentLabels.Clear();
            for (var i = 0; i < Project.SegmentCount; i++)
                SegmentLabels.Add($"Segment {i + 1}");
            CurrentSegmentIndex = keep;
        }
        finally
        {
            _rebuildingSegmentLabels = false;
        }

        OnPropertyChanged(nameof(CurrentSegmentIndex));   // re-select in the ListBox after the rebuild
        RaiseSegmentChanged();
    }

    /// <summary>Re-checks the selected segment's reference inputs against ref2va's per-generation
    /// caps. Called whenever the segment changes or the shared cast does (MainWindow's derived-state
    /// refresh), since adding a picture can push a later segment over.</summary>
    public void RefreshSegmentWarnings()
    {
        SegmentWarnings = string.Join("\n",
            ReferenceLimits.Check(Project.ForSegment(SafeSegmentIndex)).Concat(ReferenceLimits.CheckLength(Project, SafeSegmentIndex)));
        OnPropertyChanged(nameof(HasSegmentWarnings));
    }

    [RelayCommand]
    private void AddSegment()
    {
        // Start at the previous clip's length -- a chain is usually equal-length clips.
        var previousSeconds = Project.Continuations.Count == 0 ? Project.DurationSeconds : Project.Continuations[^1].DurationSeconds;
        Project.Continuations.Add(new SceneSegment { DurationSeconds = previousSeconds });
        RebuildSegmentLabels();
        CurrentSegmentIndex = Project.Continuations.Count;   // jump to the new one
    }

    [RelayCommand]
    private void RemoveCurrentSegment()
    {
        var index = SafeSegmentIndex;
        if (index == 0) return;

        var segment = Project.Continuations[index - 1];
        var hasContent = segment.Shots.Count > 0 || !string.IsNullOrWhiteSpace(segment.Summary);
        if (hasContent && MessageBox.Show(
                $"Remove Segment {index + 1} and its {segment.Shots.Count} shot(s)? This can't be undone.",
                "Remove Segment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Project.Continuations.RemoveAt(index - 1);
        CurrentSegmentIndex = index - 1;
        RebuildSegmentLabels();
    }

    partial void OnActiveTabChanged(ProjectTab? value)
    {
        CurrentSegmentIndex = 0;
        RebuildSegmentLabels();
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
    private void CreateCharacterPod()
    {
        if (!EnsurePodsFolderConfigured()) return;

        var editor = new CharacterPodEditorWindow(new Subject(), existingPodFilePath: null, Settings.CharacterPodsFolder)
            { Owner = Application.Current.MainWindow };
        if (editor.ShowDialog() != true) return;

        Project.Subjects.Add(editor.SavedSubject);
    }

    [RelayCommand]
    private void EditCharacterPod()
    {
        if (!EnsurePodsFolderConfigured()) return;

        var openDialog = new OpenFileDialog
        {
            Filter = $"Character Pod (*{CharacterPodStore.FileExtension})|*{CharacterPodStore.FileExtension}|All files (*.*)|*.*",
            InitialDirectory = Settings.CharacterPodsFolder,
            ClientGuid = CharacterPodDialogGuid
        };
        if (openDialog.ShowDialog() != true) return;

        if (!TryLoadPod(openDialog.FileName, out var subject)) return;

        // Edit is a library-maintenance action -- it never touches Project.Subjects, regardless of
        // Save or Cancel. "Load Existing Pod..." is the separate action for using a character here.
        var editor = new CharacterPodEditorWindow(subject, openDialog.FileName, Settings.CharacterPodsFolder)
            { Owner = Application.Current.MainWindow };
        editor.ShowDialog();
    }

    [RelayCommand]
    private void LoadCharacterPod()
    {
        if (!EnsurePodsFolderConfigured()) return;

        var dialog = new OpenFileDialog
        {
            Filter = $"Character Pod (*{CharacterPodStore.FileExtension})|*{CharacterPodStore.FileExtension}|All files (*.*)|*.*",
            InitialDirectory = Settings.CharacterPodsFolder,
            ClientGuid = CharacterPodDialogGuid
        };
        if (dialog.ShowDialog() != true) return;

        if (!TryLoadPod(dialog.FileName, out var subject)) return;

        Project.Subjects.Add(subject);
    }

    private static bool TryLoadPod(string podFilePath, out Subject subject)
    {
        try
        {
            subject = CharacterPodStore.Load(podFilePath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            MessageBox.Show($"Couldn't load that pod:\n{ex.Message}", "Character Pod", MessageBoxButton.OK, MessageBoxImage.Error);
            subject = null!;
            return false;
        }
    }

    /// <summary>Guards Create/Edit/Load pod actions on a configured library folder, offering to
    /// jump straight into Settings if it's still blank rather than a dead-end error message.</summary>
    private bool EnsurePodsFolderConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Settings.CharacterPodsFolder))
        {
            Directory.CreateDirectory(Settings.CharacterPodsFolder);
            return true;
        }

        var result = MessageBox.Show(
            "Set a folder for Character Pods first (Settings > Character Pods folder).\n\nOpen Settings now?",
            "Character Pods", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes) ShowSettingsWindow(isFirstRun: false);

        return !string.IsNullOrWhiteSpace(Settings.CharacterPodsFolder);
    }

    /// <summary>Swaps every detail of an existing subject -- name, appearance, pictures, voice
    /// references, retention -- for those from a Character Pod, keeping the subject in place so its
    /// &lt;Subject N&gt; number, and every already-typed tag that uses it, stays valid. Picture/audio
    /// reference numbers are rewritten afterwards the same way a delete or reorder does it, since a
    /// pod almost never carries the same count the subject had. The point: import a prompt where a
    /// character speaks but has no voice, drop in a pod that carries one, and the voice-timbre
    /// sentence and (Sx) sequencing fill themselves in.</summary>
    [RelayCommand]
    private void ReplaceSubjectWithPod(Subject subject)
    {
        if (!EnsurePodsFolderConfigured()) return;

        var dialog = new OpenFileDialog
        {
            Filter = $"Character Pod (*{CharacterPodStore.FileExtension})|*{CharacterPodStore.FileExtension}|All files (*.*)|*.*",
            InitialDirectory = Settings.CharacterPodsFolder,
            ClientGuid = CharacterPodDialogGuid
        };
        if (dialog.ShowDialog() != true) return;

        if (!TryLoadPod(dialog.FileName, out var pod)) return;

        MutateAndRenumber(() =>
        {
            subject.Classification = pod.Classification;
            subject.Name = pod.Name;
            subject.Description = pod.Description;
            subject.Retention = pod.Retention;
            subject.RetentionNote = pod.RetentionNote;

            subject.Pictures.Clear();
            foreach (var picture in pod.Pictures) subject.Pictures.Add(picture);

            subject.Audios.Clear();
            foreach (var audio in pod.Audios) subject.Audios.Add(audio);
        });
    }

    [RelayCommand]
    private void RemoveSubject(Subject subject) => MutateAndRenumber(() => Project.Subjects.Remove(subject));

    [RelayCommand]
    private void MoveSubjectUp(Subject subject) => MutateAndRenumber(() => Move(Project.Subjects, subject, -1));

    [RelayCommand]
    private void MoveSubjectDown(Subject subject) => MutateAndRenumber(() => Move(Project.Subjects, subject, 1));

    /// <summary>Wraps a mutation that can shift &lt;Subject N&gt;/&lt;Picture N&gt;/&lt;Audio N&gt;/
    /// &lt;Video N&gt; numbers (removing or reordering a subject/video) so every already-typed tag
    /// referencing a survivor whose number changed gets rewritten to match, instead of silently
    /// going stale -- e.g. deleting &lt;Picture 5&gt; would otherwise leave every "&lt;Picture 6&gt;"
    /// elsewhere pointing at a picture that no longer exists at that number.</summary>
    private void MutateAndRenumber(Action mutate)
    {
        var before = ReferenceNumberer.ComputeAllSegments(Project);
        mutate();
        var after = ReferenceNumberer.ComputeAllSegments(Project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(Project, before, after);
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
    private void AddShot() => CurrentShots.Add(new Shot());

    [RelayCommand]
    private void RemoveShot(Shot shot) => CurrentShots.Remove(shot);

    [RelayCommand]
    private void MoveShotUp(Shot shot) => Move(CurrentShots, shot, -1);

    [RelayCommand]
    private void MoveShotDown(Shot shot) => Move(CurrentShots, shot, 1);

    [RelayCommand]
    private void AddSourceVideo()
    {
        if (SafeSegmentIndex == 0) Project.SourceVideos.Add(new VideoRef());
    }

    [RelayCommand]
    private void RemoveSourceVideo(VideoRef video)
    {
        if (video.FromPreviousSegment) return;   // a continuation's <Video 1> is the previous clip; it can't be removed
        MutateAndRenumber(() => Project.SourceVideos.Remove(video));
    }

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
        // A brand-new, never-saved tab has no meaningfully distinct "prior" state to keep separate
        // from what's being saved right now -- claim this same tab (see keepOriginalTabOpen: false
        // below) instead of leaving a redundant, permanently-unsaved duplicate behind it.
        if (tab.FilePath is null) return SaveTabAs(tab, keepOriginalTabOpen: false);

        ProjectStore.Save(tab.Project, tab.FilePath);
        tab.MarkSaved();
        SaveSession();
        return true;
    }

    [RelayCommand]
    private void SaveProjectAs() => SaveTabAs(ActiveTab, keepOriginalTabOpen: true);

    /// <param name="keepOriginalTabOpen">True for an explicit "Save As...": the file just saved
    /// opens as its own new tab (via OpenProjectFile, same as opening it by hand), and whatever was
    /// open before -- including any unsaved changes -- is left completely untouched rather than
    /// being silently repointed at the new file. False for a first-ever Save on a new tab, where
    /// there's nothing else to retain.</param>
    private bool SaveTabAs(ProjectTab? tab, bool keepOriginalTabOpen)
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
        RememberFolder(f => Settings.LastProjectFolder = f, dialog.FileName);

        if (keepOriginalTabOpen)
        {
            OpenProjectFile(dialog.FileName);
        }
        else
        {
            tab.FilePath = dialog.FileName;
            tab.Project.Name = NameFromProjectFilePath(dialog.FileName);
            tab.MarkSaved();
            SaveSession();
        }

        return true;
    }

    /// <summary>Derives a project's display name from a chosen file path -- so claiming a tab's
    /// first-ever save (see SaveTabAs) updates its switcher entry to match, instead of leaving it
    /// stuck on whatever the project was called before (confusing once the file name and the in-app
    /// name have drifted apart). Strips the full ".mmref.json" extension rather than just the last
    /// segment, since Path.GetFileNameWithoutExtension alone would leave ".mmref" behind.</summary>
    private static string NameFromProjectFilePath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(ProjectStore.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ProjectStore.FileExtension.Length]
            : Path.GetFileNameWithoutExtension(fileName);
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

        // Each segment is its own generation carrying the whole shared cast, so check every one
        // against ref2va's input caps -- anything over is silently left out of the export.
        var limitProblems = Enumerable.Range(0, Project.SegmentCount)
            .SelectMany(i => ReferenceLimits.Check(Project.ForSegment(i))
                .Select(problem => Project.SegmentCount > 1 ? $"Segment {i + 1}: {problem}" : problem))
            .Concat(ReferenceLimits.CheckLengths(Project))
            .ToList();
        if (limitProblems.Count > 0 && MessageBox.Show(
                string.Join("\n", limitProblems) + "\n\nExport anyway?",
                "Export ComfyUI Workflow", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var root = Settings.ComfyUiRootFolder;
        var hasRoot = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        var workflowJson = ComfyWorkflowExporter.Export(
            templateJson, Project,
            resolvePictureFilename: (_, picture) => ResolveComfyInputFilename(picture.FilePath, hasRoot ? root : null, picture.Id),
            resolveAudioFilename: (_, audio) => ResolveComfyInputFilename(audio.FilePath, hasRoot ? root : null, audio.Id),
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

        var chainNote = Project.SegmentCount > 1
            ? $"This is a {Project.SegmentCount}-segment chain: one workflow that renders {Project.SegmentCount} clips back to back " +
              $"(saved as ..._part1 to ..._part{Project.SegmentCount}). Each clip after the first is fed the previous clip's frames and " +
              "audio inside the graph, and the pictures/voices are loaded once and shared -- load it in ComfyUI and queue it once.\n\n"
            : "";

        MessageBox.Show(
            $"Workflow exported to:\n{outputPath}\n\n" + chainNote +
            "LoadImage/LoadAudio/VHS_LoadVideoPath nodes are titled with their <Picture N>/<Audio N>/<Video N> " +
            "tag and character name. Pictures/audio with a file chosen were copied into ComfyUI's input folder " +
            "automatically; anything without a file picked still shows a placeholder to fill in by hand.\n\n" +
            "Video nodes use VHS_LoadVideoPath (ComfyUI-VideoHelperSuite) -- make sure that custom node pack " +
            "is installed, or those nodes won't resolve.",
            "Export ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Reconstructs a project from a ComfyUI workflow's prompt text and node titles --
    /// best-effort, tuned for workflows this tool exported but tolerant of a hand-written or
    /// AI-drafted MiniMax H3 prompt. Always opens as a new tab; never replaces what's open.</summary>
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
                $"Couldn't import that workflow:\n{ex.Message}\n\nThe file needs to be a ComfyUI workflow (a JSON file with a 'nodes' array).",
                "Import ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        imported.Name = GuessProjectName(dialog.FileName);
        var tab = new ProjectTab(imported);
        OpenProjects.Add(tab);
        ActiveTab = tab;
        SaveSession();

        MessageBox.Show(
            "Workflow imported as a new project. This is a best-effort reconstruction from the prompt text and node " +
            "titles, not a dedicated round-trip format -- a Subject is created for every <Subject N> the workflow " +
            "mentions, but a few UI-only details (subject classification, and task type checkboxes if the summary was " +
            "left blank) don't carry over and are left at their defaults. Double-check the result before continuing.",
            "Import ComfyUI Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Builds a new project from a six-section MiniMax H3 prompt drafted elsewhere and
    /// pasted in -- the shot-by-shot text, subjects, picture/audio slots, and spoken &lt;d&gt; lines
    /// all come across; reference files don't (there's nothing to resolve them from), so each
    /// picture/voice slot lands empty for a Character Pod to fill via "Replace with Pod...".
    /// Always opens as a new tab; never replaces what's open.</summary>
    [RelayCommand]
    private void ImportPromptText()
    {
        var dialog = new ImportPromptTextDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        SceneProject imported;
        try
        {
            imported = ComfyWorkflowImporter.ImportPromptText(dialog.PromptText);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(
                $"Couldn't import that prompt:\n{ex.Message}",
                "Import Prompt Text", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        imported.Name = "Imported Prompt";
        var tab = new ProjectTab(imported);
        OpenProjects.Add(tab);
        ActiveTab = tab;
        SaveSession();

        MessageBox.Show(
            "Prompt imported as a new project. A Subject was created for every <Subject N> the text mentions, " +
            "with an empty <Picture N>/<Audio N> slot for each reference it declares -- use \"Replace with Pod...\" " +
            "on a subject to fill those in. Spoken <d> lines came across as structured dialogue, so speaker (Sx) IDs " +
            "and the voice-timbre sentence compose automatically once a subject has a voice reference. Task type, " +
            "duration, aspect ratio, and megapixels aren't part of the prompt text and stay at their defaults. " +
            "Double-check the result before continuing.",
            "Import Prompt Text", MessageBoxButton.OK, MessageBoxImage.Information);
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
    /// often carry generic camera/download names (IMG_1234.jpg) that collide -- both across unrelated
    /// projects and between two references in the *same* project (e.g. two subjects whose pictures
    /// happen to share a filename from different folders). A later export can silently overwrite an
    /// earlier one's file out from under it, and since the copy target is also the LoadImage/LoadAudio
    /// widget value, two unrelated nodes end up pointing at the same (wrong) image. Copies are named
    /// "miniref-{workflow-name}-{referenceId}-{original filename}" -- <paramref name="referenceId"/>
    /// is the PictureRef/AudioRef's own stable Id, so every reference gets a distinct file regardless
    /// of what its source file happens to be named, and re-exporting the same project reuses the same
    /// target name instead of accumulating copies.</summary>
    private string? ResolveComfyInputFilename(string? filePath, string? comfyRoot, Guid referenceId)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        if (string.IsNullOrWhiteSpace(comfyRoot)) return Path.GetFileName(filePath);

        var inputFolder = Path.Combine(comfyRoot, "input");
        Directory.CreateDirectory(inputFolder);

        var fullSource = Path.GetFullPath(filePath);
        var fullInput = Path.GetFullPath(inputFolder);
        if (fullSource.StartsWith(fullInput, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(fullInput, fullSource);

        var targetName = BuildComfyInputFileName(filePath, referenceId);
        File.Copy(fullSource, Path.Combine(inputFolder, targetName), overwrite: true);
        return targetName;
    }

    private string BuildComfyInputFileName(string sourceFilePath, Guid referenceId)
    {
        var workflowSlug = SanitizeFileName(Project.Name).Replace(' ', '-');
        var idSlug = referenceId.ToString("N")[..8];
        return $"miniref-{workflowSlug}-{idSlug}-{Path.GetFileName(sourceFilePath)}";
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
