namespace DiskCleaner.Core.Models;

/// <summary>Информация об одном локальном диске для первого экрана программы.</summary>
public sealed class DriveInfoModel
{
    /// <summary>Корень диска с обратным слешем, например "C:\".</summary>
    public required string RootPath { get; init; }

    public string Letter => RootPath.TrimEnd('\\', ':');
    public string? VolumeLabel { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public string? FileSystem { get; init; }
    public string DriveTypeDisplay { get; init; } = "";
    public bool IsSystemDrive { get; init; }
    public bool IsReady { get; init; } = true;

    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string TotalDisplay => CleanupTarget.FormatSize(TotalBytes);
    public string FreeDisplay => CleanupTarget.FormatSize(FreeBytes);
    public string UsedDisplay => CleanupTarget.FormatSize(UsedBytes);
}
