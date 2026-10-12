using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class DryRunTests
{
    private static CleanupService CreateService()
    {
        var reparseGuard = new ReparsePointGuard();
        var safety = new PathSafetyService(reparseGuard);
        var sizeCalculator = new SizeCalculator(reparseGuard);
        return new CleanupService(safety, reparseGuard, sizeCalculator);
    }

    private static CleanupTarget MakeTarget(string fullPath) => new()
    {
        RuleName = "Test rule",
        Category = CleanupCategory.UserTemp,
        DisplayName = "Test target",
        FullPath = fullPath,
        DriveLetter = Path.GetPathRoot(fullPath)!.TrimEnd('\\', ':'),
        Description = "test",
        RiskLevel = CleanupRiskLevel.Safe,
        IsSafeToClean = true,
    };

    [Fact]
    public void Dry_run_does_not_delete_any_files()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");
        temp.CreateFile("sub/b.txt", "67890");

        var service = CreateService();
        var result = service.Clean([MakeTarget(temp.Path)], dryRun: true, progress: null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "sub", "b.txt")));
        Assert.True(result.DryRun);
    }

    [Fact]
    public void Dry_run_reports_size_that_would_be_freed_without_freeing_it()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", new string('x', 200));

        var service = CreateService();
        var result = service.Clean([MakeTarget(temp.Path)], dryRun: true, progress: null, CancellationToken.None);

        // "До" и "после" совпадают, т.к. ничего реально не удалено, но
        // размер "до" корректно отражает то, что было бы освобождено.
        Assert.Single(result.Entries);
        Assert.Equal(200, result.Entries[0].SizeBeforeBytes);
        Assert.Equal(200, result.Entries[0].SizeAfterBytes);
        Assert.Equal(0, result.Entries[0].FreedBytes);
        Assert.Equal(0, result.TotalFilesDeleted);
    }

    [Fact]
    public void Dry_run_still_runs_safety_check()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "NotWhitelisted_" + Guid.NewGuid());
        Directory.CreateDirectory(candidate);
        try
        {
            var service = CreateService();
            var result = service.Clean([MakeTarget(candidate)], dryRun: true, progress: null, CancellationToken.None);

            Assert.NotEmpty(result.Entries[0].Errors);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }
}
