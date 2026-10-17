using System.Collections.ObjectModel;
using System.Windows;

namespace DiskCleaner.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _excludedPaths;
    private readonly ObservableCollection<string> _projectFolders;

    public SettingsWindow(ObservableCollection<string> excludedPaths, ObservableCollection<string> projectFolders)
    {
        InitializeComponent();
        _excludedPaths = excludedPaths;
        _projectFolders = projectFolders;
        DataContext = _excludedPaths;
        ProjectFoldersListBox.ItemsSource = _projectFolders;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var path = NewPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        if (!_excludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _excludedPaths.Add(path);

        NewPathTextBox.Clear();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ExcludedPathsListBox.SelectedItem is string selected)
            _excludedPaths.Remove(selected);
    }

    private void OnAddProjectClick(object sender, RoutedEventArgs e)
    {
        var path = NewProjectPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        if (!_projectFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            _projectFolders.Add(path);

        NewProjectPathTextBox.Clear();
    }

    private void OnRemoveProjectClick(object sender, RoutedEventArgs e)
    {
        if (ProjectFoldersListBox.SelectedItem is string selected)
            _projectFolders.Remove(selected);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
