using System.Windows;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

public partial class PromptPreviewWindow : Window
{
    private readonly SceneProject _project;

    public PromptPreviewWindow(SceneProject project, int initialSegment = 0)
    {
        InitializeComponent();
        _project = project;

        // Only worth showing when the project is a chain -- a plain single-clip project looks
        // exactly as it always has.
        if (project.SegmentCount > 1)
        {
            for (var i = 0; i < project.SegmentCount; i++)
                SegmentCombo.Items.Add($"Segment {i + 1}");
            SegmentCombo.SelectedIndex = Math.Clamp(initialSegment, 0, project.SegmentCount - 1);
            SegmentCombo.Visibility = Visibility.Visible;
        }

        Refresh();
    }

    private int SegmentIndex => SegmentCombo.SelectedIndex < 0 ? 0 : SegmentCombo.SelectedIndex;

    private void Refresh()
    {
        PromptTextBox.Text = PromptComposer.ComposeSegment(_project, SegmentIndex);
        StatusText.Text = "";
    }

    private void SegmentCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PromptTextBox.Text);
        StatusText.Text = "Copied.";
    }
}
