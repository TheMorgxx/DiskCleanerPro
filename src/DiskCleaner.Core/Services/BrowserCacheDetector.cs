using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Поставляет whitelist-правила для кэшей известных Chromium-браузеров и
/// Firefox. Каждое правило ищет папки Cache/Code Cache/GPUCache (для
/// Chromium) или cache2 (для Firefox) внутри профиля конкретного браузера —
/// профиль пользователя браузера (Default, Profile 1, ...) при этом не
/// нужно знать заранее, он находится динамическим поиском.
/// </summary>
public sealed class BrowserCacheDetector
{
    public List<CleanupRule> GetRules()
    {
        var chromiumBrowsers = new (string AppName, string BasePath)[]
        {
            ("Google Chrome", "%LOCALAPPDATA%\\Google\\Chrome\\User Data"),
            ("Microsoft Edge", "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data"),
            ("Brave", "%LOCALAPPDATA%\\BraveSoftware\\Brave-Browser\\User Data"),
            ("Opera", "%APPDATA%\\Opera Software\\Opera Stable"),
            ("Opera GX", "%APPDATA%\\Opera Software\\Opera GX Stable"),
            ("Vivaldi", "%LOCALAPPDATA%\\Vivaldi\\User Data"),
        };

        var rules = new List<CleanupRule>();

        foreach (var (appName, basePath) in chromiumBrowsers)
        {
            rules.Add(new CleanupRule
            {
                Name = $"Кэш браузера {appName}",
                Description = $"Кэш веб-страниц браузера {appName} (Cache/Code Cache/GPUCache во всех профилях) — пересоздаётся автоматически.",
                Category = CleanupCategory.BrowserCache,
                AppName = appName,
                RiskLevel = CleanupRiskLevel.Safe,
                DriveScope = RuleDriveScope.UserProfile,
                Kind = CleanupResolutionKind.DynamicSearch,
                BasePathTemplate = basePath,
                // Opera хранит профиль прямо в BasePath (без вложенного "Default"/"Profile N"),
                // остальные Chromium-браузеры — во вложенных папках профилей; "*/..." покрывает
                // оба случая, т.к. поиск дополнительно проверяет и сам BasePath на всех уровнях.
                SubPathPatterns = ["Cache", "Code Cache", "GPUCache", "*/Cache", "*/Code Cache", "*/GPUCache"],
                MaxSearchDepth = 3,
            });
        }

        rules.Add(new CleanupRule
        {
            Name = "Кэш браузера Mozilla Firefox",
            Description = "Кэш веб-страниц Firefox (cache2 во всех профилях) — пересоздаётся автоматически.",
            Category = CleanupCategory.BrowserCache,
            AppName = "Mozilla Firefox",
            RiskLevel = CleanupRiskLevel.Safe,
            DriveScope = RuleDriveScope.UserProfile,
            Kind = CleanupResolutionKind.DynamicSearch,
            BasePathTemplate = "%LOCALAPPDATA%\\Mozilla\\Firefox\\Profiles",
            SubPathPatterns = ["*/cache2"],
            MaxSearchDepth = 2,
        });

        return rules;
    }
}
