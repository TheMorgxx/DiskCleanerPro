using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Сквозные проверки: исключение внутри цели очистки (B) и список исключений, который меняется после
/// создания сервисов (C), доходят до <see cref="CleanupService"/> и защищают файлы на диске.
/// </summary>
public sealed class CleanupExclusionTests
{
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
        SizeBytes = 0,
    };

    private static CleanupService CreateService(Func<IEnumerable<string>> excludedProvider)
    {
        var reparseGuard = new ReparsePointGuard();
        var safety = new PathSafetyService(reparseGuard, excludedProvider);
        return new CleanupService(safety, reparseGuard, new SizeCalculator(reparseGuard));
    }

    [Fact]
    public void Clean_does_not_touch_target_that_contains_excluded_subfolder()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("target");
        var other = temp.CreateFile("target/other.txt");
        var keepFile = temp.CreateFile("target/keep/important.txt");
        var keep = Path.Combine(target, "keep");
        var service = CreateService(() => new[] { keep });

        var result = service.Clean([MakeTarget(target)], dryRun: false, progress: null, CancellationToken.None);

        Assert.True(File.Exists(keepFile), "Файл в исключённой подпапке должен остаться.");
        Assert.True(File.Exists(other), "Цель блокируется целиком — остальное содержимое тоже не удаляется.");
        Assert.Single(result.Entries);
        Assert.Equal(0, result.Entries[0].FilesDeleted);
        Assert.NotEmpty(result.Entries[0].Errors);
        Assert.Contains("списка исключений", result.Entries[0].Errors[0]);
    }

    [Fact]
    public void Exclusion_added_after_services_were_created_protects_target_on_next_clean()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("target");
        var file = temp.CreateFile("target/a.txt");
        var excluded = new List<string>();
        var service = CreateService(() => excluded);

        // Пользователь добавляет исключение в «Настройках» уже после создания сервисов — без перезапуска.
        excluded.Add(target);
        var result = service.Clean([MakeTarget(target)], dryRun: false, progress: null, CancellationToken.None);

        Assert.True(File.Exists(file));
        Assert.Single(result.Entries);
        Assert.NotEmpty(result.Entries[0].Errors);
    }
}