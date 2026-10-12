using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;

namespace DiskCleaner.App.ViewModels;

/// <summary>Строка таблицы Firefox: один тип данных одного сайта (например, IndexedDB у tiktok.com).</summary>
public sealed class FirefoxPartRowViewModel : ObservableObject
{
    private bool _isSelected;

    public FirefoxPartRowViewModel(FirefoxSiteEntry site, FirefoxStoragePart part)
    {
        Site = site;
        Part = part;
    }

    public FirefoxSiteEntry Site { get; }
    public FirefoxStoragePart Part { get; }

    public string SiteName => Site.DisplayName;
    public string KindTitle => FirefoxStorageLayout.Find(Part.FolderName)?.Title ?? Part.FolderName;
    public long SizeBytes => Part.SizeBytes;
    public string SizeDisplay => Part.SizeDisplay;
    public string LastModifiedDisplay => Part.LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";
    public string Note => Part.Note;
    public bool IsSuggested => Part.IsSuggested;
    public string SuggestedDisplay => Part.IsSuggested ? "Да" : "";

    public FirefoxStorageRisk Risk => Part.Risk;
    public string RiskDisplay => Part.Risk switch
    {
        FirefoxStorageRisk.Low => "Низкий",
        FirefoxStorageRisk.Medium => "Средний",
        _ => "Высокий",
    };

    /// <summary>Данные расширений удалять нельзя — галочка недоступна.</summary>
    public bool CanSelect => Site.CanDelete;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value && CanSelect);
    }
}
