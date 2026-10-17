using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Единая точка получения whitelist-правил (агрегирует WindowsSystemCleanupService,
/// AppCacheDetector, BrowserCacheDetector) И разворачивания их в реальные
/// CleanupTarget на диске. Чтобы добавить новую категорию очистки: допишите
/// новый CleanupRule в один из трёх детекторов (или прямо здесь, в
/// GetAdditionalRules) — резолвинг, проверка безопасности и подсчёт размера
/// подхватят его автоматически, без изменений где-либо ещё.
/// </summary>
public sealed class CleanupRuleProvider
{
    private readonly PathSafetyService _safety;
    private readonly SizeCalculator _sizeCalculator;
    private readonly ReparsePointGuard _reparseGuard;
    private readonly WindowsSystemCleanupService _windowsService = new();
    private readonly AppCacheDetector _appDetector = new();
    private readonly BrowserCacheDetector _browserDetector = new();

    public CleanupRuleProvider(PathSafetyService safety, SizeCalculator sizeCalculator, ReparsePointGuard reparseGuard)
    {
        _safety = safety;
        _sizeCalculator = sizeCalculator;
        _reparseGuard = reparseGuard;
    }

    /// <summary>Полный whitelist всех известных программе категорий очистки.</summary>
    public List<CleanupRule> GetBuiltInRules()
    {
        var rules = new List<CleanupRule>();
        rules.AddRange(_windowsService.GetRules());
        rules.AddRange(_browserDetector.GetRules());
        rules.AddRange(_appDetector.GetRules());
        return rules;
    }

    /// <summary>
    /// Разворачивает правила в реальные найденные цели на выбранных дисках.
    /// UserProfile- и SystemDriveOnly-правила учитываются, только если
    /// системный диск входит в <paramref name="selectedDriveRoots"/> — если
    /// пользователь анализирует только D:, кэши профиля на C: не показываются,
    /// что соответствует явному выбору пользователя, какие диски трогать.
    /// </summary>
    public ScanResult ResolveTargets(
        IEnumerable<CleanupRule> rules,
        IReadOnlyCollection<string> selectedDriveRoots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new ScanResult { DrivesScanned = selectedDriveRoots.ToList() };
        var systemDrive = NormalizeDriveRoot(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\");
        var normalizedSelected = selectedDriveRoots.Select(NormalizeDriveRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAdmin = AdminRightsChecker.IsRunningAsAdministrator();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Сканирую: {rule.Name}...");

                var driveRootsForRule = GetApplicableDriveRoots(rule, systemDrive, normalizedSelected);

                foreach (var driveRoot in driveRootsForRule)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidatePaths = rule.Kind == CleanupResolutionKind.FixedPath
                        ? ResolveFixedPath(rule, driveRoot)
                        : ResolveDynamicSearch(rule, driveRoot, result.SkippedItems, cancellationToken);

                    foreach (var path in candidatePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!Directory.Exists(path))
                            continue;

                        var normalizedPath = PathSafetyService.Normalize(path);
                        if (!seenPaths.Add(normalizedPath))
                            continue; // уже найдено другим (более специфичным) правилом

                        var target = BuildTarget(rule, driveRoot, path, isAdmin);

                        if (target.IsSafeToClean)
                        {
                            var stats = _sizeCalculator.ComputeStats(path, rule.FileNamePatterns, result.SkippedItems);
                            target.SizeBytes = stats.SizeBytes;
                            target.FileCount = stats.FileCount;
                            target.LastModifiedUtc = stats.LastModifiedUtc;

                            // Пустые безопасные категории не показываем — нечего предложить пользователю.
                            if (target.SizeBytes == 0 && target.FileCount == 0 && !target.IsBlockedNoAdmin)
                                continue;
                        }

                        result.Targets.Add(target);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }

        result.FinishedAtUtc = DateTime.UtcNow;
        return result;
    }

    private static List<string> GetApplicableDriveRoots(CleanupRule rule, string systemDrive, HashSet<string> selectedDrives)
    {
        switch (rule.DriveScope)
        {
            case RuleDriveScope.UserProfile:
                // Профильные категории физически живут на системном диске —
                // показываем их, только если пользователь включил этот диск в анализ.
                return selectedDrives.Contains(systemDrive) ? [systemDrive] : [];

            case RuleDriveScope.SystemDriveOnly:
                return selectedDrives.Contains(systemDrive) ? [systemDrive] : [];

            case RuleDriveScope.PerSelectedDrive:
                return selectedDrives.ToList();

            default:
                return [];
        }
    }

    private CleanupTarget BuildTarget(CleanupRule rule, string driveRoot, string resolvedPath, bool isAdmin)
    {
        // Безопасность проверяется здесь и ПОВТОРНО в CleanupService прямо перед удалением.
        var isSafe = _safety.IsPathSafeForContentDeletion(resolvedPath, out var reason);

        var target = new CleanupTarget
        {
            RuleName = rule.Name,
            Category = rule.Category,
            AppName = rule.AppName,
            DisplayName = BuildDisplayName(rule, resolvedPath),
            FullPath = resolvedPath,
            DriveLetter = driveRoot.TrimEnd('\\', ':'),
            Description = rule.Description,
            RiskLevel = rule.RiskLevel,
            RequiresAdmin = rule.RequiresAdmin,
            FileNamePatterns = rule.FileNamePatterns,
            IsSafeToClean = isSafe,
            UnsafeReason = reason,
        };

        target.IsBlockedNoAdmin = rule.RequiresAdmin && !isAdmin;
        return target;
    }

    private static string BuildDisplayName(CleanupRule rule, string resolvedPath)
    {
        if (rule.Kind == CleanupResolutionKind.FixedPath)
            return rule.Name;

        return $"{rule.Name} ({ShortenPath(resolvedPath)})";
    }

    private static string ShortenPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Length <= 3 ? path : string.Join(Path.DirectorySeparatorChar, parts[^3..]);
    }

    private static string NormalizeDriveRoot(string driveRoot)
    {
        var trimmed = driveRoot.TrimEnd('\\');
        return trimmed + "\\";
    }

    private static List<string> ResolveFixedPath(CleanupRule rule, string driveRoot)
    {
        var withDrive = rule.BasePathTemplate.Replace("{drive}", driveRoot);
        var expanded = Environment.ExpandEnvironmentVariables(withDrive);
        return [expanded];
    }

    private List<string> ResolveDynamicSearch(CleanupRule rule, string driveRoot, List<SkippedItem> skippedItems, CancellationToken ct)
    {
        var withDrive = rule.BasePathTemplate.Replace("{drive}", driveRoot);
        var basePath = Environment.ExpandEnvironmentVariables(withDrive);
        var found = new List<string>();

        if (!Directory.Exists(basePath))
            return found;

        foreach (var pattern in rule.SubPathPatterns)
        {
            ct.ThrowIfCancellationRequested();
            var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            SearchRecursive(basePath, segments, 0, rule.MaxSearchDepth, found, skippedItems, ct);
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SearchRecursive(string currentDir, string[] segments, int segmentIndex, int maxDepth,
        List<string> found, List<SkippedItem> skippedItems, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (maxDepth <= 0 || segmentIndex >= segments.Length)
            return;

        // Никогда не заходим внутрь symlink/junction при поиске — тот же принцип,
        // что и при подсчёте размера.
        if (_reparseGuard.IsReparsePoint(currentDir))
        {
            skippedItems.Add(new SkippedItem { Path = currentDir, Reason = SkipReason.ReparsePoint });
            return;
        }

        if (!Directory.Exists(currentDir))
            return;

        var segment = segments[segmentIndex];
        var isLastSegment = segmentIndex == segments.Length - 1;

        IEnumerable<string> candidates;
        try
        {
            candidates = segment == "*"
                ? Directory.EnumerateDirectories(currentDir).ToList()
                : Directory.EnumerateDirectories(currentDir, segment).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skippedItems.Add(new SkippedItem { Path = currentDir, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            return;
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (_reparseGuard.IsReparsePoint(candidate))
            {
                skippedItems.Add(new SkippedItem { Path = candidate, Reason = SkipReason.ReparsePoint });
                continue;
            }

            if (isLastSegment)
            {
                if (segment == "*")
                    continue; // "*" на последнем месте не задаёт конкретную цель — пропускаем
                found.Add(candidate);
            }
            else
            {
                SearchRecursive(candidate, segments, segmentIndex + 1, maxDepth - 1, found, skippedItems, ct);
            }
        }

        // Для литеральных (не "*") сегментов дополнительно ищем совпадения и
        // глубже во вложенных папках — так находятся, например, кэши
        // Chromium-браузеров на произвольной глубине вложенности профиля.
        if (segment != "*" && maxDepth > 1)
        {
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var subDir in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (_reparseGuard.IsReparsePoint(subDir))
                {
                    skippedItems.Add(new SkippedItem { Path = subDir, Reason = SkipReason.ReparsePoint });
                    continue;
                }
                SearchRecursive(subDir, segments, segmentIndex, maxDepth - 1, found, skippedItems, ct);
            }
        }
    }
}
