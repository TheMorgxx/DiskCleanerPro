using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

public readonly record struct DirectoryStats(long SizeBytes, int FileCount, DateTime? LastModifiedUtc);

/// <summary>
/// Считает размер и количество файлов внутри директории. НИКОГДА не заходит
/// внутрь symlink/junction (проверяется через ReparsePointGuard на каждом
/// шаге) — такие элементы просто добавляются в список пропущенных и не
/// учитываются в размере. Недоступные файлы/папки пропускаются, не роняя
/// подсчёт.
/// </summary>
public sealed class SizeCalculator
{
    private readonly ReparsePointGuard _reparseGuard;

    public SizeCalculator(ReparsePointGuard reparseGuard)
    {
        _reparseGuard = reparseGuard;
    }

    /// <summary>
    /// Считает статистику по содержимому <paramref name="rootPath"/>. Если
    /// <paramref name="fileNamePatterns"/> задано — учитываются только файлы,
    /// подходящие под один из wildcard-шаблонов (используется, например, для
    /// кэша миниатюр Windows), подпапки в этом случае не обходятся вовсе.
    /// </summary>
    public DirectoryStats ComputeStats(string rootPath, string[]? fileNamePatterns, List<SkippedItem>? skippedItems = null)
    {
        if (!Directory.Exists(rootPath))
            return new DirectoryStats(0, 0, null);

        // Сам rootPath — это уже проверенная PathSafetyService папка-категория
        // (не reparse point, см. шаг 2b), поэтому в неё заходить можно;
        // reparse-проверка применяется к КАЖДОЙ вложенной сущности ниже.
        if (fileNamePatterns is { Length: > 0 })
            return ComputeStatsForFilePattern(rootPath, fileNamePatterns, skippedItems);

        long size = 0;
        int count = 0;
        DateTime? lastModified = null;

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            IEnumerable<string> subDirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skippedItems?.Add(new SkippedItem { Path = dir, Reason = SkipReason.AccessDenied, Detail = ex.Message });
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    if (_reparseGuard.IsReparsePoint(file))
                    {
                        skippedItems?.Add(new SkippedItem { Path = file, Reason = SkipReason.ReparsePoint });
                        continue;
                    }

                    var info = new FileInfo(file);
                    size += info.Length;
                    count++;
                    if (lastModified is null || info.LastWriteTimeUtc > lastModified)
                        lastModified = info.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skippedItems?.Add(new SkippedItem { Path = file, Reason = SkipReason.AccessDenied, Detail = ex.Message });
                }
            }

            foreach (var sub in subDirs)
            {
                if (_reparseGuard.IsReparsePoint(sub))
                {
                    // Ключевое правило безопасности: НЕ заходим внутрь
                    // symlink/junction при подсчёте размера.
                    skippedItems?.Add(new SkippedItem { Path = sub, Reason = SkipReason.ReparsePoint });
                    continue;
                }
                stack.Push(sub);
            }
        }

        return new DirectoryStats(size, count, lastModified);
    }

    private DirectoryStats ComputeStatsForFilePattern(string rootPath, string[] fileNamePatterns, List<SkippedItem>? skippedItems)
    {
        long size = 0;
        int count = 0;
        DateTime? lastModified = null;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skippedItems?.Add(new SkippedItem { Path = rootPath, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            return new DirectoryStats(0, 0, null);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!fileNamePatterns.Any(p => MatchesWildcard(name, p)))
                continue;

            try
            {
                if (_reparseGuard.IsReparsePoint(file))
                {
                    skippedItems?.Add(new SkippedItem { Path = file, Reason = SkipReason.ReparsePoint });
                    continue;
                }

                var info = new FileInfo(file);
                size += info.Length;
                count++;
                if (lastModified is null || info.LastWriteTimeUtc > lastModified)
                    lastModified = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skippedItems?.Add(new SkippedItem { Path = file, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            }
        }

        return new DirectoryStats(size, count, lastModified);
    }

    /// <summary>Простое сопоставление wildcard-шаблона ("*" и "?"), без regex и без LiteralPath-исключений.</summary>
    public static bool MatchesWildcard(string name, string pattern)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
