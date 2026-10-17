using System.Windows;
using DiskCleaner.App.ViewModels;

namespace DiskCleaner.App.Views;

public partial class ResultsWindow : Window
{
    public ResultsWindow(CleanupResultViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
