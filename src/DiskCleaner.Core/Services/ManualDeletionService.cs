using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Ручное удаление файлов и папок, которые пользователь сам выбрал (например, в результатах
/// анализа диска). Работает НЕ по whitelist, как <see cref="CleanupService"/> (там цели заранее
/// известны), а по набору жёстких запретов и подтверждений:
///
///   • блок: корень диска, Windows, Program Files, Корзина, служебные папки, сам профиль
///     пользователя, AppData и её корни, ключи DPAPI и реестровые файлы пользователя,
///     папка самой программы, пути из списка исключений и любая папка, внутри которой лежит
///     что-то из этого (иначе рекурсивное удаление снесло бы защищённое);
///   • подтверждение: личные файлы (Documents, Desktop, Downloads …) и данные внутри профиля
///     браузера (не кэш) — удаляются только при <see cref="ManualDeletionOptions.ConfirmedSensitive"/>;
///   • путь проверяется заново прямо перед удалением;
///   • symlink/junction внутри удаляемой папки удаляются как ссылка — цель ссылки не затрагивается.
///
/// Результат возвращается в тех же моделях, что и у основной очистки (<see cref="CleanupResult"/>),
/// поэтому его можно писать в тот же журнал через <see cref="Logger"/>.
/// </summary>
public sealed class ManualDeletionService
{
    private static readonly string[] ProtectedRootFileNames =
    {
        "hiberfil.sys", "pagefile.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log.tmp",
    };

    private static readonly string[] UserDataFolderNames =
    {
        "Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music", "OneDrive",
    };

    private static readonly string[] AppDataRootNames = { "Local", "Roaming", "LocalLow" };

    // Пути относительно профиля пользователя: DPAPI-ключи, сохранённые учётные данные, реестровый куст UsrClass.dat.
    private static readonly (string[] Parts, string Reason)[] ProfileProtectedSubtrees =
    {
        (new[] { "AppData", "Roaming", "Microsoft", "Protect" }, "ключи DPAPI — без них не расшифруются сохранённые пароли"),
        (new[] { "AppData", "Roaming", "Microsoft", "Crypto" }, "ключи шифрования пользователя"),
        (new[] { "AppData", "Roaming", "Microsoft", "SystemCertificates" }, "сертификаты пользователя"),
        (new[] { "AppData", "Roaming", "Microsoft", "Credentials" }, "сохранённые учётные данные Windows"),
        (new[] { "AppData", "Local", "Microsoft", "Credentials" }, "сохранённые учётные данные Windows"),
        (new[] { "AppData", "Local", "Microsoft", "Windows" }, "данные Windows пользователя (включая реестровый файл UsrClass.dat)"),
    };

    // Профили браузеров: путь + признак того, что внутри лежат данные пользователя.
    private static readonly string[][] BrowserProfileMarkers =
    {
        new[] { "Mozilla", "Firefox", "Profiles" },
        new[] { "Google", "Chrome", "User Data" },
        new[] { "Microsoft", "Edge", "User Data" },
        new[] { "BraveSoftware", "Brave-Browser", "User Data" },
    };

    // Имена папок, внутри которых лежит только кэш браузера — их удаление не затрагивает логины и данные сайтов.
    private static readonly HashSet<string> BrowserCacheFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "cache2", "code cache", "gpucache", "startupcache", "shadercache", "grshadercache", "dawncache",
    };

    private readonly ReparsePointGuard _guard;
    private readonly IRecycleBinMover _recycleBin;
    private readonly Func<IEnumerable<string>>? _excludedPathsProvider;
    private readonly string _userProfile;
    private readonly string _usersRoot;
    private readonly string _appBase;
    private readonly string? _appExecutable;
    private readonly string? _appPrefix;
    private readonly List<ProtectedPath> _subtrees;
    private readonly List<ProtectedPath> _exact;

    /// <param name="excludedPathsProvider">
    /// Вызывается при КАЖДОЙ проверке, поэтому изменения списка исключений действуют сразу, без перезапуска.
    /// </param>
    public ManualDeletionService(
        ReparsePointGuard guard,
        ManualDeletionPaths paths,
        Func<IEnumerable<string>>? excludedPathsProvider = null,
        IRecycleBinMover? recycleBin = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        ArgumentNullException.ThrowIfNull(paths);

        _recycleBin = recycleBin ?? new WindowsRecycleBinMover();
        _excludedPathsProvider = excludedPathsProvider;
        _userProfile = Normalize(paths.UserProfile);
        _usersRoot = Normalize(paths.UsersRoot);
        _appBase = Normalize(paths.AppBaseDirectory);
        _appExecutable = string.IsNullOrWhiteSpace(paths.AppExecutablePath) ? null : Normalize(paths.AppExecutablePath);
        _appPrefix = string.IsNullOrWhiteSpace(paths.AppFileNamePrefix) ? null : paths.AppFileNamePrefix;
        _subtrees = paths.ProtectedSubtrees.Select(p => new ProtectedPath(Normalize(p.Path), p.Reason)).ToList();
        _exact = paths.ProtectedExact.Select(p => new ProtectedPath(Normalize(p.Path), p.Reason)).ToList();
    }

    // ------------------------------------------------------------------ проверка

    /// <summary>Можно ли удалить путь, и нужно ли для этого отдельное подтверждение. Ничего не меняет на диске.</summary>
    public DeletionVerdict Evaluate(string path)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return DeletionVerdict.Blocked("путь не задан");
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return DeletionVerdict.Blocked($"не удалось разобрать путь: {ex.Message}");
        }

        // Корень диска проверяем до Exists, пока он ещё не «обрезан» до пустой строки/«C:».
        var driveRoot = Path.GetPathRoot(full);
        if (driveRoot is not null && string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
            return DeletionVerdict.Blocked("нельзя удалять корень диска");

        var normalized = TrimSeparators(full);
        var isDirectory = Directory.Exists(normalized);
        var isFile = !isDirectory && File.Exists(normalized);
        if (!isDirectory && !isFile)
            return DeletionVerdict.Blocked("путь не существует");

        // 1) Пользовательские исключения — читаем заново при каждой проверке.
        //    Если надёжно прочитать список не удалось — блокируем (лучше отказать, чем удалить исключённое).
        var excludedPaths = ReadExcludedPaths();
        if (excludedPaths is null)
            return DeletionVerdict.Blocked("не удалось прочитать список исключений — повтори через секунду");

        foreach (var excluded in excludedPaths)
        {
            if (IsUnderOrEqual(excluded, normalized))
                return DeletionVerdict.Blocked("путь добавлен пользователем в список исключений");
            if (IsUnderOrEqual(normalized, excluded))
                return DeletionVerdict.Blocked($"внутри лежит путь из списка исключений ({excluded})");
        }

        // 2) Сам путь — не symlink/junction.
        if (_guard.IsReparsePoint(normalized))
            return DeletionVerdict.Blocked("путь — symlink/junction (reparse point), пропущен из соображений безопасности");

        // 3) Сама программа: её папка, любая папка, внутри которой она лежит, и файлы программы.
        //    Остальное содержимое папки программы удалять можно (например, чужие файлы в Downloads).
        if (IsUnderOrEqual(normalized, _appBase))
            return DeletionVerdict.Blocked("это папка самой программы (или папка, в которой она лежит)");
        if (IsAppFile(normalized))
            return DeletionVerdict.Blocked("файл самой программы");

        // 4) Защищённые поддеревья (сам путь, всё внутри и любой предок).
        foreach (var p in _subtrees)
        {
            var verdict = CheckSubtree(normalized, p.Path, p.Reason);
            if (verdict is not null)
                return verdict;
        }

        // 5) Защищены только сами по себе.
        if (string.Equals(normalized, _usersRoot, StringComparison.OrdinalIgnoreCase))
            return DeletionVerdict.Blocked("папка с профилями пользователей");
        foreach (var p in _exact)
        {
            if (string.Equals(normalized, p.Path, StringComparison.OrdinalIgnoreCase))
                return DeletionVerdict.Blocked($"{p.Reason}");
        }

        // 6) Служебные файлы в корне диска (hiberfil.sys и т.п.).
        if (isFile && driveRoot is not null &&
            string.Equals(TrimSeparators(Path.GetDirectoryName(normalized) ?? ""), TrimSeparators(driveRoot), StringComparison.OrdinalIgnoreCase) &&
            ProtectedRootFileNames.Any(n => string.Equals(n, Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(normalized);
            var hint = string.Equals(name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                ? " Файл гибернации отключается командой «powercfg /h off» от администратора."
                : "";
            return DeletionVerdict.Blocked($"служебный файл Windows ({name}).{hint}");
        }

        // 7) Правила профиля пользователя.
        DeletionSensitivity sensitivity = DeletionSensitivity.None;
        string? note = null;
        foreach (var profile in ProfilesFor(normalized))
        {
            if (!IsUnderOrEqual(profile, normalized))
                continue;

            var segments = RelativeSegments(profile, normalized);

            if (segments.Length == 0)
                return DeletionVerdict.Blocked("папка профиля пользователя");

            if (segments.Length == 1 && isFile &&
                segments[0].StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase))
                return DeletionVerdict.Blocked("файл реестра пользователя (NTUSER)");

            if (Eq(segments[0], "AppData"))
            {
                if (segments.Length == 1)
                    return DeletionVerdict.Blocked("папка AppData целиком");
                if (segments.Length == 2 && AppDataRootNames.Any(n => Eq(n, segments[1])))
                    return DeletionVerdict.Blocked($"корневая папка AppData\\{segments[1]} целиком — удаляй конкретные вложенные папки");
            }

            foreach (var (parts, reason) in ProfileProtectedSubtrees)
            {
                var protectedPath = Path.Combine(new[] { profile }.Concat(parts).ToArray());
                var verdict = CheckSubtree(normalized, protectedPath, reason);
                if (verdict is not null)
                    return verdict;
            }

            if (UserDataFolderNames.Any(n => Eq(n, segments[0])))
            {
                if (segments.Length == 1)
                    return DeletionVerdict.Blocked($"папка «{segments[0]}» целиком — выбери конкретные файлы или вложенные папки");

                sensitivity = DeletionSensitivity.PersonalFiles;
                note = $"личные файлы в папке «{segments[0]}»";
            }
        }

        // 8) Данные внутри профиля браузера (кэш не считается).
        if (sensitivity == DeletionSensitivity.None && IsInsideBrowserProfileData(normalized))
        {
            sensitivity = DeletionSensitivity.BrowserProfile;
            note = "данные внутри профиля браузера (не кэш): можно потерять данные сайтов и сохранённые входы; " +
                   "для точечной очистки лучше использовать модуль Firefox";
        }

        return sensitivity == DeletionSensitivity.None
            ? DeletionVerdict.Allowed()
            : DeletionVerdict.AllowedWithConfirmation(sensitivity, note!);
    }

    private static DeletionVerdict? CheckSubtree(string normalized, string protectedPath, string reason)
    {
        if (IsUnderOrEqual(protectedPath, normalized))
            return DeletionVerdict.Blocked($"защищённая папка ({protectedPath}): {reason}");
        if (IsUnderOrEqual(normalized, protectedPath))
            return DeletionVerdict.Blocked($"внутри находится защищённая папка ({protectedPath}): {reason}");
        return null;
    }

    private IEnumerable<string> ProfilesFor(string normalized)
    {
        var profiles = new List<string> { _userProfile };

        if (IsUnderOrEqual(_usersRoot, normalized) && !string.Equals(_usersRoot, normalized, StringComparison.OrdinalIgnoreCase))
        {
            var first = RelativeSegments(_usersRoot, normalized).FirstOrDefault();
            if (first is not null)
                profiles.Add(Path.Combine(_usersRoot, first));
        }

        return profiles.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInsideBrowserProfileData(string normalized)
    {
        var segments = Segments(normalized);
        foreach (var marker in BrowserProfileMarkers)
        {
            for (var i = 0; i + marker.Length <= segments.Length; i++)
            {
                var matches = true;
                for (var j = 0; j < marker.Length && matches; j++)
                    matches = Eq(segments[i + j], marker[j]);
                if (!matches)
                    continue;

                var after = segments.Skip(i + marker.Length).ToArray();
                // Сам каталог «Profiles»/«User Data» без вложенных данных — не повод для предупреждения.
                if (after.Length == 0)
                    continue;
                if (after.Any(s => BrowserCacheFolderNames.Contains(s)))
                    return false;
                return true;
            }
        }

        return false;
    }

    private bool IsAppFile(string normalized)
    {
        if (_appExecutable is not null && string.Equals(normalized, _appExecutable, StringComparison.OrdinalIgnoreCase))
            return true;

        if (_appPrefix is null)
            return false;

        var parent = Path.GetDirectoryName(normalized);
        return parent is not null &&
               string.Equals(parent, _appBase, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(normalized).StartsWith(_appPrefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Читает список исключений. null — прочитать не удалось (например, коллекцию изменяли в другом потоке
    /// три раза подряд): вызывающий обязан считать это блокировкой, а не «исключений нет».
    /// </summary>
    private List<string>? ReadExcludedPaths()
    {
        if (_excludedPathsProvider is null)
            return new List<string>();

        List<string>? raw = null;
        for (var attempt = 0; attempt < 3 && raw is null; attempt++)
        {
            try
            {
                raw = _excludedPathsProvider().ToList();
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(10); // коллекцию изменили во время чтения — пробуем ещё раз
            }
        }

        if (raw is null)
            return null;

        var result = new List<string>();
        foreach (var item in raw)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item))
                    result.Add(Normalize(item));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // Некорректный путь в исключениях игнорируем.
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ удаление

    /// <summary>
    /// Удаляет выбранные пути. Каждый путь заново проходит <see cref="Evaluate"/> прямо перед удалением.
    /// Если выбрана и папка, и файл внутри неё, обрабатывается только папка. В режиме DryRun ничего не
    /// удаляется; запись показывает, сколько бы освободилось (SizeAfter = 0), FilesDeleted = 0.
    /// </summary>
    public CleanupResult Delete(
        IEnumerable<string> paths,
        ManualDeletionOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var result = new CleanupResult { DryRun = options.DryRun };

        try
        {
            foreach (var path in DropNestedSelections(paths))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((options.DryRun ? "[DRY-RUN] " : "") + $"Обрабатываю: {path}");
                result.Entries.Add(DeleteOne(path, options, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }

        return result;
    }

    private CleanupLogEntry DeleteOne(string path, ManualDeletionOptions options, CancellationToken ct)
    {
        var name = SafeFileName(path);
        var displayName = "Ручное удаление: " + name + (options.UseRecycleBin && !options.DryRun ? " [в корзину]" : "");

        // Повторная проверка непосредственно перед удалением.
        var verdict = Evaluate(path);
        if (!verdict.IsAllowed)
        {
            return new CleanupLogEntry
            {
                TargetDisplayName = displayName,
                Path = path,
                DryRun = options.DryRun,
                Errors = [$"Заблокировано: {verdict.BlockReason}. Пропущено."],
            };
        }

        if (verdict.RequiresConfirmation && !options.ConfirmedSensitive)
        {
            return new CleanupLogEntry
            {
                TargetDisplayName = displayName,
                Path = path,
                DryRun = options.DryRun,
                Errors = [$"Нужно подтверждение: {verdict.SensitivityNote}. Пропущено."],
            };
        }

        var normalized = TrimSeparators(Path.GetFullPath(path));
        var isDirectory = Directory.Exists(normalized);
        var (sizeBefore, filesBefore) = Measure(normalized, isDirectory, ct);

        if (options.DryRun)
        {
            // Пробный прогон показывает, сколько БЫ освободилось: SizeAfter = 0, FilesDeleted = 0 (ничего не удалено).
            return new CleanupLogEntry
            {
                TargetDisplayName = displayName,
                Path = normalized,
                SizeBeforeBytes = sizeBefore,
                SizeAfterBytes = 0,
                FilesDeleted = 0,
                DryRun = true,
            };
        }

        var skipped = new List<SkippedItem>();
        var errors = new List<string>();
        long deletedBytes = 0;
        var filesDeleted = 0;

        if (options.UseRecycleBin)
        {
            if (_recycleBin.TryMoveToRecycleBin(normalized, isDirectory, out var error))
            {
                deletedBytes = sizeBefore;
                filesDeleted = filesBefore;
            }
            else
            {
                errors.Add($"Не удалось отправить в корзину: {error}");
            }
        }
        else if (isDirectory)
        {
            filesDeleted = DeleteTree(normalized, skipped, errors, ref deletedBytes, ct);
        }
        else
        {
            if (TryDeleteFile(normalized, skipped, errors))
            {
                filesDeleted = 1;
                deletedBytes = sizeBefore;
            }
        }

        return new CleanupLogEntry
        {
            TargetDisplayName = displayName,
            Path = normalized,
            SizeBeforeBytes = sizeBefore,
            SizeAfterBytes = Math.Max(0, sizeBefore - deletedBytes),
            FilesDeleted = filesDeleted,
            FilesSkipped = skipped.Count,
            DryRun = false,
            SkippedItems = skipped,
            Errors = errors,
        };
    }

    /// <summary>
    /// Рекурсивно удаляет папку вместе с содержимым. Ссылки (symlink/junction) удаляются как сами ссылки —
    /// содержимое цели не затрагивается. Файлы, которые не удалось удалить (заняты, нет доступа), пропускаются
    /// и логируются; папка при этом остаётся.
    /// </summary>
    private int DeleteTree(string directory, List<SkippedItem> skipped, List<string> errors, ref long deletedBytes, CancellationToken ct)
    {
        var deleted = 0;
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Не удалось прочитать {directory}: {ex.Message}");
            return 0;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedItem { Path = entry, Reason = SkipReason.AccessDenied, Detail = ex.Message });
                continue;
            }

            var isDir = attributes.HasFlag(FileAttributes.Directory);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                try
                {
                    if (isDir)
                        Directory.Delete(entry, recursive: false); // убирает только саму ссылку
                    else
                        File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new SkippedItem { Path = entry, Reason = SkipReason.ReparsePoint, Detail = ex.Message });
                }

                continue;
            }

            if (isDir)
            {
                deleted += DeleteTree(entry, skipped, errors, ref deletedBytes, ct);
                continue;
            }

            long length = 0;
            try
            {
                length = new FileInfo(entry).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Размер не прочитался — удалять это не мешает.
            }

            if (TryDeleteFile(entry, skipped, errors))
            {
                deleted++;
                deletedBytes += length;
            }
        }

        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedItem { Path = directory, Reason = SkipReason.AccessDenied, Detail = "папка не удалена: " + ex.Message });
        }

        return deleted;
    }

    private static bool TryDeleteFile(string file, List<SkippedItem> skipped, List<string> errors)
    {
        try
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);

            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedItem { Path = file, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            return false;
        }
    }

    /// <summary>Размер и число файлов; в ссылки не заходит.</summary>
    private (long Bytes, int Files) Measure(string path, bool isDirectory, CancellationToken ct)
    {
        try
        {
            if (!isDirectory)
                return (new FileInfo(path).Length, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 1);
        }

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos(
                         "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                ct.ThrowIfCancellationRequested();
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                if (info is DirectoryInfo)
                {
                    var (b, f) = Measure(info.FullName, true, ct);
                    bytes += b;
                    files += f;
                }
                else if (info is FileInfo file)
                {
                    bytes += file.Length;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Размер получится приблизительным — на удаление это не влияет.
        }

        return (bytes, files);
    }

    /// <summary>Если выбраны и папка, и что-то внутри неё — оставляем только папку, чтобы не считать дважды.</summary>
    private static IEnumerable<string> DropNestedSelections(IEnumerable<string> paths)
    {
        var items = new List<(string Original, string? Normalized)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var original in paths)
        {
            string? normalized = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(original))
                    normalized = Normalize(original);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // Оставляем как есть: Evaluate вернёт понятную причину блокировки.
            }

            if (normalized is not null && !seen.Add(normalized))
                continue;

            items.Add((original, normalized));
        }

        var order = Enumerable.Range(0, items.Count)
            .OrderBy(i => items[i].Normalized?.Length ?? int.MaxValue)
            .ToList();
        var keepNormalized = new List<string>();
        var keep = new bool[items.Count];

        foreach (var i in order)
        {
            var normalized = items[i].Normalized;
            if (normalized is null)
            {
                keep[i] = true; // Evaluate вернёт понятную причину блокировки
                continue;
            }

            if (keepNormalized.Any(k => IsUnderOrEqual(k, normalized)))
                continue;

            keepNormalized.Add(normalized);
            keep[i] = true;
        }

        // Возвращаем в порядке, в котором пользователь выбрал.
        return Enumerable.Range(0, items.Count).Where(i => keep[i]).Select(i => items[i].Original).ToList();
    }

    // ------------------------------------------------------------------ пути

    private static string Normalize(string path) => PathSafetyService.Normalize(path);

    private static string TrimSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnderOrEqual(string root, string child)
        => string.Equals(root, child, StringComparison.OrdinalIgnoreCase) ||
           child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string[] Segments(string path)
        => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static string[] RelativeSegments(string root, string child)
        => string.Equals(root, child, StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : Segments(child.Substring(root.Length));

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string path)
    {
        try
        {
            var trimmed = TrimSeparators(path);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
