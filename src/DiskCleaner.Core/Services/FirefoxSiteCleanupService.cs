using System.Diagnostics;
using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Точечная очистка данных сайтов в профиле Firefox: удаляются только выбранные типы данных
/// (idb / cache / ls / fs) выбранных сайтов. Куки (cookies.sqlite), пароли (logins.json, key4.db),
/// история и закладки (places.sqlite), расширения и настройки не затрагиваются — они лежат вне
/// storage\default, а модуль по построению не умеет удалять ничего, кроме подпапок allowlist'а
/// <see cref="FirefoxStorageLayout.KnownParts"/> внутри storage\default\&lt;источник&gt;.
///
/// Само удаление выполняет <see cref="ManualDeletionService"/> (исключения, dry-run, корзина,
/// защита ссылок, повторная проверка перед удалением), модуль добавляет:
///   • проверку формы пути — цель обязана выглядеть как storage\default\&lt;источник&gt;[\&lt;тип&gt;];
///   • запрет на данные расширений (moz-extension);
///   • отказ от реального удаления, пока Firefox запущен (иначе повреждается база квот и данные);
///   • понятные подписи в журнале («Firefox: www.tiktok.com — IndexedDB»).
/// </summary>
public sealed class FirefoxSiteCleanupService
{
    private readonly ManualDeletionService _deletion;
    private readonly Func<string, bool> _isProfileInUse;

    /// <param name="isProfileInUse">
    /// Получает путь профиля, возвращает true, если Firefox запущен/профиль занят.
    /// По умолчанию: процесс firefox запущен ИЛИ файл parent.lock профиля удерживается.
    /// </param>
    public FirefoxSiteCleanupService(ManualDeletionService deletion, Func<string, bool>? isProfileInUse = null)
    {
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _isProfileInUse = isProfileInUse ?? DefaultIsProfileInUse;
    }

    public CleanupResult Clean(
        IEnumerable<FirefoxSiteSelection> selections,
        FirefoxCleanupOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(options);

        var result = new CleanupResult { DryRun = options.DryRun };
        var plannedPaths = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selection in selections.Where(s => s.Parts.Count > 0))
        {
            var error = Validate(selection, out var originPath, out var profilePath);
            if (error is not null)
            {
                result.Entries.Add(new CleanupLogEntry
                {
                    TargetDisplayName = $"Firefox: {selection.Site.DisplayName}",
                    Path = selection.Site.OriginPath,
                    DryRun = options.DryRun,
                    Errors = [$"Некорректная цель: {error}. Пропущено."],
                });
                continue;
            }

            profiles.Add(profilePath);
            Plan(selection, originPath, options, plannedPaths, labels);
        }

        // Реальное удаление при запущенном Firefox запрещено целиком (всё или ничего).
        if (!options.DryRun)
        {
            var busy = profiles.Where(p => SafeIsInUse(p)).ToList();
            if (busy.Count > 0)
            {
                foreach (var profile in busy)
                {
                    result.Entries.Add(new CleanupLogEntry
                    {
                        TargetDisplayName = "Firefox",
                        Path = profile,
                        DryRun = false,
                        Errors = ["Firefox запущен или профиль занят. Закрой Firefox (включая фоновые процессы) и повтори. Ничего не удалено."],
                    });
                }

                return result;
            }
        }

        if (plannedPaths.Count == 0)
            return result;

        var deletion = _deletion.Delete(
            plannedPaths,
            new ManualDeletionOptions
            {
                DryRun = options.DryRun,
                UseRecycleBin = options.UseRecycleBin,
                ConfirmedSensitive = options.Confirmed,
            },
            progress,
            cancellationToken);

        foreach (var entry in deletion.Entries)
        {
            var label = labels.TryGetValue(entry.Path, out var text) ? text : entry.TargetDisplayName;
            result.Entries.Add(new CleanupLogEntry
            {
                TargetDisplayName = label + (options.UseRecycleBin && !options.DryRun ? " [в корзину]" : ""),
                Path = entry.Path,
                SizeBeforeBytes = entry.SizeBeforeBytes,
                SizeAfterBytes = entry.SizeAfterBytes,
                FilesDeleted = entry.FilesDeleted,
                FilesSkipped = entry.FilesSkipped,
                DryRun = entry.DryRun,
                SkippedItems = entry.SkippedItems,
                Errors = entry.Errors,
            });
        }

        result.WasCancelled = deletion.WasCancelled;
        return result;
    }

    // ------------------------------------------------------------------ планирование

    private static void Plan(
        FirefoxSiteSelection selection,
        string originPath,
        FirefoxCleanupOptions options,
        List<string> plannedPaths,
        Dictionary<string, string> labels)
    {
        var site = selection.Site;
        var selectedNames = selection.Parts
            .Select(p => p.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> existingFolders;
        try
        {
            existingFolders = Directory.Exists(originPath)
                ? Directory.EnumerateDirectories(originPath).Select(d => Path.GetFileName(d)!).ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existingFolders = new List<string>();
        }

        // Выбраны все папки сайта — удаляем источник целиком (вместе со служебным .metadata-v2),
        // иначе в настройках Firefox останется пустая запись сайта.
        var wholeOrigin = options.RemoveEmptyOriginFolders &&
                          existingFolders.Count > 0 &&
                          existingFolders.All(selectedNames.Contains);

        if (wholeOrigin)
        {
            plannedPaths.Add(originPath);
            labels[originPath] = $"Firefox: {site.DisplayName} — весь сайт ({string.Join(", ", existingFolders)})";
            return;
        }

        foreach (var part in selection.Parts)
        {
            var path = Path.Combine(originPath, part.FolderName);
            if (!Directory.Exists(path))
                continue;

            var title = FirefoxStorageLayout.Find(part.FolderName)?.Title ?? part.FolderName;
            plannedPaths.Add(path);
            labels[path] = $"Firefox: {site.DisplayName} — {title}";
        }
    }

    // ------------------------------------------------------------------ проверка формы цели

    /// <summary>
    /// Проверяет, что цель имеет вид &lt;профиль&gt;\storage\default\&lt;источник&gt;[\idb|cache|ls|fs].
    /// Возвращает текст ошибки или null.
    /// </summary>
    private static string? Validate(FirefoxSiteSelection selection, out string originPath, out string profilePath)
    {
        originPath = "";
        profilePath = "";
        var site = selection.Site;

        string origin;
        try
        {
            origin = PathSafetyService.Normalize(site.OriginPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return "путь сайта не разобран";
        }

        var defaultDir = Path.GetDirectoryName(origin);
        var storageDir = defaultDir is null ? null : Path.GetDirectoryName(defaultDir);
        var profileDir = storageDir is null ? null : Path.GetDirectoryName(storageDir);

        if (defaultDir is null || storageDir is null || profileDir is null ||
            !string.Equals(Path.GetFileName(defaultDir), "default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(storageDir), "storage", StringComparison.OrdinalIgnoreCase))
        {
            return "путь не похож на <профиль>\\storage\\default\\<сайт>";
        }

        var folderName = Path.GetFileName(origin);
        if (!string.Equals(folderName, site.OriginFolderName, StringComparison.Ordinal))
            return "имя папки сайта не совпадает с описанием сайта";

        // Не доверяем флагу из модели — разбираем имя папки сами.
        if (site.IsExtension || FirefoxOriginInfo.Parse(folderName).IsExtension)
            return "данные расширений не удаляются";

        foreach (var part in selection.Parts)
        {
            if (FirefoxStorageLayout.Find(part.FolderName) is null)
                return $"тип данных «{part.FolderName}» не входит в разрешённые (idb, cache, ls, fs)";

            string normalizedPart;
            try
            {
                normalizedPart = PathSafetyService.Normalize(part.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return "путь типа данных не разобран";
            }

            var expected = Path.Combine(origin, part.FolderName);
            if (!string.Equals(normalizedPart, expected, StringComparison.OrdinalIgnoreCase))
                return $"путь «{part.Path}» не лежит внутри папки сайта";
        }

        originPath = origin;
        profilePath = profileDir;
        return null;
    }

    // ------------------------------------------------------------------ Firefox запущен?

    private bool SafeIsInUse(string profilePath)
    {
        try
        {
            return _isProfileInUse(profilePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return true; // не смогли убедиться, что Firefox закрыт — безопаснее считать занятым
        }
    }

    private static bool DefaultIsProfileInUse(string profilePath)
    {
        try
        {
            if (Process.GetProcessesByName("firefox").Length > 0)
                return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Список процессов недоступен — полагаемся на проверку parent.lock ниже.
        }

        var lockFile = Path.Combine(profilePath, "parent.lock");
        if (!File.Exists(lockFile))
            return false;

        try
        {
            // Пока Firefox работает, он удерживает parent.lock — открыть его эксклюзивно не получится.
            using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
