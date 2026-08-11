using System.Windows;

namespace MiniRef.App.Views;

/// <summary>Prompts for the name a Character Pod gets saved under -- "Name the Character" or
/// "Name the Setting" depending on the subject's classification (decided by the caller). Modeled
/// on NewProjectDialog, except a blank name isn't accepted here: unlike a project name, this one
/// becomes the pod's actual filename.</summary>
public partial class NamePodDialog : Window
{
    public string PodName { get; private set; } = "";

    public NamePodDialog(string promptLabel, string initialName)
    {
        InitializeComponent();
        Title = promptLabel;
        PromptText.Text = promptLabel;
        NameTextBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            NameTextBox.Focus();
            return;
        }

        PodName = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
