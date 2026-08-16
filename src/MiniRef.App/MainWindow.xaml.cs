using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiniRef.App.ViewModels;
using MiniRef.App.Views;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private readonly ObservableCollection<TagChip> _summaryChips = [];
    private readonly HashSet<Subject> _subscribedSubjects = [];
    private ObservableCollection<Subject>? _subscribedList;
    private readonly HashSet<VideoRef> _subscribedVideos = [];
    private ObservableCollection<VideoRef>? _subscribedVideoList;

    public MainWindow()
    {
        InitializeComponent();
        SummaryChipsControl.ItemsSource = _summaryChips;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        AttachToProject(ViewModel.Project);

        // A ContextMenu is a separate logical-tree root and doesn't inherit DataContext from its
        // owner -- without this, the "+ Add" menu's Command bindings would silently resolve to
        // nothing.
        AddSubjectButton.ContextMenu!.DataContext = DataContext;

        Loaded += MainWindow_Loaded;
    }

    private void AddSubjectButton_Click(object sender, RoutedEventArgs e)
    {
        AddSubjectButton.ContextMenu!.PlacementTarget = AddSubjectButton;
        AddSubjectButton.ContextMenu.IsOpen = true;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (string.IsNullOrWhiteSpace(ViewModel.Settings.ComfyUiRootFolder))
            ViewModel.ShowSettingsWindowForFirstRun();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Project))
            AttachToProject(ViewModel.Project);
    }

    private void AttachToProject(SceneProject project)
    {
        DetachSubjectList();
        _subscribedList = project.Subjects;
        _subscribedList.CollectionChanged += SubjectList_CollectionChanged;
        foreach (var s in _subscribedList) SubscribeSubject(s);

        DetachVideoList();
        _subscribedVideoList = project.SourceVideos;
        _subscribedVideoList.CollectionChanged += VideoList_CollectionChanged;
        foreach (var v in _subscribedVideoList) SubscribeVideo(v);

        RefreshDerivedState();
    }

    private void DetachSubjectList()
    {
        if (_subscribedList is not null)
            _subscribedList.CollectionChanged -= SubjectList_CollectionChanged;
        foreach (var s in _subscribedSubjects.ToList())
            UnsubscribeSubject(s);
    }

    private void DetachVideoList()
    {
        if (_subscribedVideoList is not null)
            _subscribedVideoList.CollectionChanged -= VideoList_CollectionChanged;
        foreach (var v in _subscribedVideos.ToList())
            UnsubscribeVideo(v);
    }

    private void SubjectList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (Subject s in e.OldItems) UnsubscribeSubject(s);
        if (e.NewItems is not null)
            foreach (Subject s in e.NewItems) SubscribeSubject(s);
        RefreshDerivedState();
    }

    private void VideoList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (VideoRef v in e.OldItems) UnsubscribeVideo(v);
        if (e.NewItems is not null)
            foreach (VideoRef v in e.NewItems) SubscribeVideo(v);
        RefreshDerivedState();
    }

    private void SubscribeSubject(Subject s)
    {
        if (!_subscribedSubjects.Add(s)) return;
        s.PropertyChanged += Subject_PropertyChanged;
        s.Pictures.CollectionChanged += ReferenceList_CollectionChanged;
        s.Audios.CollectionChanged += ReferenceList_CollectionChanged;
    }

    private void UnsubscribeSubject(Subject s)
    {
        if (!_subscribedSubjects.Remove(s)) return;
        s.PropertyChanged -= Subject_PropertyChanged;
        s.Pictures.CollectionChanged -= ReferenceList_CollectionChanged;
        s.Audios.CollectionChanged -= ReferenceList_CollectionChanged;
    }

    private void SubscribeVideo(VideoRef v)
    {
        if (!_subscribedVideos.Add(v)) return;
        v.PropertyChanged += Subject_PropertyChanged;
    }

    private void UnsubscribeVideo(VideoRef v)
    {
        if (!_subscribedVideos.Remove(v)) return;
        v.PropertyChanged -= Subject_PropertyChanged;
    }

    private void Subject_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshDerivedState();
    private void ReferenceList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDerivedState();

    /// <summary>Recomputes everything that depends on subject/picture/audio/video order: the
    /// summary chip list and each PictureRef/AudioRef's DisplayNumber (the N shown as
    /// "&lt;Picture N&gt;" next to it in the Cast &amp; Setting panel).</summary>
    private void RefreshDerivedState()
    {
        var subjects = ViewModel.Project.Subjects;
        var videos = ViewModel.Project.SourceVideos;

        _summaryChips.Clear();
        foreach (var chip in TagChipBuilder.Build(subjects, videos))
            _summaryChips.Add(chip);

        var (_, pictureNumbers, audioNumbers) = ReferenceNumberer.NumberSubjects(subjects);
        foreach (var subject in subjects)
        {
            foreach (var picture in subject.Pictures)
                picture.DisplayNumber = pictureNumbers[picture.Id];
            foreach (var audio in subject.Audios)
                audio.DisplayNumber = audioNumbers[audio.Id];
        }
    }

    private void SummaryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tagText }) return;

        var summary = ViewModel.Project.Summary;
        var caret = Math.Clamp(SummaryTextBox.CaretIndex, 0, summary.Length);
        ViewModel.Project.Summary = summary.Insert(caret, tagText);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            SummaryTextBox.CaretIndex = Math.Min(caret + tagText.Length, ViewModel.Project.Summary.Length);
            SummaryTextBox.Focus();
        }), DispatcherPriority.Background);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        new PromptPreviewWindow(ViewModel.Project) { Owner = this }.Show();
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (!ViewModel.ConfirmExit())
            e.Cancel = true;
    }

    /// <summary>Switches the active project to whichever row was clicked in the project switcher's
    /// dropdown. Clicks on that row's rename/close buttons never reach here -- Button already marks
    /// the underlying mouse event handled before it can bubble up to this Border-level handler.</summary>
    private void ProjectRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProjectTab { IsEditingName: false } tab) return;
        ViewModel.ActiveTab = tab;
        ProjectSwitcherToggle.IsChecked = false;
    }

    private void EditProjectName_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProjectTab tab)
            tab.IsEditingName = true;
    }

    private void CloseProjectTab_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProjectTab tab)
            ViewModel.CloseProjectCommand.Execute(tab);
    }

    /// <summary>Focuses and selects the rename TextBox the moment it becomes visible, rather than
    /// requiring a second click after hitting the pencil button.</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (((FrameworkElement)sender).DataContext is ProjectTab tab)
            tab.IsEditingName = false;
        e.Handled = true;
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProjectTab tab)
            tab.IsEditingName = false;
    }
}
