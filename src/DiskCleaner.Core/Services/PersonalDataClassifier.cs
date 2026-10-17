using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Определяет личные данные по расширению файла и по составу папки. Только по именам и размерам:
/// содержимое файлов не читается. Расширение .ts намеренно не входит в видео (это TypeScript).
/// </summary>
public static class PersonalDataClassifier
{
    private static readonly Dictionary<string, PersonalDataCategory> ByExtension = BuildExtensionMap();

    private static Dictionary<string, PersonalDataCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, PersonalDataCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(PersonalDataCategory category, params string[] extensions)
        {
            foreach (var extension in extensions)
                map[extension] = category;
        }

        Add(PersonalDataCategory.Video,
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".vob", ".3gp", ".ogv");

        Add(PersonalDataCategory.Audio,
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".ape", ".alac");

        Add(PersonalDataCategory.Image,
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".psd", ".xcf",
            // RAW
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".raw");

        Add(PersonalDataCategory.Document,
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".odt", ".ods", ".odp", ".rtf", ".epub", ".djvu");

        Add(PersonalDataCategory.Archive,
            ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso");

        Add(PersonalDataCategory.VmImagesAndVaults,
            ".vhd", ".vhdx", ".vmdk", ".vdi", ".kdbx", ".pst");

        return map;
    }

    /// <summary>Категория личных данных по расширению имени или пути файла; null, если расширение не из списка.</summary>
    public static PersonalDataCategory? Classify(string? fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath))
            return null;

        var extension = Path.GetExtension(fileNameOrPath);
        if (string.IsNullOrEmpty(extension))
            return null;

        return ByExtension.TryGetValue(extension, out var category) ? category : null;
    }

    public static string DisplayName(PersonalDataCategory category) => category switch
    {
        PersonalDataCategory.Video => "видео",
        PersonalDataCategory.Audio => "аудио",
        PersonalDataCategory.Image => "изображения",
        PersonalDataCategory.Document => "документы",
        PersonalDataCategory.Archive => "архивы",
        PersonalDataCategory.VmImagesAndVaults => "образы ВМ, хранилища паролей, почта",
        _ => category.ToString(),
    };

    /// <summary>
    /// Какие правила делают папку «личной». <paramref name="stats"/> — статистика поддерева (без игнорируемых
    /// подпапок), <paramref name="folderBytes"/> — полный размер папки. Пороги берутся из <paramref name="options"/>.
    /// </summary>
    public static PersonalFolderReason GetFolderReasons(PersonalStats stats, long folderBytes, DiskAnalysisOptions? options = null)
    {
        options ??= new DiskAnalysisOptions();
        var reasons = PersonalFolderReason.None;

        if (stats.CountedFiles >= options.PersonalMinFileCount)
            reasons |= PersonalFolderReason.ByFileCount;

        if (folderBytes > 0 &&
            folderBytes >= options.PersonalShareMinFolderBytes &&
            (double)stats.TotalBytes / folderBytes >= options.PersonalMinShare)
            reasons |= PersonalFolderReason.ByShare;

        if (stats.TotalBytes >= options.PersonalAbsoluteBytes)
            reasons |= PersonalFolderReason.ByTotalSize;

        return reasons;
    }

    /// <summary>Пояснение для пользователя: почему папка считается личной и что в ней нашлось.</summary>
    public static string DescribeFolder(PersonalFolderInfo info)
    {
        var parts = new List<string>();
        foreach (var category in Enum.GetValues<PersonalDataCategory>()
                     .Select(c => (Category: c, Totals: info.Stats.Get(c)))
                     .Where(x => x.Totals.Files > 0)
                     .OrderByDescending(x => x.Totals.Bytes)
                     .Take(3))
        {
            parts.Add($"{DisplayName(category.Category)}: {category.Totals.Files} шт., {CleanupTarget.FormatSize(category.Totals.Bytes)}");
        }

        return $"личные файлы по составу папки ({string.Join("; ", parts)})";
    }
}
