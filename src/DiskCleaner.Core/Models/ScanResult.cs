namespace DiskCleaner.Core.Models;

/// <summary>Итог одного прохода сканирования по выбранным дискам.</summary>
public sealed class ScanResult
{
    public List<CleanupTarget> Targets { get; init; } = [];
    public List<SkippedItem> SkippedItems { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> DrivesScanned { get; init; } = [];
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime FinishedAtUtc { get; set; } = DateTime.UtcNow;
    public bool WasCancelled { get; set; }

    public long TotalReclaimableBytes => Targets.Where(t => t.IsSafeToClean && !t.IsBlockedNoAdmin).Sum(t => t.SizeBytes);
}
