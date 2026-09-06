using System.Windows;

namespace MiniRef.App.Views;

public partial class ImportPromptTextDialog : Window
{
    /// <summary>The raw prompt the user pasted -- read by MainViewModel.ImportPromptText only when
    /// the dialog closed with DialogResult == true.</summary>
    public string PromptText { get; private set; } = "";

    public ImportPromptTextDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptTextBox.Focus();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        PromptText = PromptTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
