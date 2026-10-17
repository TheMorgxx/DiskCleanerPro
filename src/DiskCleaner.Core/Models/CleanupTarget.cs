namespace DiskCleaner.Core.Models;

/// <summary>
/// Конкретный найденный на диске путь, готовый к показу пользователю и, при
/// подтверждении, к очистке. Один CleanupRule может породить несколько
/// CleanupTarget (например, правило браузерных кэшей — по одному на каждый
/// найденный профиль/браузер).
/// </summary>
public sealed class CleanupTarget
{
    public required string RuleName { get; init; }
    public required CleanupCategory Category { get; init; }
    public string? AppName { get; init; }
    public required string DisplayName { get; init; }
    public required string FullPath { get; init; }

    /// <summary>Буква диска без двоеточия/слеша, например "C" или "D".</summary>
    public required string DriveLetter { get; init; }

    public required string Description { get; init; }
    public required CleanupRiskLevel RiskLevel { get; init; }
    public bool RequiresAdmin { get; init; }

    /// <summary>Если заданы — при удалении будут затронуты только файлы, совпадающие с этими wildcard-шаблонами.</summary>
    public string[]? FileNamePatterns { get; init; }

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public DateTime? LastModifiedUtc { get; set; }

    /// <summary>Путь прошёл проверку PathSafetyService.</summary>
    public bool IsSafeToClean { get; set; }
    public string? UnsafeReason { get; set; }

    /// <summary>
    /// true, если правило требует прав администратора, а текущий процесс их
    /// не имеет — в этом случае элемент показывается в UI, но недоступен для
    /// выбора, со статусом "requires admin".
    /// </summary>
    public bool IsBlockedNoAdmin { get; set; }

    public string SizeDisplay => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }
}
