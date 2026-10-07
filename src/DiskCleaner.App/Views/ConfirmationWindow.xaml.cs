using System.Windows;

namespace DiskCleaner.App.Views;

public partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
