using DiskCleaner.Core.Models;

namespace DiskCleaner.App.ViewModels;

public sealed class CleanupResultViewModel
{
    public CleanupResult Model { get; }

    public CleanupResultViewModel(CleanupResult model)
    {
        Model = model;
    }

    public bool DryRun => Model.DryRun;
    public bool WasCancelled => Model.WasCancelled;
    public string TotalFreedDisplay => Model.TotalFreedDisplay;
    public int TotalFilesDeleted => Model.TotalFilesDeleted;
    public int TotalFilesSkipped => Model.TotalFilesSkipped;
    public List<string> Errors => Model.AllErrors;
    public List<SkippedItem> SkippedItems => Model.AllSkippedItems;
    public List<CleanupLogEntry> Entries => Model.Entries;

    public string HeaderText => DryRun
        ? $"Тестовый прогон завершён{(WasCancelled ? " (отменено)" : "")}. Было бы освобождено: {TotalFreedDisplay}"
        : $"Очистка завершена{(WasCancelled ? " (отменено)" : "")}. Освобождено: {TotalFreedDisplay}";
}
