using System.Windows;

namespace MiniRef.App.Views;

public partial class ImportPromptTextDialog : Window
{
    /// <summary>The raw prompt the user pasted, and the name to give the new project -- both read by
    /// MainViewModel.ImportPromptText only when the dialog closed with DialogResult == true.</summary>
    public string PromptText { get; private set; } = "";
    public string ProjectName { get; private set; } = "Imported Prompt";

    public ImportPromptTextDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptTextBox.Focus();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        PromptText = PromptTextBox.Text;
        var name = ProjectNameTextBox.Text.Trim();
        ProjectName = name.Length == 0 ? "Imported Prompt" : name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
