using System.Windows;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.Views;

/// <summary>Элементы, пропущенные при последнем сканировании (снимок списка на момент открытия окна).</summary>
public partial class SkippedItemsWindow : Window
{
    public SkippedItemsWindow(IReadOnlyList<SkippedItem> items)
    {
        InitializeComponent();
        HeaderText.Text = $"Пропущено при сканировании: {items.Count}";
        ItemsList.ItemsSource = items;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
