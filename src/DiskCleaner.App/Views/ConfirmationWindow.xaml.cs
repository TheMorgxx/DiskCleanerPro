using System.Windows;
using System.Windows.Controls;

namespace DiskCleaner.App.Views;

public partial class ConfirmationWindow : Window
{
    private readonly string? _requiredWord;

    public ConfirmationWindow(string message, string? requiredWord = null)
    {
        _requiredWord = string.IsNullOrWhiteSpace(requiredWord) ? null : requiredWord.Trim();
        InitializeComponent();
        MessageText.Text = message;
        if (_requiredWord is not null)
        {
            RequiredWordPanel.Visibility = Visibility.Visible;
            RequiredWordPrompt.Text = $"Для подтверждения введите «{_requiredWord}» (без учёта регистра):";
            Loaded += (_, _) => RequiredWordTextBox.Focus();
        }
        ConfirmButton.IsEnabled = IsWordConfirmed();
    }

    private bool IsWordConfirmed() => _requiredWord is null ||
        string.Equals(RequiredWordTextBox.Text.Trim(), _requiredWord, StringComparison.OrdinalIgnoreCase);

    private void OnRequiredWordChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = IsWordConfirmed();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (IsWordConfirmed())
            DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
