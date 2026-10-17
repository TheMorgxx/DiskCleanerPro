using System.Globalization;
using System.Windows.Data;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.Localization;

/// <summary>
/// Русские названия для значений enum из Core. Core остаётся без локализации,
/// соответствие живёт только в слое App. Для значения, которого нет в таблице,
/// показывается исходное имя enum.
/// </summary>
public static class UiNames
{
    public const string RiskSafe = "Безопасно";
    public const string RiskReview = "Проверить";
    public const string RiskManual = "Вручную";

    private static readonly Dictionary<CleanupCategory, string> Categories = new()
    {
        [CleanupCategory.UserTemp] = "Пользовательский TEMP",
        [CleanupCategory.WindowsTemp] = "Windows Temp",
        [CleanupCategory.NvidiaDxCache] = "Кэш NVIDIA DX",
        [CleanupCategory.DirectXShaderCache] = "Кэш шейдеров DirectX",
        [CleanupCategory.CrashDumps] = "Дампы сбоев",
        [CleanupCategory.BrowserCache] = "Кэш браузеров",
        [CleanupCategory.ElectronAppCache] = "Кэш Electron-приложений",
        [CleanupCategory.JetBrainsCache] = "Кэш JetBrains",
        [CleanupCategory.GradleCache] = "Кэш Gradle",
        [CleanupCategory.NuGetCache] = "Кэш NuGet",
        [CleanupCategory.PipCache] = "Кэш pip",
        [CleanupCategory.NodePackageManagerCache] = "Кэш npm/yarn/pnpm",
        [CleanupCategory.PlaywrightCache] = "Кэш Playwright",
        [CleanupCategory.AndroidCache] = "Кэш Android",
        [CleanupCategory.GameEngineCache] = "Кэш игровых движков",
        [CleanupCategory.RobloxCache] = "Кэш Roblox",
        [CleanupCategory.CapCutCache] = "Кэш CapCut",
        [CleanupCategory.WindowsThumbnailCache] = "Кэш миниатюр Windows",
        [CleanupCategory.DeliveryOptimizationCache] = "Кэш оптимизации доставки",
        [CleanupCategory.WindowsUpdateCache] = "Кэш Windows Update",
        [CleanupCategory.DriveRootTempFolder] = "Temp в корне диска",
        [CleanupCategory.ShaderCacheGeneric] = "Кэш шейдеров (поиск по диску)",
    };

    private static readonly Dictionary<SkipReason, string> SkipReasons = new()
    {
        [SkipReason.ReparsePoint] = "Ссылка (reparse point)",
        [SkipReason.AccessDenied] = "Нет доступа",
        [SkipReason.IOError] = "Ошибка ввода-вывода",
        [SkipReason.UnsafePath] = "Небезопасный путь",
        [SkipReason.RequiresAdminNotElevated] = "Нужны права администратора",
    };

    public static string Risk(CleanupRiskLevel level) => level switch
    {
        CleanupRiskLevel.Safe => RiskSafe,
        CleanupRiskLevel.Review => RiskReview,
        CleanupRiskLevel.Manual => RiskManual,
        _ => level.ToString(),
    };

    public static string Category(CleanupCategory category) =>
        Categories.TryGetValue(category, out var name) ? name : category.ToString();

    public static string SkipReasonName(SkipReason reason) =>
        SkipReasons.TryGetValue(reason, out var name) ? name : reason.ToString();

    public static string DriveTypeName(string? type) => type switch
    {
        "Fixed" => "Локальный",
        "Removable" => "Съёмный",
        "Network" => "Сетевой",
        "CDRom" => "Оптический",
        "Ram" => "RAM-диск",
        _ => "Неизвестно",
    };
}

/// <summary>Причина пропуска (SkipReason) → русское название. Общий для ResultsWindow и окна пропущенных элементов.</summary>
public sealed class SkipReasonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SkipReason reason ? UiNames.SkipReasonName(reason) : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → «Да»/«Нет».</summary>
public sealed class YesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Да" : "Нет";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
