namespace DiskCleaner.Core.Models;

/// <summary>Файл или папка в результатах анализа.</summary>
public sealed class AnalyzedItem
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required bool IsDirectory { get; init; }

    /// <summary>Глубина от корня сканирования: корень = 0.</summary>
    public required int Depth { get; init; }

    /// <summary>
    /// Для файла — время последней записи; для папки — самое свежее время записи среди
    /// всех файлов внутри (null, если внутри нет файлов). Помогает понять, «пользуются ли этим».
    /// </summary>
    public DateTime? LastModifiedUtc { get; init; }

    public string SizeDisplay => CleanupTarget.FormatSize(SizeBytes);
}

/// <summary>Известное место, которое обычно можно чистить (или нельзя трогать — см. <see cref="Hint"/>).</summary>
public sealed record KnownPlaceDefinition(string Label, string Path, string? Hint = null);

/// <summary>Найденное известное место с реальным размером.</summary>
public sealed class KnownPlaceResult
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public string? Hint { get; init; }
    public DateTime? LastModifiedUtc { get; init; }

    public string SizeDisplay => CleanupTarget.FormatSize(SizeBytes);
}

/// <summary>Группа однотипных папок-пожирателей (node_modules, .gradle, Unity Library и т.п.).</summary>
public sealed class SuspectGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<AnalyzedItem> Items { get; init; }
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public string TotalDisplay => CleanupTarget.FormatSize(TotalBytes);
}

public sealed class ExtensionStat
{
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public string SizeDisplay => CleanupTarget.FormatSize(SizeBytes);
}

/// <summary>Прогресс сканирования для UI.</summary>
public sealed record DiskAnalysisProgress(long FilesScanned, long DirectoriesScanned, string CurrentPath);

/// <summary>Полный результат анализа диска. Анализ только читает — ничего не удаляет.</summary>
public sealed class DiskAnalysisResult
{
    public required string RootPath { get; init; }

    public long? DiskTotalBytes { get; init; }
    public long? DiskFreeBytes { get; init; }
    public long? DiskUsedBytes => DiskTotalBytes is null || DiskFreeBytes is null ? null : DiskTotalBytes - DiskFreeBytes;

    /// <summary>Сколько байт реально прочитано сканером (без ссылок и недоступных папок).</summary>
    public long ScannedBytes { get; init; }
    public long FilesScanned { get; init; }
    public long DirectoriesScanned { get; init; }
    public TimeSpan Elapsed { get; init; }

    public bool IsAdministrator { get; init; }

    /// <summary>true — пользователь отменил сканирование, данные частичные.</summary>
    public bool WasCancelled { get; init; }

    public int AccessDeniedCount { get; init; }
    public int ReparsePointsSkippedCount { get; init; }
    public int IoErrorCount { get; init; }

    public IReadOnlyList<AnalyzedItem> TopLevelFolders { get; init; } = Array.Empty<AnalyzedItem>();
    public IReadOnlyList<AnalyzedItem> RootFiles { get; init; } = Array.Empty<AnalyzedItem>();
    public IReadOnlyList<KnownPlaceResult> KnownPlaces { get; init; } = Array.Empty<KnownPlaceResult>();
    public IReadOnlyList<AnalyzedItem> HeaviestDirectories { get; init; } = Array.Empty<AnalyzedItem>();
    public IReadOnlyList<AnalyzedItem> HeaviestFiles { get; init; } = Array.Empty<AnalyzedItem>();
    public IReadOnlyList<SuspectGroup> SuspectGroups { get; init; } = Array.Empty<SuspectGroup>();
    public IReadOnlyList<ExtensionStat> TopExtensions { get; init; } = Array.Empty<ExtensionStat>();
    public IReadOnlyList<SkippedItem> SkippedSamples { get; init; } = Array.Empty<SkippedItem>();
}
