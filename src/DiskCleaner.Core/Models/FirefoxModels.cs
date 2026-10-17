namespace DiskCleaner.Core.Models;

/// <summary>Профиль Firefox (папка в %APPDATA%\Mozilla\Firefox\Profiles).</summary>
public sealed class FirefoxProfile
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>Здесь Firefox хранит данные сайтов: по одной папке на сайт («источник»).</summary>
    public string StorageDefaultPath => System.IO.Path.Combine(Path, "storage", "default");
}

/// <summary>Тип данных сайта внутри папки источника.</summary>
public enum FirefoxStorageKind
{
    /// <summary>Папка idb — локальная база сайта (IndexedDB).</summary>
    IndexedDb,

    /// <summary>Папка cache — Cache API (кэш, который сайт ведёт сам).</summary>
    CacheApi,

    /// <summary>Папка ls — localStorage: настройки сайта и часто токены входа.</summary>
    LocalStorage,

    /// <summary>Папка fs — файловая система сайта (OPFS): файлы, которые сайт хранит у пользователя.</summary>
    FileSystem,
}

/// <summary>Чем рискует пользователь, удаляя этот тип данных.</summary>
public enum FirefoxStorageRisk
{
    Low,
    Medium,
    High,
}

/// <summary>Один тип данных одного сайта (например, idb у tiktok.com).</summary>
public sealed class FirefoxStoragePart
{
    public required FirefoxStorageKind Kind { get; init; }

    /// <summary>Имя папки на диске: idb, cache, ls или fs.</summary>
    public required string FolderName { get; init; }

    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public FirefoxStorageRisk Risk { get; init; }

    /// <summary>Что произойдёт после удаления — показывается пользователю.</summary>
    public string Note { get; init; } = "";

    /// <summary>Крупный и низкорисковый тип данных (кэш или IndexedDB от порога) — кандидат на «выбрать рекомендуемые».</summary>
    public bool IsSuggested { get; init; }

    public string SizeDisplay => CleanupTarget.FormatSize(SizeBytes);
}

/// <summary>Данные одного сайта (источника) в профиле.</summary>
public sealed class FirefoxSiteEntry
{
    public required string ProfilePath { get; init; }

    /// <summary>Имя папки источника, например https+++www.tiktok.com.</summary>
    public required string OriginFolderName { get; init; }

    public required string OriginPath { get; init; }
    public required string Scheme { get; init; }
    public required string Host { get; init; }
    public int? Port { get; init; }

    /// <summary>Номер контейнера Firefox (userContextId), если сайт открыт в контейнере.</summary>
    public int? ContainerId { get; init; }

    /// <summary>Ключ раздела (partitionKey) для сторонних данных, если есть.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>Данные расширения браузера (moz-extension) — никогда не удаляются.</summary>
    public bool IsExtension { get; init; }

    public required string DisplayName { get; init; }
    public IReadOnlyList<FirefoxStoragePart> Parts { get; init; } = Array.Empty<FirefoxStoragePart>();

    /// <summary>Подпапки, которых модуль не знает (например sdb): их размер учтён, но удалять их нельзя.</summary>
    public IReadOnlyList<string> OtherFolders { get; init; } = Array.Empty<string>();
    public long OtherBytes { get; init; }

    public long TotalBytes => Parts.Sum(p => p.SizeBytes) + OtherBytes;
    public DateTime? LastModifiedUtc { get; init; }
    public bool CanDelete => !IsExtension && Parts.Count > 0;
    public string TotalDisplay => CleanupTarget.FormatSize(TotalBytes);
}

/// <summary>Результат анализа данных сайтов в одном профиле.</summary>
public sealed class FirefoxStorageReport
{
    public required FirefoxProfile Profile { get; init; }
    public IReadOnlyList<FirefoxSiteEntry> Sites { get; init; } = Array.Empty<FirefoxSiteEntry>();

    /// <summary>Сколько мелких сайтов (меньше порога MinSiteBytes) скрыто из списка и сколько они занимают.</summary>
    public int HiddenSmallSitesCount { get; init; }
    public long HiddenSmallSitesBytes { get; init; }

    public long TotalBytes => Sites.Sum(s => s.TotalBytes) + HiddenSmallSitesBytes;
    public bool WasCancelled { get; init; }
    public IReadOnlyList<SkippedItem> Skipped { get; init; } = Array.Empty<SkippedItem>();
}

public sealed class FirefoxAnalysisOptions
{
    private const long Mb = 1024L * 1024L;

    /// <summary>Сайты меньше этого размера в список не попадают (их сотни и они не интересны).</summary>
    public long MinSiteBytes { get; init; } = 1 * Mb;

    /// <summary>Кэш и IndexedDB от этого размера помечаются как рекомендуемые к очистке.</summary>
    public long SuggestedMinBytes { get; init; } = 100 * Mb;
}

/// <summary>Что чистить: сайт и выбранные типы его данных.</summary>
public sealed record FirefoxSiteSelection(FirefoxSiteEntry Site, IReadOnlyList<FirefoxStoragePart> Parts);

public sealed class FirefoxCleanupOptions
{
    /// <summary>По умолчанию ничего не удаляется — только проверка и подсчёт.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Отправлять в Корзину (место на диске при этом не освобождается, пока Корзина не очищена).</summary>
    public bool UseRecycleBin { get; init; }

    /// <summary>Пользователь явно подтвердил удаление данных сайтов (они лежат внутри профиля браузера).</summary>
    public bool Confirmed { get; init; }

    /// <summary>Если выбраны все папки сайта — удалять папку сайта целиком, а не оставлять пустую оболочку.</summary>
    public bool RemoveEmptyOriginFolders { get; init; } = true;
}
