using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>Строит DriveInfoModel из системного System.IO.DriveInfo, безопасно обрабатывая недоступные диски.</summary>
public sealed class DiskUsageScanner
{
    public DriveInfoModel BuildModel(DriveInfo driveInfo, string? systemDriveRoot)
    {
        if (!driveInfo.IsReady)
        {
            return new DriveInfoModel
            {
                RootPath = driveInfo.Name,
                DriveTypeDisplay = driveInfo.DriveType.ToString(),
                IsReady = false,
            };
        }

        try
        {
            return new DriveInfoModel
            {
                RootPath = driveInfo.Name,
                VolumeLabel = SafeGet(() => driveInfo.VolumeLabel),
                TotalBytes = driveInfo.TotalSize,
                FreeBytes = driveInfo.AvailableFreeSpace,
                FileSystem = SafeGet(() => driveInfo.DriveFormat),
                DriveTypeDisplay = driveInfo.DriveType.ToString(),
                IsSystemDrive = systemDriveRoot is not null &&
                                string.Equals(driveInfo.Name, systemDriveRoot, StringComparison.OrdinalIgnoreCase),
                IsReady = true,
            };
        }
        catch (IOException)
        {
            return new DriveInfoModel
            {
                RootPath = driveInfo.Name,
                DriveTypeDisplay = driveInfo.DriveType.ToString(),
                IsReady = false,
            };
        }
    }

    private static string? SafeGet(Func<string> getter)
    {
        try { return getter(); }
        catch (IOException) { return null; }
    }
}
