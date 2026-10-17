using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Системные корни, от которых зависят правила <see cref="PathSafetyService"/>. В программе берутся из системы
/// (<see cref="FromSystem"/>), в тестах подставляются временные папки.
/// </summary>
public sealed record PathSafetyRoots(
    string UserProfile,
    string LocalAppData,
    string RoamingAppData,
    string Temp,
    string WindowsFolder)
{
    public static PathSafetyRoots FromSystem() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.GetTempPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
}

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
    private readonly string _userProfile;
    private readonly string _tempRoot;
    private readonly List<string> _profileAllowedRoots;
    private readonly List<string> _windowsApprovedSubRoots;
    private readonly Func<IEnumerable<string>> _excludedPathsProvider;

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

    /// <summary>
    /// Конструктор для тестов и простых сценариев: список исключений копируется ОДИН раз и дальше не
    /// меняется. Для приложения используй перегрузку с провайдером — там изменения видны сразу.
    /// </summary>
    public PathSafetyService(ReparsePointGuard reparseGuard, IEnumerable<string>? userExcludedPaths = null, PathSafetyRoots? roots = null)
        : this(reparseGuard, CreateFixedProvider(userExcludedPaths), roots)
    {
    }

    /// <param name="excludedPathsProvider">
    /// Вызывается при КАЖДОЙ проверке, поэтому изменения списка исключений действуют сразу, без перезапуска
    /// (как в <see cref="ManualDeletionService"/>).
    /// </param>
    /// <param name="roots">Системные корни (профиль, AppData, Temp, Windows); null — взять из системы.</param>
    public PathSafetyService(ReparsePointGuard reparseGuard, Func<IEnumerable<string>> excludedPathsProvider, PathSafetyRoots? roots = null)
    {
        _reparseGuard = reparseGuard;
        ArgumentNullException.ThrowIfNull(excludedPathsProvider);
        _excludedPathsProvider = excludedPathsProvider;
        roots ??= PathSafetyRoots.FromSystem();
        var userProfile = roots.UserProfile;
        var localAppData = roots.LocalAppData;
        var roamingAppData = roots.RoamingAppData;
        var temp = roots.Temp;
        var windowsFolder = roots.WindowsFolder;
        _userProfile = Normalize(userProfile);
        _tempRoot = Normalize(temp);

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

    }

    private static Func<IEnumerable<string>> CreateFixedProvider(IEnumerable<string>? userExcludedPaths)
    {
        var copy = (userExcludedPaths ?? []).ToList();
        return () => copy;
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
    /// Читает список исключений. null — прочитать не удалось (например, коллекцию изменяли в другом потоке
    /// три раза подряд, либо провайдер вернул null): вызывающий обязан считать это блокировкой, а не
    /// «исключений нет». Некорректные и пустые пути в списке пропускаются.
    /// </summary>
    private List<string>? ReadExcludedPaths()
    {
        List<string>? raw = null;
        for (var attempt = 0; attempt < 3 && raw is null; attempt++)
        {
            try
            {
                var source = _excludedPathsProvider();
                if (source is null)
                    return null;

                raw = source.ToList();
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
            if (string.IsNullOrWhiteSpace(item))
                continue;

            var normalized = NormalizeSafe(item);
            if (normalized is not null)
                result.Add(normalized);
        }

        return result;
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
        string full;
        try
        {
            full = Path.GetFullPath(candidatePath);
            normalized = Normalize(candidatePath);
        }
        catch (Exception ex)
        {
            reason = $"не удалось разобрать путь: {ex.Message}";
            return false;
        }

        // 0) Пользовательский блок-лист (SettingsService.ExcludedPaths) —
        //    дополнительная страховка поверх whitelist, а не основа защиты.
        //    Список читается заново при каждой проверке. Блокируем и сам исключённый путь (и всё внутри него),
        //    и цель, внутри которой лежит исключённый путь — иначе рекурсивная очистка цели снесла бы
        //    исключённое (так же, как в ManualDeletionService.Evaluate).
        var excludedPaths = ReadExcludedPaths();
        if (excludedPaths is null)
        {
            reason = "не удалось прочитать список исключений — повтори через секунду";
            return false;
        }

        foreach (var excluded in excludedPaths)
        {
            if (IsUnder(excluded, normalized))
            {
                reason = "путь добавлен пользователем в список исключений";
                return false;
            }

            if (IsUnder(normalized, excluded))
            {
                reason = $"внутри лежит путь из списка исключений ({excluded})";
                return false;
            }
        }

        // 2) Никогда не считаем безопасным сам корень диска. Проверяется по ПОЛНОМУ пути ДО обрезки завершающего
        //    разделителя и до проверки существования: после Normalize "C:\" превращается в "C:", что Windows
        //    трактует как «текущая папка на диске C», и корень уже не распознаётся (как в ManualDeletionService).
        var fullRoot = Path.GetPathRoot(full);
        if (fullRoot is not null && string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "нельзя удалять содержимое корня диска целиком";
            return false;
        }

        // 1) Путь обязан реально существовать и быть директорией.
        if (!Directory.Exists(normalized))
        {
            reason = "путь не существует";
            return false;
        }

        var driveRoot = Path.GetPathRoot(normalized);

        // 2b) Сам целевой путь не должен быть symlink/junction — доверять
        //     содержимому ссылки нельзя, она может указывать куда угодно.
        if (_reparseGuard.IsReparsePoint(normalized))
        {
            reason = "путь является symlink/junction (reparse point) и пропущен из соображений безопасности";
            return false;
        }

        // 3) Явно защищённые пользовательские папки (Documents/Desktop/... /
        //    Videos/Music) — под любым профилем, проверяется всегда.
        var userProfile = _userProfile;
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

        // Путь внутри профильных корней текущего пользователя (%TEMP%, %LOCALAPPDATA%, ...).
        // Нужен и шагу 4 (исключение для "Users"), и шагу 5 (Whitelist A).
        var underProfileRoot = _profileAllowedRoots.Any(root => IsUnder(root, normalized));

        // 3b) Сам профильный корень целиком (%LOCALAPPDATA%, %APPDATA%, .gradle, .nuget, .android) не цель очистки:
        //     там лежат настройки и данные программ (gradle.properties, debug.keystore и т.п.). Допустимы только
        //     вложенные папки кэшей. Исключение — Temp: его правило как раз и указывает на корень, а чистится
        //     только содержимое.
        if (!string.Equals(normalized, _tempRoot, StringComparison.OrdinalIgnoreCase) &&
            _profileAllowedRoots.Any(root => string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"корень профильной папки ({normalized}) целиком не может быть целью очистки — допустимы только вложенные папки кэшей";
            return false;
        }

        // 4) Жёсткий блок системных/программных директорий — считается для
        //    диска, на котором реально лежит candidate (не только C:), с
        //    явными исключениями для одобренных подпапок Windows и, для записи
        //    "Users", для профильных корней ТЕКУЩЕГО пользователя (иначе шаг 4,
        //    стоящий раньше Whitelist A, блокировал бы %TEMP% и %LOCALAPPDATA%,
        //    лежащие внутри {диск}\Users\<я>). Чужие профили и всё остальное
        //    внутри "Users" (в т.ч. {диск}\Users\<я>\Projects) остаётся заблокированным.
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

                if (underProfileRoot && string.Equals(forbiddenName, "Users", StringComparison.OrdinalIgnoreCase))
                    continue;

                reason = $"путь находится внутри защищённой системной директории ({forbiddenPath})";
                return false;
            }
        }

        // 5) Whitelist A — профильные корни пользователя + одобренные Windows-подпапки.
        //    IsUnder(root, normalized) возвращает true и для точного совпадения
        //    с самим root. Для корня Temp это ожидаемо: правило "%TEMP%" резолвится
        //    ИМЕННО в сам корень _profileAllowedRoots, и это по-прежнему безопасно,
        //    т.к. CleanupService всегда удаляет только СОДЕРЖИМОЕ FullPath, а не
        //    сам FullPath. Запрет удаления КОРНЯ ДИСКА (C:\) — отдельная, более
        //    ранняя проверка (шаг 2) и с этим шагом никак не связана.
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