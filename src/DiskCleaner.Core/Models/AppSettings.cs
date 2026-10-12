namespace DiskCleaner.Core.Models;

/// <summary>Пользовательские настройки, сохраняемые между запусками через SettingsService.</summary>
public sealed class AppSettings
{
    /// <summary>Имена правил (CleanupRule.Name), которые пользователь в прошлый раз отмечал галочкой.</summary>
    public List<string> SelectedRuleNames { get; set; } = [];

    public bool DryRunEnabled { get; set; } = true;

    /// <summary>Корни дисков, выбранных для сканирования в прошлый раз, например ["C:\\", "D:\\"].</summary>
    public List<string> LastSelectedDrives { get; set; } = [];

    /// <summary>
    /// Пути, которые пользователь явно исключил из очистки (даже если они
    /// попадают под безопасное правило) — проверяются в PathSafetyService
    /// как дополнительный блок-лист поверх whitelist-логики.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];
}
