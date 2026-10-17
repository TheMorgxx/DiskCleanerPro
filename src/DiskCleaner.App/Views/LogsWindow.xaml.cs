using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

namespace DiskCleaner.App.Views;

public partial class LogsWindow : Window
{
    private readonly string _logDirectory;

    public LogsWindow(ObservableCollection<string> logLines, string logDirectory)
    {
        InitializeComponent();
        _logDirectory = logDirectory;
        LogListBox.ItemsSource = logLines;
        HeaderTextBlock.Text = $"Записи текущей сессии (полный журнал по датам хранится в: {logDirectory})";
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_logDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Не удалось открыть проводник. Логи находятся в:\n{_logDirectory}",
                "Открыть папку с логами", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
