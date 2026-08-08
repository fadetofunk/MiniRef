using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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

    /// <summary>Guards RebuildShotDocument's Document replacement from being read back by
    /// ShotTextBox_TextChanged as if it were a user edit -- harmless either way since it would
    /// just re-serialize to the same Shot.Text, but this skips that redundant work.</summary>
    private bool _suppressTextChanged;

    public ShotCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RebuildShotDocument();
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
        card.RebuildShotDocument();
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
        card.RebuildShotDocument();
    }

    private void Subjects_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (Subject s in e.OldItems) Unsubscribe(s);
        if (e.NewItems is not null)
            foreach (Subject s in e.NewItems) Subscribe(s);
        RebuildChips();
        RebuildShotDocument();
    }

    private void Videos_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (VideoRef v in e.OldItems) UnsubscribeVideo(v);
        if (e.NewItems is not null)
            foreach (VideoRef v in e.NewItems) SubscribeVideo(v);
        RebuildChips();
        RebuildShotDocument();
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

    private void Subject_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildChips();
        RebuildShotDocument();
    }

    private void PicturesOrDialogue_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildChips();
        RebuildShotDocument();
    }

    private void RebuildChips()
    {
        Chips.Clear();
        if (AllSubjects is null) return;
        foreach (var chip in TagChipBuilder.Build(AllSubjects, SourceVideos))
            Chips.Add(chip);
    }

    /// <summary>Rebuilds the shot text box's FlowDocument from Shot.Text, chipifying reference
    /// tags. Called whenever the underlying data a chip's label depends on changes from outside
    /// this control (a different Shot bound in, a subject renamed, pictures/videos added or
    /// removed) -- never while the user is typing in this box, since ShotTextBox_TextChanged
    /// only pushes the live document into Shot.Text and never triggers a rebuild of its own.</summary>
    private void RebuildShotDocument()
    {
        if (Shot is not { } shot) return;

        _suppressTextChanged = true;
        try
        {
            ShotTextBox.Document = ShotRichTextBuilder.BuildDocument(shot.Text, AllSubjects ?? [], SourceVideos ?? []);
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void ShotTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        if (Shot is not { } shot) return;
        shot.Text = ShotRichTextBuilder.Serialize(ShotTextBox.Document);
    }

    /// <summary>Enter inserts a soft line break instead of WPF's default new-Paragraph behavior,
    /// keeping the whole shot as one Paragraph so LineBreak &lt;-&gt; '\n' stays a clean 1:1 mapping
    /// in ShotRichTextBuilder rather than juggling multiple Paragraph blocks for ordinary typing.</summary>
    private void ShotTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        EditingCommands.EnterLineBreak.Execute(null, ShotTextBox);
    }

    /// <summary>Forces paste to plain text so clipboard formatting (e.g. copying from a browser
    /// or Word) can't smuggle in Bold/Span/Hyperlink inlines that ShotRichTextBuilder.Serialize
    /// doesn't know how to round-trip.</summary>
    private void ShotTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var text = (string)e.DataObject.GetData(DataFormats.UnicodeText);
        e.DataObject = new DataObject(DataFormats.UnicodeText, text);
        e.FormatToApply = DataFormats.UnicodeText;
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

    /// <summary>Inserts at the caret, chipifying any reference tags <paramref name="text"/>
    /// contains (whole chip inserts, or a dialogue/silent line that starts with one). Replaces
    /// the current selection first if there is one, so e.g. selecting a placeholder word and
    /// clicking a reference chip swaps it in rather than leaving the placeholder behind. The
    /// resulting Shot.Text update happens via ShotTextBox_TextChanged, not here.</summary>
    private void InsertAtCaret(string text)
    {
        if (Shot is null) return;

        var selection = ShotTextBox.Selection;
        if (!selection.IsEmpty)
            selection.Text = "";

        var end = ShotRichTextBuilder.InsertAt(ShotTextBox.CaretPosition, text, AllSubjects ?? [], SourceVideos ?? []);
        ShotTextBox.CaretPosition = end;
        ShotTextBox.Focus();
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
