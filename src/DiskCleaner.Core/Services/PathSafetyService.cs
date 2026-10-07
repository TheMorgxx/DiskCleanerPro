using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Единственная точка, которая решает "можно ли трогать этот путь". Работает
/// по принципу whitelist: путь безопасен, только если он проходит одну из
/// двух независимых проверок:
///
///   Whitelist A — путь лежит строго ВНУТРИ одного из известных профильных
///   каталогов кэшей текущего пользователя (%TEMP%, %LOCALAPPDATA% и т.д.)
///   либо внутри явно одобренных подпапок системных директорий (например
///   C:\Windows\Temp) — сам корень при этом никогда не считается безопасным
///   для удаления, только его содержимое.
///
///   Whitelist B — путь на ЛЮБОМ диске носит хорошо известное "кэшевое" имя
///   (Temp, tmp, ShaderCache) и при этом НЕ пересекается ни с одной защищённой
///   директорией (см. ниже) — используется для категорий уровня диска,
///   которые должны работать не только на диске C:.
///
/// Blacklist используется только как ДОПОЛНИТЕЛЬНАЯ, вторичная страховка (в
/// явном списке защищённых директорий и в пользовательских исключениях), а не
/// как основной механизм — как и требуется.
/// </summary>
public sealed class PathSafetyService
{
    private readonly ReparsePointGuard _reparseGuard;
    private readonly List<string> _profileAllowedRoots;
    private readonly List<string> _windowsApprovedSubRoots;
    private readonly HashSet<string> _userExcludedPaths;

    // Папки прямо в корне ЛЮБОГО диска, которые никогда не считаются
    // безопасными — ни для whitelist A (кроме явных исключений из
    // _windowsApprovedSubRoots), ни для whitelist B.
    private static readonly string[] ForbiddenTopLevelFolderNames =
    [
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "$Recycle.Bin",
        "System Volume Information",
        "ProgramData",
        "Users",
    ];

    private static readonly string[] DriveWideSafeLeafNames =
    [
        "Temp",
        "tmp",
        "ShaderCache",
    ];

    public PathSafetyService(ReparsePointGuard reparseGuard, IEnumerable<string>? userExcludedPaths = null)
    {
        _reparseGuard = reparseGuard;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var temp = Path.GetTempPath();
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        _profileAllowedRoots = new List<string>
        {
            Normalize(temp),
            Normalize(localAppData),
            Normalize(roamingAppData),
            Normalize(Path.Combine(userProfile, ".gradle")),
            Normalize(Path.Combine(userProfile, ".nuget")),
            Normalize(Path.Combine(userProfile, ".android")),
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        // Явные "дыры" в защите папки Windows — только эти конкретные
        // подпапки внутри Windows считаются безопасными кэшами, всё
        // остальное внутри Windows запрещено.
        _windowsApprovedSubRoots = new List<string>
        {
            Normalize(Path.Combine(windowsFolder, "Temp")),
            Normalize(Path.Combine(windowsFolder, "SoftwareDistribution", "Download")),
            Normalize(Path.Combine(windowsFolder, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
        };

        _userExcludedPaths = new HashSet<string>(
            (userExcludedPaths ?? []).Select(NormalizeSafe).Where(p => p is not null)!,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Приводит путь к полной, нормализованной, абсолютной форме без
    /// завершающего разделителя — все сравнения "IsUnder" полагаются на это.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? NormalizeSafe(string path)
    {
        try { return Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Проверяет, что candidate можно безопасно чистить. Вызывается ДВАЖДЫ в
    /// жизненном цикле каждого пути: один раз при сканировании (чтобы решить,
    /// показывать ли его как доступный к выбору) и обязательно ПОВТОРНО прямо
    /// перед удалением (на случай, если между сканированием и нажатием
    /// "Очистить" что-то на диске изменилось).
    /// </summary>
    public bool IsPathSafeForContentDeletion(string candidatePath, out string? reason)
    {
        string normalized;
        try
        {
            normalized = Normalize(candidatePath);
        }
        catch (Exception ex)
        {
            reason = $"не удалось разобрать путь: {ex.Message}";
            return false;
        }

        // 0) Пользовательский блок-лист (SettingsService.ExcludedPaths) —
        //    дополнительная страховка поверх whitelist, а не основа защиты.
        if (_userExcludedPaths.Contains(normalized) || _userExcludedPaths.Any(ex => IsUnder(ex, normalized)))
        {
            reason = "путь добавлен пользователем в список исключений";
            return false;
        }

        // 1) Путь обязан реально существовать и быть директорией.
        if (!Directory.Exists(normalized))
        {
            reason = "путь не существует";
            return false;
        }

        // 2) Никогда не считаем безопасным сам корень диска.
        var driveRoot = Path.GetPathRoot(normalized);
        if (driveRoot is not null && string.Equals(Normalize(driveRoot), normalized, StringComparison.OrdinalIgnoreCase))
        {
            reason = "нельзя удалять содержимое корня диска целиком";
            return false;
        }

        // 2b) Сам целевой путь не должен быть symlink/junction — доверять
        //     содержимому ссылки нельзя, она может указывать куда угодно.
        if (_reparseGuard.IsReparsePoint(normalized))
        {
            reason = "путь является symlink/junction (reparse point) и пропущен из соображений безопасности";
            return false;
        }

        // 3) Явно защищённые пользовательские папки (Documents/Desktop/... /
        //    Videos/Music) — под любым профилем, проверяется всегда.
        var userProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string[] userProtected =
        [
            Path.Combine(userProfile, "Documents"),
            Path.Combine(userProfile, "Desktop"),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Pictures"),
            Path.Combine(userProfile, "Videos"),
            Path.Combine(userProfile, "Music"),
        ];
        foreach (var protectedPath in userProtected)
        {
            var np = Normalize(protectedPath);
            if (string.Equals(normalized, np, StringComparison.OrdinalIgnoreCase) || IsUnder(np, normalized))
            {
                reason = $"путь пересекается с защищённой пользовательской папкой ({protectedPath})";
                return false;
            }
        }

        // 4) Жёсткий блок системных/программных директорий — считается для
        //    диска, на котором реально лежит candidate (не только C:), с
        //    явными исключениями для одобренных подпапок Windows.
        if (driveRoot is not null)
        {
            foreach (var forbiddenName in ForbiddenTopLevelFolderNames)
            {
                var forbiddenPath = Normalize(Path.Combine(driveRoot, forbiddenName));
                var intersectsForbidden = string.Equals(normalized, forbiddenPath, StringComparison.OrdinalIgnoreCase) ||
                                           IsUnder(forbiddenPath, normalized);
                if (!intersectsForbidden)
                    continue;

                var isApprovedWindowsSubRoot = _windowsApprovedSubRoots.Any(root =>
                    string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase) || IsUnder(root, normalized));
                if (isApprovedWindowsSubRoot)
                    continue;

                reason = $"путь находится внутри защищённой системной директории ({forbiddenPath})";
                return false;
            }
        }

        // 5) Whitelist A — профильные корни пользователя + одобренные Windows-подпапки.
        //    IsUnder(root, normalized) уже возвращает true и для точного совпадения
        //    с самим root — это ожидаемо: например, правило "%TEMP%" резолвится
        //    ИМЕННО в сам корень _profileAllowedRoots, и это по-прежнему безопасно,
        //    т.к. CleanupService всегда удаляет только СОДЕРЖИМОЕ FullPath, а не
        //    сам FullPath. Запрет удаления КОРНЯ ДИСКА (C:\) — отдельная, более
        //    ранняя проверка (шаг 2) и с этим шагом никак не связана.
        var underProfileRoot = _profileAllowedRoots.Any(root => IsUnder(root, normalized));
        var underApprovedWindowsRoot = _windowsApprovedSubRoots.Any(root => IsUnder(root, normalized));

        if (underProfileRoot || underApprovedWindowsRoot)
        {
            reason = null;
            return true;
        }

        // 6) Whitelist B — известное "кэшевое" имя папки на любом диске.
        //    Системные и пользовательские защищённые пути уже отсеяны выше.
        var leafName = Path.GetFileName(normalized);
        var hasSafeLeafName = DriveWideSafeLeafNames.Any(name =>
            string.Equals(name, leafName, StringComparison.OrdinalIgnoreCase));
        if (hasSafeLeafName)
        {
            reason = null;
            return true;
        }

        reason = "путь не входит ни в один разрешённый (whitelist) корень кэшей";
        return false;
    }

    /// <summary>True, если child лежит строго внутри root (или равен ему).</summary>
    private static bool IsUnder(string root, string child)
    {
        var rootWithSep = root + Path.DirectorySeparatorChar;
        return child.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(child, root, StringComparison.OrdinalIgnoreCase);
    }
}
