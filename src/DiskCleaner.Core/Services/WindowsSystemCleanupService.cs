using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Поставляет whitelist-правила для системных категорий уровня ОС/диска:
/// временные файлы, кэш миниатюр, кэш Windows Update, Delivery Optimization,
/// кэш шейдеров DirectX/NVIDIA. Ничего не удаляет и не сканирует само —
/// только описывает ПРАВИЛА, разворачивание в реальные пути делает
/// CleanupRuleProvider.
/// </summary>
public sealed class WindowsSystemCleanupService
{
    public List<CleanupRule> GetRules()
    {
        return
        [
            new CleanupRule
            {
                Name = "Временные файлы пользователя (%TEMP%)",
                Description = "Временные файлы, создаваемые программами и системой для текущего пользователя.",
                Category = CleanupCategory.UserTemp,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%TEMP%",
            },
            new CleanupRule
            {
                Name = "Временные файлы Windows",
                Description = "Общесистемная временная папка Windows. Может требовать прав администратора.",
                Category = CleanupCategory.WindowsTemp,
                RiskLevel = CleanupRiskLevel.Safe,
                RequiresAdmin = true,
                DriveScope = RuleDriveScope.SystemDriveOnly,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "{drive}Windows\\Temp",
            },
            new CleanupRule
            {
                Name = "NVIDIA DXCache",
                Description = "Кэш шейдеров DirectX от драйвера NVIDIA, пересоздаётся автоматически.",
                Category = CleanupCategory.NvidiaDxCache,
                AppName = "NVIDIA",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\NVIDIA\\DXCache",
            },
            new CleanupRule
            {
                Name = "DirectX Shader Cache (D3DSCache)",
                Description = "Общий кэш скомпилированных шейдеров DirectX, управляемый самой Windows.",
                Category = CleanupCategory.DirectXShaderCache,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\D3DSCache",
            },
            new CleanupRule
            {
                Name = "Crash Dumps",
                Description = "Дампы аварийного завершения программ Windows Error Reporting.",
                Category = CleanupCategory.CrashDumps,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\CrashDumps",
            },
            new CleanupRule
            {
                Name = "Кэш миниатюр Windows (thumbcache)",
                Description = "Файлы-превью для проводника Windows. Затрагиваются только сами файлы thumbcache_*.db / iconcache_*.db, ничего больше в этой папке не удаляется.",
                Category = CleanupCategory.WindowsThumbnailCache,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer",
                FileNamePatterns = ["thumbcache_*.db", "iconcache_*.db"],
            },
            new CleanupRule
            {
                Name = "Кэш загрузок Windows Update",
                Description = "Скачанные, но уже установленные или устаревшие файлы обновлений Windows (SoftwareDistribution\\Download). " +
                               "Для гарантированной полной очистки службу Windows Update (wuauserv) рекомендуется предварительно остановить вручную — " +
                               "программа этого не делает автоматически. Часть файлов может быть занята системой и будет пропущена.",
                Category = CleanupCategory.WindowsUpdateCache,
                RiskLevel = CleanupRiskLevel.Manual,
                RequiresAdmin = true,
                DriveScope = RuleDriveScope.SystemDriveOnly,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "{drive}Windows\\SoftwareDistribution\\Download",
            },
            new CleanupRule
            {
                Name = "Кэш Delivery Optimization",
                Description = "Кэш системы Delivery Optimization (P2P-доставка обновлений Windows/Store). Требует прав администратора для доступа.",
                Category = CleanupCategory.DeliveryOptimizationCache,
                RiskLevel = CleanupRiskLevel.Manual,
                RequiresAdmin = true,
                DriveScope = RuleDriveScope.SystemDriveOnly,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "{drive}Windows\\ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization\\Cache",
            },
            new CleanupRule
            {
                Name = "Папка Temp в корне диска",
                Description = "Папка Temp прямо в корне диска — обычно используется инсталляторами и портативными программами как временное хранилище.",
                Category = CleanupCategory.DriveRootTempFolder,
                RiskLevel = CleanupRiskLevel.Review,
                DriveScope = RuleDriveScope.PerSelectedDrive,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "{drive}Temp",
            },
            new CleanupRule
            {
                Name = "Папка tmp в корне диска",
                Description = "Папка tmp прямо в корне диска — обычно используется программами как временное хранилище.",
                Category = CleanupCategory.DriveRootTempFolder,
                RiskLevel = CleanupRiskLevel.Review,
                DriveScope = RuleDriveScope.PerSelectedDrive,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "{drive}tmp",
            },
            new CleanupRule
            {
                Name = "ShaderCache на диске",
                Description = "Кэш скомпилированных шейдеров игр (Steam/Unity/Unreal), найденный поиском по всему диску — пересоздаётся автоматически, " +
                               "но помечен как Review, т.к. найден эвристически, а не по точному известному пути.",
                Category = CleanupCategory.ShaderCacheGeneric,
                RiskLevel = CleanupRiskLevel.Review,
                DriveScope = RuleDriveScope.PerSelectedDrive,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = "{drive}",
                SubPathPatterns = ["ShaderCache"],
                MaxSearchDepth = 6,
            },
        ];
    }
}
