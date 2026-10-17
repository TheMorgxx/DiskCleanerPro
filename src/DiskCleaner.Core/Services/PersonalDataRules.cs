namespace DiskCleaner.Core.Services;

/// <summary>
/// Пороги и константы защиты личных данных. Единственное место, где записаны числа:
/// <see cref="Models.DiskAnalysisOptions"/> берёт значения по умолчанию отсюда.
/// </summary>
public static class PersonalDataRules
{
    /// <summary>Папка «личная», если в ней не меньше стольки личных файлов (считаются файлы от <see cref="CountRuleMinFileBytes"/>).</summary>
    public const int MinFolderFileCount = 20;

    /// <summary>Файлы меньше этого размера не учитываются в правиле «количество» (иконки, спрайты, миниатюры).</summary>
    public const long CountRuleMinFileBytes = 32L * 1024;

    /// <summary>Папка «личная», если личные файлы занимают не меньше этой доли её размера.</summary>
    public const double MinFolderShare = 0.5;

    /// <summary>Правило доли применяется только к папкам не меньше этого размера.</summary>
    public const long ShareRuleMinFolderBytes = 100L * 1024 * 1024;

    /// <summary>Личных файлов суммарно не меньше этого размера в папке — подтверждение независимо от доли.</summary>
    public const long AbsolutePersonalBytes = 1024L * 1024 * 1024;

    /// <summary>При таком объёме личных данных в удаляемом подтверждение требует ввести <see cref="ConfirmWord"/>.</summary>
    public const long TypedConfirmationBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>Слово для подтверждения крупного удаления личных данных.</summary>
    public const string ConfirmWord = "УДАЛИТЬ";

    /// <summary>
    /// Папки, чьё содержимое не считается личным по составу: в них много картинок и аудио, но это ассеты игр,
    /// кэши и зависимости. Их файлы не идут в статистику родителей, а сами они не попадают в индекс личных папок.
    /// Отдельные файлы по расширению это не отменяет.
    /// </summary>
    private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".cache", ".git", "site-packages", "__pycache__", ".gradle", ".m2", "Pods", "venv", ".venv",
        "steamapps", "SteamLibrary", "Epic Games", "GOG Games", "WindowsApps",
        "Cache", "Code Cache", "GPUCache", "cache2", "ShaderCache",
    };

    public static bool IsIgnoredFolderName(string? name)
        => !string.IsNullOrEmpty(name) && IgnoredFolderNames.Contains(name);

    /// <summary>Совпадает ли введённое пользователем слово с <see cref="ConfirmWord"/> (без учёта регистра и пробелов по краям).</summary>
    public static bool IsConfirmWord(string? input)
        => string.Equals(input?.Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase);
}
