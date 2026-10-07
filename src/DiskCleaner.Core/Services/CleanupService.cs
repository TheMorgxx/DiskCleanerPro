using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

public sealed class CleanupService
{
    private readonly PathSafetyService _safety;
    private readonly ReparsePointGuard _reparseGuard;
    private readonly SizeCalculator _sizeCalculator;

    public CleanupService(PathSafetyService safety, ReparsePointGuard reparseGuard, SizeCalculator sizeCalculator)
    {
        _safety = safety;
        _reparseGuard = reparseGuard;
        _sizeCalculator = sizeCalculator;
    }

    /// <summary>
    /// Удаляет СОДЕРЖИМОЕ каждой выбранной цели (сама папка-цель никогда не
    /// удаляется). Каждый путь повторно проверяется через PathSafetyService
    /// непосредственно перед удалением. При dryRun=true ничего не удаляется —
    /// только считается, что было бы удалено, и это явно отражается в
    /// CleanupLogEntry.DryRun.
    /// </summary>
    public CleanupResult Clean(
        IEnumerable<CleanupTarget> selectedTargets,
        bool dryRun,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new CleanupResult { DryRun = dryRun };

        try
        {
            foreach (var target in selectedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((dryRun ? "[DRY-RUN] " : "") + $"Обрабатываю: {target.DisplayName}");

                result.Entries.Add(CleanOne(target, dryRun, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }

        return result;
    }

    private CleanupLogEntry CleanOne(CleanupTarget target, bool dryRun, CancellationToken cancellationToken)
    {
        // Повторная, ОБЯЗАТЕЛЬНАЯ проверка непосредственно перед удалением —
        // на случай, если между сканированием и подтверждением что-то изменилось.
        if (!_safety.IsPathSafeForContentDeletion(target.FullPath, out var reason))
        {
            return new CleanupLogEntry
            {
                TargetDisplayName = target.DisplayName,
                Path = target.FullPath,
                SizeBeforeBytes = target.SizeBytes,
                SizeAfterBytes = target.SizeBytes,
                DryRun = dryRun,
                Errors = [$"Путь не прошёл повторную проверку безопасности: {reason}. Пропущено."],
            };
        }

        var skipped = new List<SkippedItem>();
        var errors = new List<string>();
        var beforeStats = _sizeCalculator.ComputeStats(target.FullPath, target.FileNamePatterns, skipped);
        var sizeBefore = beforeStats.SizeBytes;

        int filesDeleted = 0;

        if (!dryRun)
        {
            filesDeleted = target.FileNamePatterns is { Length: > 0 }
                ? DeleteMatchingFilesOnly(target.FullPath, target.FileNamePatterns, skipped, errors, cancellationToken)
                : DeleteDirectoryContentsOnly(target.FullPath, skipped, errors, cancellationToken);
        }

        var afterSkipped = new List<SkippedItem>();
        DirectoryStats afterStats = dryRun
            ? beforeStats
            : _sizeCalculator.ComputeStats(target.FullPath, target.FileNamePatterns, afterSkipped);
        var sizeAfter = afterStats.SizeBytes;

        var filesSkippedCount = skipped.Count;

        return new CleanupLogEntry
        {
            TargetDisplayName = target.DisplayName,
            Path = target.FullPath,
            SizeBeforeBytes = sizeBefore,
            SizeAfterBytes = sizeAfter,
            FilesDeleted = filesDeleted,
            FilesSkipped = filesSkippedCount,
            DryRun = dryRun,
            SkippedItems = skipped,
            Errors = errors,
        };
    }

    /// <summary>
    /// Удаляет только файлы и подпапки ВНУТРИ directoryPath, саму папку
    /// directoryPath не трогает. Никогда не заходит внутрь и не удаляет
    /// содержимое symlink/junction — такие элементы пропускаются целиком (сам
    /// reparse point тоже не удаляется, только логируется как skipped, из
    /// соображений максимальной осторожности). Заблокированные/недоступные
    /// файлы пропускаются без падения — ошибка добавляется в errors.
    /// </summary>
    private int DeleteDirectoryContentsOnly(string directoryPath, List<SkippedItem> skipped, List<string> errors, CancellationToken ct)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        int deletedCount = 0;

        IEnumerable<string> files;
        IEnumerable<string> subDirs;
        try
        {
            files = Directory.EnumerateFiles(directoryPath).ToList();
            subDirs = Directory.EnumerateDirectories(directoryPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errors.Add($"Не удалось прочитать содержимое {directoryPath}: {ex.Message}");
            return 0;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (_reparseGuard.IsReparsePoint(file))
            {
                skipped.Add(new SkippedItem { Path = file, Reason = SkipReason.ReparsePoint });
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);

                File.Delete(file);
                deletedCount++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(new SkippedItem { Path = file, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            }
        }

        foreach (var subDir in subDirs)
        {
            ct.ThrowIfCancellationRequested();

            if (_reparseGuard.IsReparsePoint(subDir))
            {
                // Ключевое правило: НЕ удаляем содержимое symlink/junction и не
                // трогаем сам reparse point — только логируем как пропущенный.
                skipped.Add(new SkippedItem { Path = subDir, Reason = SkipReason.ReparsePoint });
                continue;
            }

            try
            {
                deletedCount += CountFilesRecursively(subDir);
                Directory.Delete(subDir, recursive: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                errors.Add($"Не удалось удалить папку целиком, чищу частично: {subDir} ({ex.Message})");
                deletedCount += DeleteDirectoryContentsOnly(subDir, skipped, errors, ct);
            }
        }

        return deletedCount;
    }

    private int DeleteMatchingFilesOnly(string directoryPath, string[] fileNamePatterns, List<SkippedItem> skipped, List<string> errors, CancellationToken ct)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        int deletedCount = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directoryPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errors.Add($"Не удалось прочитать содержимое {directoryPath}: {ex.Message}");
            return 0;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);
            if (!fileNamePatterns.Any(p => SizeCalculator.MatchesWildcard(name, p)))
                continue;

            if (_reparseGuard.IsReparsePoint(file))
            {
                skipped.Add(new SkippedItem { Path = file, Reason = SkipReason.ReparsePoint });
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);

                File.Delete(file);
                deletedCount++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(new SkippedItem { Path = file, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            }
        }

        return deletedCount;
    }

    private int CountFilesRecursively(string path)
    {
        // Используется только для статистики "файлов удалено" перед быстрым
        // Directory.Delete(recursive:true) — сам подсчёт ничего не удаляет.
        // Не заходит в reparse points по тем же правилам, что и SizeCalculator.
        if (_reparseGuard.IsReparsePoint(path))
            return 0;

        int count = 0;
        try
        {
            count += Directory.EnumerateFiles(path).Count();
            foreach (var sub in Directory.EnumerateDirectories(path))
                count += CountFilesRecursively(sub);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Не удалось посчитать точно — не критично для статистики, продолжаем.
        }
        return count;
    }
}
