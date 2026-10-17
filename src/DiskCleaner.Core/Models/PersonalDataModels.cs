namespace DiskCleaner.Core.Models;

/// <summary>Категории личных данных, определяемые по расширению файла.</summary>
public enum PersonalDataCategory
{
    Video,
    Audio,
    Image,
    Document,
    Archive,

    /// <summary>Образы виртуальных машин, хранилища паролей, почтовые базы (vhd, vhdx, vmdk, vdi, kdbx, pst).</summary>
    VmImagesAndVaults,
}

/// <summary>Число файлов и их суммарный размер в одной категории.</summary>
public readonly record struct CategoryTotals(long Files, long Bytes);

/// <summary>
/// Накопленная статистика личных файлов по категориям (с накоплением по вложенным папкам).
/// Значение-структура: копируется дёшево, поэтому разность «после минус до» даёт статистику поддерева без аллокаций.
/// </summary>
public readonly record struct PersonalStats(
    CategoryTotals Video,
    CategoryTotals Audio,
    CategoryTotals Image,
    CategoryTotals Document,
    CategoryTotals Archive,
    CategoryTotals VmImagesAndVaults,
    long CountedFiles)
{
    /// <summary>Сколько личных файлов набралось для правила «количество» (только файлы не меньше порога размера).</summary>
    public long TotalFiles => Video.Files + Audio.Files + Image.Files + Document.Files + Archive.Files + VmImagesAndVaults.Files;

    public long TotalBytes => Video.Bytes + Audio.Bytes + Image.Bytes + Document.Bytes + Archive.Bytes + VmImagesAndVaults.Bytes;

    public CategoryTotals Get(PersonalDataCategory category) => category switch
    {
        PersonalDataCategory.Video => Video,
        PersonalDataCategory.Audio => Audio,
        PersonalDataCategory.Image => Image,
        PersonalDataCategory.Document => Document,
        PersonalDataCategory.Archive => Archive,
        PersonalDataCategory.VmImagesAndVaults => VmImagesAndVaults,
        _ => default,
    };

    /// <summary>Возвращает новую статистику с добавленным файлом. <paramref name="counted"/> — файл учитывается в правиле «количество».</summary>
    public PersonalStats Add(PersonalDataCategory category, long bytes, bool counted)
    {
        var current = Get(category);
        var updated = new CategoryTotals(current.Files + 1, current.Bytes + bytes);
        var countedFiles = CountedFiles + (counted ? 1 : 0);

        return category switch
        {
            PersonalDataCategory.Video => this with { Video = updated, CountedFiles = countedFiles },
            PersonalDataCategory.Audio => this with { Audio = updated, CountedFiles = countedFiles },
            PersonalDataCategory.Image => this with { Image = updated, CountedFiles = countedFiles },
            PersonalDataCategory.Document => this with { Document = updated, CountedFiles = countedFiles },
            PersonalDataCategory.Archive => this with { Archive = updated, CountedFiles = countedFiles },
            PersonalDataCategory.VmImagesAndVaults => this with { VmImagesAndVaults = updated, CountedFiles = countedFiles },
            _ => this,
        };
    }

    public static PersonalStats operator -(PersonalStats a, PersonalStats b) => new(
        Sub(a.Video, b.Video),
        Sub(a.Audio, b.Audio),
        Sub(a.Image, b.Image),
        Sub(a.Document, b.Document),
        Sub(a.Archive, b.Archive),
        Sub(a.VmImagesAndVaults, b.VmImagesAndVaults),
        a.CountedFiles - b.CountedFiles);

    private static CategoryTotals Sub(CategoryTotals a, CategoryTotals b) => new(a.Files - b.Files, a.Bytes - b.Bytes);
}

/// <summary>Почему папка признана «личной» по содержимому.</summary>
[Flags]
public enum PersonalFolderReason
{
    None = 0,

    /// <summary>Не меньше заданного числа личных файлов (учитываются файлы от порога размера).</summary>
    ByFileCount = 1,

    /// <summary>Личные файлы занимают не меньше заданной доли размера папки (и папка не меньше минимального размера).</summary>
    ByShare = 2,

    /// <summary>Личных файлов суммарно не меньше абсолютного порога размера, независимо от доли.</summary>
    ByTotalSize = 4,
}

/// <summary>Папка, признанная личной по результатам анализа диска.</summary>
public sealed record PersonalFolderInfo(string Path, PersonalStats Stats, long FolderBytes, PersonalFolderReason Reasons)
{
    /// <summary>
    /// Личным считается и всё внутри папки — только если папка в основном состоит из личных файлов (правило доли).
    /// Для правил «количество» и «суммарный размер» вложенное не наследуется: иначе накопление по предкам
    /// сделало бы «личным» весь диск.
    /// </summary>
    public bool InheritsToChildren => Reasons.HasFlag(PersonalFolderReason.ByShare);
}
