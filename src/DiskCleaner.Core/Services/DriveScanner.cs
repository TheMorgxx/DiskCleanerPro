using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>Перечисляет доступные для сканирования локальные диски — источник данных для первого экрана.</summary>
public sealed class DriveScanner
{
    private readonly DiskUsageScanner _usageScanner;

    public DriveScanner(DiskUsageScanner usageScanner)
    {
        _usageScanner = usageScanner;
    }

    /// <summary>Возвращает все готовые (IsReady) локальные и съёмные диски, отсортированные по букве.</summary>
    public List<DriveInfoModel> GetAvailableDrives()
    {
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var result = new List<DriveInfoModel>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return result;
        }

        foreach (var drive in drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!drive.IsReady)
                continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            result.Add(_usageScanner.BuildModel(drive, systemDrive));
        }

        return result;
    }
}
