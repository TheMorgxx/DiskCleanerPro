using System.Collections.ObjectModel;
using System.Windows;

namespace DiskCleaner.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _excludedPaths;

    public SettingsWindow(ObservableCollection<string> excludedPaths)
    {
        InitializeComponent();
        _excludedPaths = excludedPaths;
        DataContext = _excludedPaths;
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
