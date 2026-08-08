using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

/// <summary>One editable row in the Workflow Models section, seeded from
/// <see cref="ComfyWorkflowExporter.DiscoverModelSlots"/> and any already-saved override.</summary>
public partial class ModelOverrideRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string DefaultFilename { get; init; }
    public required string ModelsFolder { get; init; }

    [ObservableProperty] private string overrideFilename = "";
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ModelOverrideRow> _modelRows = [];

    public SettingsWindow(AppSettings settings, string? templateJson, bool isFirstRun = false)
    {
        InitializeComponent();
        _settings = settings;
        RootFolderTextBox.Text = settings.ComfyUiRootFolder;

        if (isFirstRun)
        {
            WelcomeBanner.Visibility = Visibility.Visible;
            CancelButton.Content = "Skip for now";
        }

        ModelRowsControl.ItemsSource = _modelRows;
        PopulateModelRows(templateJson);
        UpdateRootValidationMessage();
    }

    private void PopulateModelRows(string? templateJson)
    {
        if (templateJson is null)
        {
            NoTemplateText.Visibility = Visibility.Visible;
            ModelRowsControl.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var slot in ComfyWorkflowExporter.DiscoverModelSlots(templateJson))
        {
            _modelRows.Add(new ModelOverrideRow
            {
                Key = slot.Key,
                Label = slot.Label,
                DefaultFilename = slot.CurrentFilename,
                ModelsFolder = slot.ModelsFolder,
                OverrideFilename = _settings.ModelOverrides.GetValueOrDefault(slot.Key, "")
            });
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select ComfyUI root folder" };
        if (!string.IsNullOrWhiteSpace(RootFolderTextBox.Text) && Directory.Exists(RootFolderTextBox.Text))
            dialog.InitialDirectory = RootFolderTextBox.Text;

        if (dialog.ShowDialog() == true)
            RootFolderTextBox.Text = dialog.FolderName;
    }

    private void RootFolderTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRootValidationMessage();

    private void UpdateRootValidationMessage()
    {
        var path = RootFolderTextBox.Text.Trim();
        if (path.Length == 0)
        {
            RootValidationText.Text = "";
            return;
        }

        if (ComfyRootValidator.LooksValid(path))
        {
            RootValidationText.Text = "✓ Found 'input' and 'models' folders here -- looks like a ComfyUI install.";
            RootValidationText.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
        }
        else
        {
            RootValidationText.Text = "⚠ Couldn't find 'input' and 'models' subfolders here -- double-check this is the right folder.";
            RootValidationText.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush");
        }
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModelOverrideRow row }) return;

        var dialog = new OpenFileDialog { Filter = "Model files|*.safetensors;*.ckpt;*.pt;*.bin|All files|*.*" };

        var root = RootFolderTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(row.ModelsFolder))
        {
            var modelsFolder = Path.Combine(root, "models", row.ModelsFolder);
            if (Directory.Exists(modelsFolder))
                dialog.InitialDirectory = modelsFolder;
        }

        if (dialog.ShowDialog() == true)
            row.OverrideFilename = Path.GetFileName(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var root = RootFolderTextBox.Text.Trim();
        if (root.Length > 0 && !ComfyRootValidator.LooksValid(root))
        {
            var proceed = MessageBox.Show(
                $"\"{root}\" doesn't look like a ComfyUI install -- no 'input' and 'models' subfolders were found there. " +
                "Exports and file copies will likely fail until this is corrected.\n\nSave anyway?",
                "Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        _settings.ComfyUiRootFolder = root;

        foreach (var row in _modelRows)
        {
            var value = row.OverrideFilename.Trim();
            if (value.Length == 0) _settings.ModelOverrides.Remove(row.Key);
            else _settings.ModelOverrides[row.Key] = value;
        }

        SettingsStore.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
