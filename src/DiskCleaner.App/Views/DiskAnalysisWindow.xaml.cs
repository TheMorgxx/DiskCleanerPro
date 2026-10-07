using System.Runtime.InteropServices;
using System.Windows;
using DiskCleaner.App.ViewModels;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.Views;

public partial class DiskAnalysisWindow : Window
{
    private readonly DiskAnalysisViewModel _viewModel;

    public DiskAnalysisWindow(DiskAnalysisViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _viewModel.ConfirmDeletion = ShowConfirmationDialog;
        _viewModel.ConfirmSensitive = ShowConfirmationDialog;
        _viewModel.ShowResults = ShowResultsDialog;
        _viewModel.CopyToClipboard = CopyTextToClipboard;
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

    private static bool CopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            // Буфер обмена в этот момент занят другим приложением.
            return false;
        }
    }
}
