using System.Windows;

namespace MiniRef.App.Views;

public partial class NewProjectDialog : Window
{
    public string ProjectName { get; private set; } = "Untitled Scene";

    public NewProjectDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        ProjectName = name.Length == 0 ? "Untitled Scene" : name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
