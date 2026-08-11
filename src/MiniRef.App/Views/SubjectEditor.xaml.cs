using System.Windows;
using System.Windows.Controls;
using MiniRef.App.ViewModels;
using MiniRef.Core.Models;

namespace MiniRef.App.Views;

public partial class SubjectEditor : UserControl
{
    public SubjectEditor() => InitializeComponent();

    public static readonly DependencyProperty DisplayNumberProperty = DependencyProperty.Register(
        nameof(DisplayNumber), typeof(int), typeof(SubjectEditor), new PropertyMetadata(0));

    /// <summary>The 1-based "&lt;Subject N&gt;" number, set from MainWindow.xaml where the
    /// AlternationIndex binding still correctly reaches the ItemsControl's real item
    /// container -- doing that lookup from inside this UserControl's own XAML doesn't
    /// work, since the UserControl's own default ContentControl template introduces an
    /// extra ContentPresenter that the "nearest ancestor" search finds first.</summary>
    public int DisplayNumber
    {
        get => (int)GetValue(DisplayNumberProperty);
        set => SetValue(DisplayNumberProperty, value);
    }

    private Subject? Subject => DataContext as Subject;

    private MainViewModel? MainViewModel =>
        Application.Current.MainWindow?.DataContext as MainViewModel;

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) s.IsExpanded = !s.IsExpanded;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) MainViewModel?.RemoveSubjectCommand.Execute(s);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) MainViewModel?.MoveSubjectUpCommand.Execute(s);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Subject is { } s) MainViewModel?.MoveSubjectDownCommand.Execute(s);
    }
}
