using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MiniRef.App.ViewModels;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

public partial class ShotCard : UserControl
{
    public static readonly DependencyProperty AllSubjectsProperty = DependencyProperty.Register(
        nameof(AllSubjects), typeof(ObservableCollection<Subject>), typeof(ShotCard),
        new PropertyMetadata(null, OnAllSubjectsChanged));

    public ObservableCollection<Subject>? AllSubjects
    {
        get => (ObservableCollection<Subject>?)GetValue(AllSubjectsProperty);
        set => SetValue(AllSubjectsProperty, value);
    }

    public static readonly DependencyProperty SourceVideosProperty = DependencyProperty.Register(
        nameof(SourceVideos), typeof(ObservableCollection<VideoRef>), typeof(ShotCard),
        new PropertyMetadata(null, OnSourceVideosChanged));

    public ObservableCollection<VideoRef>? SourceVideos
    {
        get => (ObservableCollection<VideoRef>?)GetValue(SourceVideosProperty);
        set => SetValue(SourceVideosProperty, value);
    }

    public static readonly DependencyProperty DisplayNumberProperty = DependencyProperty.Register(
        nameof(DisplayNumber), typeof(int), typeof(ShotCard), new PropertyMetadata(0));

    /// <summary>The 1-based "[Shot N]" number, set from MainWindow.xaml where the
    /// AlternationIndex binding still correctly reaches the ItemsControl's real item
    /// container -- doing that lookup from inside this UserControl's own XAML doesn't
    /// work, since the UserControl's own default ContentControl template introduces an
    /// extra ContentPresenter that the "nearest ancestor" search finds first.</summary>
    public int DisplayNumber
    {
        get => (int)GetValue(DisplayNumberProperty);
        set => SetValue(DisplayNumberProperty, value);
    }

    public ObservableCollection<TagChip> Chips { get; } = [];

    private readonly HashSet<Subject> _subscribed = [];
    private readonly HashSet<VideoRef> _subscribedVideos = [];

    public ShotCard()
    {
        InitializeComponent();
        Unloaded += (_, _) => { UnsubscribeAll(); UnsubscribeAllVideos(); };
    }

    private Shot? Shot => DataContext as Shot;

    private MainViewModel? MainViewModel => Application.Current.MainWindow?.DataContext as MainViewModel;

    private static void OnAllSubjectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ShotCard)d;
        card.UnsubscribeAll();

        if (e.OldValue is ObservableCollection<Subject> oldList)
            oldList.CollectionChanged -= card.Subjects_CollectionChanged;
        if (e.NewValue is ObservableCollection<Subject> newList)
        {
            newList.CollectionChanged += card.Subjects_CollectionChanged;
            foreach (var s in newList) card.Subscribe(s);
        }

        card.RebuildChips();
    }

    private static void OnSourceVideosChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ShotCard)d;
        card.UnsubscribeAllVideos();

        if (e.OldValue is ObservableCollection<VideoRef> oldList)
            oldList.CollectionChanged -= card.Videos_CollectionChanged;
        if (e.NewValue is ObservableCollection<VideoRef> newList)
        {
            newList.CollectionChanged += card.Videos_CollectionChanged;
            foreach (var v in newList) card.SubscribeVideo(v);
        }

        card.RebuildChips();
    }

    private void Subjects_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (Subject s in e.OldItems) Unsubscribe(s);
        if (e.NewItems is not null)
            foreach (Subject s in e.NewItems) Subscribe(s);
        RebuildChips();
    }

    private void Videos_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (VideoRef v in e.OldItems) UnsubscribeVideo(v);
        if (e.NewItems is not null)
            foreach (VideoRef v in e.NewItems) SubscribeVideo(v);
        RebuildChips();
    }

    private void Subscribe(Subject s)
    {
        if (!_subscribed.Add(s)) return;
        s.PropertyChanged += Subject_PropertyChanged;
        s.Pictures.CollectionChanged += PicturesOrDialogue_CollectionChanged;
    }

    private void Unsubscribe(Subject s)
    {
        if (!_subscribed.Remove(s)) return;
        s.PropertyChanged -= Subject_PropertyChanged;
        s.Pictures.CollectionChanged -= PicturesOrDialogue_CollectionChanged;
    }

    private void UnsubscribeAll()
    {
        foreach (var s in _subscribed.ToList()) Unsubscribe(s);
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

    private void UnsubscribeAllVideos()
    {
        foreach (var v in _subscribedVideos.ToList()) UnsubscribeVideo(v);
    }

    private void Subject_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RebuildChips();
    private void PicturesOrDialogue_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildChips();

    private void RebuildChips()
    {
        Chips.Clear();
        if (AllSubjects is null) return;
        foreach (var chip in TagChipBuilder.Build(AllSubjects, SourceVideos))
            Chips.Add(chip);
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tagText })
            InsertAtCaret(tagText);
    }

    private void InsertMotion_Click(object sender, RoutedEventArgs e)
    {
        if (MotionCombo.SelectedItem is not CameraMotion motion) return;
        var amplitude = AmplitudeCombo.SelectedItem as CameraAmplitude?;
        var speed = SpeedCombo.SelectedItem as CameraSpeed?;
        InsertAtCaret(" " + EnumFormatting.ToMotionSentence(motion, amplitude, speed));
    }

    private void InsertDialogue_Click(object sender, RoutedEventArgs e)
    {
        if (Shot is not { } shot) return;
        if (SpeakerCombo.SelectedItem is not Subject speaker) return;
        if (AllSubjects is null) return;

        var (subjectNumbers, _, _) = ReferenceNumberer.NumberSubjects(AllSubjects);
        var n = subjectNumbers.TryGetValue(speaker.Id, out var num) ? num : 0;

        // Neither guide defines an official "no dialogue" marker -- overall_soundscape's N/A
        // only covers silence for the whole video, not a single shot. So a silent subject is
        // called out in prose instead, using the same identifying prefix as spoken dialogue,
        // to give the model an explicit instruction rather than an audio gap it fills on its own.
        if (SilentCheckBox.IsChecked == true)
        {
            InsertAtCaret($" {ReferenceNumberer.SubjectTag(n)} {ReferenceNumberer.SpeakerTag(n)} says nothing, remaining silent.");
            return;
        }

        var text = DialogueTextBox.Text.Trim();
        if (text.Length == 0) return;
        var lang = string.IsNullOrWhiteSpace(LanguageBox.Text) ? "English" : LanguageBox.Text.Trim();

        // Per the guide's speaker/dialogue rules: everything outside <d> is the identifying
        // phrase, ID, action, and delivery; everything inside <d> is only the language tag and
        // the verbatim spoken content. The closing </d> is what tells the model dialogue has
        // ended and scene description resumes -- omitting it (as this used to) leaves the
        // boundary ambiguous. "says," is a generic default; edit it in place to add delivery,
        // e.g. "says in a hushed, panicked voice,".
        InsertAtCaret($" {ReferenceNumberer.SubjectTag(n)} {ReferenceNumberer.SpeakerTag(n)} says, <d>[{lang}] {text}</d>");
        shot.Dialogue.Add(new DialogueLine { SpeakerSubjectId = speaker.Id, Language = lang, Text = text });
        DialogueTextBox.Clear();
    }

    private void SilentCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        var silent = SilentCheckBox.IsChecked == true;
        LanguageBox.IsEnabled = !silent;
        DialogueTextBox.IsEnabled = !silent;
    }

    private void InsertOnScreenText_Click(object sender, RoutedEventArgs e)
    {
        var text = OnScreenTextBox.Text.Trim();
        if (text.Length == 0) return;
        InsertAtCaret($" \"{text}\"");
        OnScreenTextBox.Clear();
    }

    private void InsertAtCaret(string text)
    {
        if (Shot is not { } shot) return;
        var caret = Math.Clamp(ShotTextBox.CaretIndex, 0, shot.Text.Length);
        shot.Text = shot.Text.Insert(caret, text);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShotTextBox.CaretIndex = Math.Min(caret + text.Length, shot.Text.Length);
            ShotTextBox.Focus();
        }), DispatcherPriority.Background);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Shot is { } s) MainViewModel?.RemoveShotCommand.Execute(s);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Shot is { } s) MainViewModel?.MoveShotUpCommand.Execute(s);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Shot is { } s) MainViewModel?.MoveShotDownCommand.Execute(s);
    }
}
