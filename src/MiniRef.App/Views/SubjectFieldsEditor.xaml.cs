using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MiniRef.App.ViewModels;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

/// <summary>The Classification/Name/Description/Pictures/Audio/Retention fields for editing one
/// Subject -- extracted out of <see cref="SubjectEditor"/> so the same fields can be reused
/// standalone (no list-membership, no move/remove-from-list chrome) in a Character Pod editor.
/// Its DataContext is expected to be the Subject being edited, inherited from whatever host places
/// it -- same as SubjectEditor's own field content did before this control existed.</summary>
public partial class SubjectFieldsEditor : UserControl
{
    public SubjectFieldsEditor() => InitializeComponent();

    private Subject? Subject => DataContext as Subject;

    /// <summary>Only needed for the two Browse handlers' "last folder used" bookkeeping -- resolved
    /// this way (rather than a passed-in dependency) because AppSettings is genuinely app-wide and
    /// this control has no other reason to need a constructor parameter or dependency property just
    /// to reach it, whether it's embedded in the main Cast & Setting list or in a standalone pod
    /// editor window (Application.Current.MainWindow is still the same window either way).</summary>
    private AppSettings? Settings => (Application.Current.MainWindow?.DataContext as MainViewModel)?.Settings;

    private void AddPicture_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) s.Pictures.Add(new PictureRef());
    }

    private void RemovePicture_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s && sender is Button { DataContext: PictureRef picture })
            s.Pictures.Remove(picture);
    }

    private void ToggleAudio_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) s.Audio = s.Audio is null ? new AudioRef() : null;
    }

    private void BrowsePicture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PictureRef picture }) return;
        if (Settings is not { } settings) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*",
            ClientGuid = MainViewModel.PictureDialogGuid
        };
        if (Directory.Exists(settings.LastPictureFolder)) dialog.InitialDirectory = settings.LastPictureFolder;
        if (dialog.ShowDialog() != true) return;

        picture.FilePath = dialog.FileName;
        RememberFolder(settings, f => settings.LastPictureFolder = f, dialog.FileName);
    }

    private void BrowseAudio_Click(object sender, RoutedEventArgs e)
    {
        if (Subject?.Audio is not { } audio) return;
        if (Settings is not { } settings) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.ogg;*.m4a|All files|*.*",
            ClientGuid = MainViewModel.AudioDialogGuid
        };
        if (Directory.Exists(settings.LastAudioFolder)) dialog.InitialDirectory = settings.LastAudioFolder;
        if (dialog.ShowDialog() != true) return;

        audio.FilePath = dialog.FileName;
        RememberFolder(settings, f => settings.LastAudioFolder = f, dialog.FileName);
    }

    private static void RememberFolder(AppSettings settings, Action<string> assign, string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return;

        assign(folder);
        SettingsStore.Save(settings);
    }
}
