using System.Windows;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

public partial class PromptPreviewWindow : Window
{
    private readonly SceneProject _project;

    public PromptPreviewWindow(SceneProject project)
    {
        InitializeComponent();
        _project = project;
        Refresh();
    }

    private void Refresh()
    {
        PromptTextBox.Text = PromptComposer.Compose(_project);
        StatusText.Text = "";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PromptTextBox.Text);
        StatusText.Text = "Copied.";
    }
}
