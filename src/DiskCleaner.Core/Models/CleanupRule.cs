namespace DiskCleaner.Core.Models;

/// <summary>Как разворачивать BasePathTemplate в реальные пути на диске.</summary>
public enum CleanupResolutionKind
{
    /// <summary>Путь задан напрямую (после подстановки переменных окружения / {drive}).</summary>
    FixedPath,

    /// <summary>
    /// Нужно найти подпапки по маске внутри BasePathTemplate (см.
    /// SubPathPatterns на CleanupRule и логику в CleanupRuleProvider).
    /// </summary>
    DynamicSearch,
}

/// <summary>
/// От какого набора дисков зависит правило. Это заменяет ручные "if
/// systemDrive == ..." проверки на явную декларацию в самом правиле.
/// </summary>
public enum RuleDriveScope
{
    /// <summary>
    /// Путь привязан к профилю ТЕКУЩЕГО пользователя Windows (%TEMP%,
    /// %LOCALAPPDATA% и т.д.) и не зависит от того, какие диски выбраны для
    /// сканирования — разворачивается один раз, независимо от выбора дисков.
    /// </summary>
    UserProfile,

    /// <summary>
    /// Путь существует только на системном диске (там, где установлена
    /// Windows) — например C:\Windows\Temp. Правило разворачивается только
    /// когда пользователь включил системный диск в сканирование.
    /// </summary>
    SystemDriveOnly,

    /// <summary>
    /// Путь зависит от конкретного диска и должен быть развёрнут отдельно для
    /// КАЖДОГО диска, который пользователь выбрал для сканирования.
    /// BasePathTemplate в этом случае может содержать токен "{drive}",
    /// который будет заменён на корень диска, например "D:\".
    /// </summary>
    PerSelectedDrive,
}

/// <summary>
/// Статическое описание одной whitelist-категории. Это ЕДИНСТВЕННОЕ место в
/// программе, где перечислены пути, которые в принципе можно предлагать к
/// удалению — см. CleanupRuleProvider.GetBuiltInRules(). Чтобы добавить новую
/// категорию, нужно добавить сюда новый CleanupRule и ничего больше.
/// </summary>
public sealed class CleanupRule
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required CleanupCategory Category { get; init; }

    /// <summary>Имя конкретного приложения/инструмента, если применимо (для UI), иначе null.</summary>
    public string? AppName { get; init; }

    public required CleanupRiskLevel RiskLevel { get; init; }

    /// <summary>
    /// true, если для реального доступа к пути обычно нужны права
    /// администратора (например SoftwareDistribution\Download,
    /// DeliveryOptimization\Cache, иногда C:\Windows\Temp).
    /// </summary>
    public bool RequiresAdmin { get; init; }

    public required RuleDriveScope DriveScope { get; init; }
    public required CleanupResolutionKind Kind { get; init; }

    /// <summary>
    /// Путь с переменными окружения (%TEMP%, %LOCALAPPDATA% и т.д.) и/или
    /// токеном "{drive}" для PerSelectedDrive-правил. Для DynamicSearch — это
    /// базовая директория, от которой начинается поиск по SubPathPatterns.
    /// </summary>
    public required string BasePathTemplate { get; init; }

    /// <summary>Для DynamicSearch: относительные шаблоны подпапок (поддерживается "*" как маска одного уровня).</summary>
    public string[] SubPathPatterns { get; init; } = [];

    /// <summary>Максимальная глубина рекурсивного поиска для DynamicSearch.</summary>
    public int MaxSearchDepth { get; init; } = 4;

    /// <summary>
    /// Если задано — при удалении и подсчёте размера учитываются ТОЛЬКО файлы,
    /// имя которых совпадает с одним из этих wildcard-шаблонов (например
    /// "thumbcache_*.db"), а не всё содержимое папки. Подпапки при этом не
    /// затрагиваются вообще. Используется для точечных категорий вроде кэша
    /// миниатюр Windows, где в той же папке может быть посторонний контент.
    /// </summary>
    public string[]? FileNamePatterns { get; init; }
}
