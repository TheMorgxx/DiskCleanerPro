using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Поставляет whitelist-правила для кэшей инструментов разработки (JetBrains,
/// Gradle, NuGet, pip, npm/yarn/pnpm, Playwright, Android), игровых движков
/// (Unity/Unreal — только глобальный, не per-project кэш) и известных
/// приложений (Roblox, CapCut), плюс общий "ловец" для Electron-приложений.
/// </summary>
public sealed class AppCacheDetector
{
    public List<CleanupRule> GetRules()
    {
        return
        [
            new CleanupRule
            {
                Name = "JetBrains IDE caches",
                Description = "Кэш/индексы/логи установленных IDE JetBrains (Rider, IntelliJ и т.д.) — пересоздаются при следующем запуске IDE.",
                Category = CleanupCategory.JetBrainsCache,
                AppName = "JetBrains",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = "%LOCALAPPDATA%\\JetBrains",
                SubPathPatterns = ["*/caches", "*/index", "*/log", "*/jcef_cache", "*/full-line"],
                MaxSearchDepth = 2,
            },
            new CleanupRule
            {
                Name = "Gradle caches",
                Description = "Кэш зависимостей и сборок Gradle.",
                Category = CleanupCategory.GradleCache,
                AppName = "Gradle",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%USERPROFILE%\\.gradle\\caches",
            },
            new CleanupRule
            {
                Name = "NuGet global packages cache",
                Description = "Глобальный кэш пакетов NuGet (~/.nuget/packages) — пакеты будут скачаны заново при следующей сборке.",
                Category = CleanupCategory.NuGetCache,
                AppName = "NuGet",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%USERPROFILE%\\.nuget\\packages",
            },
            new CleanupRule
            {
                Name = "NuGet HTTP cache",
                Description = "Кэш HTTP-ответов NuGet (v3-cache).",
                Category = CleanupCategory.NuGetCache,
                AppName = "NuGet",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\NuGet\\v3-cache",
            },
            new CleanupRule
            {
                Name = "pip cache",
                Description = "Кэш скачанных пакетов Python pip.",
                Category = CleanupCategory.PipCache,
                AppName = "pip",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\pip\\cache",
            },
            new CleanupRule
            {
                Name = "npm cache",
                Description = "Кэш пакетов npm.",
                Category = CleanupCategory.NodePackageManagerCache,
                AppName = "npm",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\npm-cache",
            },
            new CleanupRule
            {
                Name = "npm cache (Roaming)",
                Description = "Кэш пакетов npm в старом расположении (%APPDATA%\\npm-cache).",
                Category = CleanupCategory.NodePackageManagerCache,
                AppName = "npm",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%APPDATA%\\npm-cache",
            },
            new CleanupRule
            {
                Name = "Yarn cache",
                Description = "Кэш пакетов Yarn.",
                Category = CleanupCategory.NodePackageManagerCache,
                AppName = "Yarn",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\Yarn\\Cache",
            },
            new CleanupRule
            {
                Name = "pnpm store",
                Description = "Кэш-хранилище пакетов pnpm.",
                Category = CleanupCategory.NodePackageManagerCache,
                AppName = "pnpm",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\pnpm-store",
            },
            new CleanupRule
            {
                Name = "Playwright browsers cache",
                Description = "Скачанные бинарники браузеров для Playwright, при необходимости будут скачаны заново.",
                Category = CleanupCategory.PlaywrightCache,
                AppName = "Playwright",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\ms-playwright",
            },
            new CleanupRule
            {
                Name = "Android SDK temp",
                Description = "Временные файлы Android SDK.",
                Category = CleanupCategory.AndroidCache,
                AppName = "Android SDK",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\Android\\Sdk\\.temp",
            },
            new CleanupRule
            {
                Name = "Android build cache (~/.android)",
                Description = "Кэш сборки инструментов Android (build-cache, cache) в профиле пользователя.",
                Category = CleanupCategory.AndroidCache,
                AppName = "Android",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = "%USERPROFILE%\\.android",
                SubPathPatterns = ["cache", "build-cache"],
                MaxSearchDepth = 1,
            },
            new CleanupRule
            {
                Name = "Unity глобальный кэш",
                Description = "Глобальный кэш пакетов/ассетов Unity Editor (не относится к конкретному проекту).",
                Category = CleanupCategory.GameEngineCache,
                AppName = "Unity",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\Unity\\cache",
            },
            new CleanupRule
            {
                Name = "Unreal Engine Derived Data Cache",
                Description = "Глобальный Derived Data Cache Unreal Engine — пересчитывается автоматически, может быть очень большим.",
                Category = CleanupCategory.GameEngineCache,
                AppName = "Unreal Engine",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\UnrealEngine\\Common\\DerivedDataCache",
            },
            new CleanupRule
            {
                Name = "Roblox downloads cache",
                Description = "Кэш загруженных игровых ресурсов (assets) Roblox — скачивается заново при следующем запуске игры.",
                Category = CleanupCategory.RobloxCache,
                AppName = "Roblox",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.FixedPath,
                BasePathTemplate = "%LOCALAPPDATA%\\Roblox\\downloads",
            },
            new CleanupRule
            {
                Name = "CapCut cache",
                Description = "Кэш приложения CapCut (Cache/Code Cache/GPUCache внутри профиля пользователя CapCut).",
                Category = CleanupCategory.CapCutCache,
                AppName = "CapCut",
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = "%LOCALAPPDATA%\\CapCut",
                SubPathPatterns = ["Cache", "Code Cache", "GPUCache"],
                MaxSearchDepth = 5,
            },
            new CleanupRule
            {
                Name = "Кэш прочих Electron-приложений",
                Description = "Общий кэш (Cache/Code Cache/GPUCache), используемый Electron-приложениями (Discord, VS Code, Slack и т.п.), " +
                               "не выделенными в отдельное правило выше.",
                Category = CleanupCategory.ElectronAppCache,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = "%LOCALAPPDATA%",
                SubPathPatterns = ["Cache", "Code Cache", "GPUCache"],
                MaxSearchDepth = 6,
            },
        ];
    }
}
