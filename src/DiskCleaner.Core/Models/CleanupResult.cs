namespace DiskCleaner.Core.Models;

/// <summary>Сводный итог операции очистки по всем выбранным целям — для экрана результатов.</summary>
public sealed class CleanupResult
{
    public bool DryRun { get; init; }
    public List<CleanupLogEntry> Entries { get; init; } = [];
    public bool WasCancelled { get; set; }

    public long TotalFreedBytes => Entries.Sum(e => e.FreedBytes);
    public int TotalFilesDeleted => Entries.Sum(e => e.FilesDeleted);
    public int TotalFilesSkipped => Entries.Sum(e => e.FilesSkipped);
    public List<SkippedItem> AllSkippedItems => Entries.SelectMany(e => e.SkippedItems).ToList();
    public List<string> AllErrors => Entries.SelectMany(e => e.Errors).ToList();
    public string TotalFreedDisplay => CleanupTarget.FormatSize(TotalFreedBytes);
}
