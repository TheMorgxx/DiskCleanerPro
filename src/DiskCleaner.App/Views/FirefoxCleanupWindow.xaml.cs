using System.Windows;
using DiskCleaner.App.ViewModels;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.Views;

public partial class FirefoxCleanupWindow : Window
{
    private readonly FirefoxCleanupViewModel _viewModel;

    public FirefoxCleanupWindow(FirefoxCleanupViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _viewModel.ConfirmDeletion = ShowConfirmationDialog;
        _viewModel.ShowResults = ShowResultsDialog;
        DataContext = _viewModel;

        // Закрытие окна останавливает фоновый анализ/удаление.
        Closed += (_, _) => _viewModel.Shutdown();
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
}
