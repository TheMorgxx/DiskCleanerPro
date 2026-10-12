namespace DiskCleaner.Core.Models;

public enum SkipReason
{
    ReparsePoint,
    AccessDenied,
    IOError,
    UnsafePath,
    RequiresAdminNotElevated,
}

/// <summary>Один пропущенный при сканировании или удалении элемент — файл, папка или reparse point.</summary>
public sealed class SkippedItem
{
    public required string Path { get; init; }
    public required SkipReason Reason { get; init; }
    public string? Detail { get; init; }
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;

    public override string ToString() => $"{Reason}: {Path}" + (Detail is null ? "" : $" ({Detail})");
}
