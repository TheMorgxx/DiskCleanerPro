using System.Windows;
using DiskCleaner.App.ViewModels;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel
        {
            ConfirmDeletion = ShowConfirmationDialog,
            ShowResults = ShowResultsDialog,
            ShowSettingsWindow = ShowSettingsDialog,
            ShowLogsWindow = ShowLogsDialog,
            ShowDiskAnalysisWindow = ShowDiskAnalysisDialog,
            ShowFirefoxWindow = ShowFirefoxDialog,
        };
        DataContext = _viewModel;
    }

    private bool ShowConfirmationDialog(string message)
    {
        var dialog = new ConfirmationWindow(message) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void ShowResultsDialog(CleanupResult result)
    {
        var window = new ResultsWindow(new CleanupResultViewModel(result)) { Owner = this };
        window.Show();
    }

    private void ShowSettingsDialog()
    {
        var window = new SettingsWindow(_viewModel.ExcludedPaths) { Owner = this };
        window.ShowDialog();
    }

    private void ShowLogsDialog()
    {
        var window = new LogsWindow(_viewModel.LogLines, _viewModel.LogDirectory) { Owner = this };
        window.Show();
    }

    private void ShowDiskAnalysisDialog()
    {
        var window = new DiskAnalysisWindow(_viewModel.CreateDiskAnalysisViewModel()) { Owner = this };
        window.Show();
    }

    private void ShowFirefoxDialog()
    {
        var window = new FirefoxCleanupWindow(_viewModel.CreateFirefoxViewModel()) { Owner = this };
        window.Show();
    }
}
