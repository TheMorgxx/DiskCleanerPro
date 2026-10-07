namespace DiskCleaner.Core.Models;

/// <summary>Одна запись в журнале очистки — результат обработки одной CleanupTarget.</summary>
public sealed class CleanupLogEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string TargetDisplayName { get; init; }
    public required string Path { get; init; }
    public long SizeBeforeBytes { get; init; }
    public long SizeAfterBytes { get; init; }
    public long FreedBytes => Math.Max(0, SizeBeforeBytes - SizeAfterBytes);
    public int FilesDeleted { get; init; }
    public int FilesSkipped { get; init; }
    public bool DryRun { get; init; }
    public List<SkippedItem> SkippedItems { get; init; } = [];
    public List<string> Errors { get; init; } = [];

    public override string ToString()
    {
        var mode = DryRun ? "DRY-RUN" : "CLEAN";
        var line = $"[{TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC] {mode} | {TargetDisplayName} | {Path} | " +
                   $"до={CleanupTarget.FormatSize(SizeBeforeBytes)} | " +
                   $"после={CleanupTarget.FormatSize(SizeAfterBytes)} | " +
                   $"освобождено={CleanupTarget.FormatSize(FreedBytes)} | " +
                   $"удалено файлов={FilesDeleted} | пропущено={FilesSkipped}";
        if (Errors.Count > 0)
            line += $" | ошибок={Errors.Count}: {string.Join("; ", Errors.Take(5))}" + (Errors.Count > 5 ? "..." : "");
        return line;
    }
}
