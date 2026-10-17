using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class CleanupServiceTests
{
    private static CleanupService CreateService(out PathSafetyService safety)
    {
        var reparseGuard = new ReparsePointGuard();
        safety = new PathSafetyService(reparseGuard);
        var sizeCalculator = new SizeCalculator(reparseGuard);
        return new CleanupService(safety, reparseGuard, sizeCalculator);
    }

    private static CleanupTarget MakeTarget(string fullPath, string[]? fileNamePatterns = null) => new()
    {
        RuleName = "Test rule",
        Category = CleanupCategory.UserTemp,
        DisplayName = "Test target",
        FullPath = fullPath,
        DriveLetter = Path.GetPathRoot(fullPath)!.TrimEnd('\\', ':'),
        Description = "test",
        RiskLevel = CleanupRiskLevel.Safe,
        FileNamePatterns = fileNamePatterns,
        IsSafeToClean = true,
        SizeBytes = 0,
    };

    [Fact]
    public void Deletes_only_content_not_the_root_folder()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");
        temp.CreateSubDirectory("sub");
        temp.CreateFile("sub/b.txt", "67890");

        var service = CreateService(out _);
        var result = service.Clean([MakeTarget(temp.Path)], dryRun: false, progress: null, CancellationToken.None);

        Assert.True(Directory.Exists(temp.Path), "Корневая папка-цель не должна удаляться.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.Equal(2, result.TotalFilesDeleted);
    }

    [Fact]
    public void Reports_correct_freed_bytes()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", new string('x', 100));
        temp.CreateFile("b.txt", new string('y', 50));

        var service = CreateService(out _);
        var result = service.Clean([MakeTarget(temp.Path)], dryRun: false, progress: null, CancellationToken.None);

        Assert.Equal(150, result.TotalFreedBytes);
    }

    [Fact]
    public void Skips_locked_file_without_throwing()
    {
        using var temp = new TempTestDirectory();
        var lockedFile = temp.CreateFile("locked.txt", "content");

        using var handle = File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        // FileShare.Read всё ещё запрещает удаление на Windows, пока хендл открыт.

        var service = CreateService(out _);
        var exception = Record.Exception(() =>
            service.Clean([MakeTarget(temp.Path)], dryRun: false, progress: null, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void Rejects_target_that_fails_second_safety_check()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");

        var service = CreateService(out _);
        // Путь-цель, который заведомо не пройдёт PathSafetyService (не в whitelist).
        var unsafeTarget = MakeTarget(Path.Combine(AppContext.BaseDirectory, "NotWhitelisted_" + Guid.NewGuid()));
        Directory.CreateDirectory(unsafeTarget.FullPath);
        try
        {
            var result = service.Clean([unsafeTarget], dryRun: false, progress: null, CancellationToken.None);

            Assert.Single(result.Entries);
            Assert.NotEmpty(result.Entries[0].Errors);
            Assert.True(Directory.Exists(unsafeTarget.FullPath));
        }
        finally
        {
            Directory.Delete(unsafeTarget.FullPath, recursive: true);
        }
    }

    [Fact]
    public void FileNamePatterns_deletes_only_matching_files()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("thumbcache_001.db", "12345");
        temp.CreateFile("keep.txt", "should remain");

        var service = CreateService(out _);
        var result = service.Clean(
            [MakeTarget(temp.Path, fileNamePatterns: ["thumbcache_*.db"])],
            dryRun: false, progress: null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(temp.Path, "thumbcache_001.db")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "keep.txt")));
        Assert.Equal(1, result.TotalFilesDeleted);
    }
}
