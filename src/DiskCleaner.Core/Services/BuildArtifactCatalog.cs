namespace DiskCleaner.Core.Services;

/// <summary>Подписи для групп «папок-пожирателей» (<see cref="Models.SuspectGroup"/>): чем пересоздаётся и что учесть перед удалением.</summary>
public static class BuildArtifactCatalog
{
    public const string FallbackDescription = "Артефакты сборки — пересоздаются сборкой";

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Rust target"] = "Артефакты сборки Rust — пересоздаются командой cargo build",
        ["Maven target"] = "Артефакты сборки Maven — пересоздаются командой mvn package",
        ["node_modules"] = "node_modules — восстанавливается npm install",
        ["Pods"] = "Pods — восстанавливается командой pod install",
        ["venv (Python)"] = "Виртуальное окружение Python — пересоздаётся (python -m venv и pip install -r requirements.txt)",
        ["Unity Library"] = "Unity Library — переимпорт может занять часы",
        ["__pycache__"] = "Кэш байткода Python — создаётся заново при запуске",
        [".gradle"] = "Кэш Gradle — скачивается заново; проверьте gradle.properties",
        [".m2"] = "Репозиторий Maven — зависимости скачиваются заново; локально собранные (mvn install) не восстановятся",
        [".cache"] = "Кэш приложения — обычно пересоздаётся",
    };

    /// <summary>Подпись для группы по её имени; для неизвестной группы — <see cref="FallbackDescription"/>.</summary>
    public static string Describe(string groupName)
        => Descriptions.TryGetValue(groupName, out var description) ? description : FallbackDescription;
}
