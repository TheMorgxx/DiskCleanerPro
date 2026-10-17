using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Индекс личных папок, найденных анализатором диска. Заполняется результатом анализа (в том числе частичным
/// при отмене) и читается <see cref="ManualDeletionService"/> при каждой проверке пути. Потокобезопасен:
/// читатели видят неизменяемый снимок, писатели подменяют его целиком под блокировкой.
/// В индексе только папки, попавшие в результат анализа; для остальных папок содержимое не проверяется.
/// </summary>
public sealed class PersonalContentIndex
{
    private static readonly IReadOnlyDictionary<string, PersonalFolderInfo> Empty =
        new Dictionary<string, PersonalFolderInfo>(StringComparer.OrdinalIgnoreCase);

    private readonly object _writeLock = new();
    private readonly Dictionary<string, IReadOnlyList<PersonalFolderInfo>> _byRoot = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, PersonalFolderInfo> _merged = Empty;

    public int Count => _merged.Count;

    /// <summary>Заменяет записи анализа указанного корня. Записи других корней остаются.</summary>
    public void ReplaceForRoot(string rootPath, IEnumerable<PersonalFolderInfo> folders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(folders);

        var rootKey = Key(rootPath);
        var list = new List<PersonalFolderInfo>();
        foreach (var folder in folders)
        {
            var key = Key(folder.Path);
            if (key is not null)
                list.Add(folder with { Path = key });
        }

        if (rootKey is null)
            return;

        lock (_writeLock)
        {
            _byRoot[rootKey] = list;

            var merged = new Dictionary<string, PersonalFolderInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entries in _byRoot.Values)
            {
                foreach (var entry in entries)
                    merged[entry.Path] = entry;
            }

            _merged = merged;
        }
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            _byRoot.Clear();
            _merged = Empty;
        }
    }

    /// <summary>Запись о самой папке <paramref name="path"/>, если она признана личной.</summary>
    public bool TryGetFolder(string path, out PersonalFolderInfo? info)
    {
        info = null;
        var key = Key(path);
        return key is not null && _merged.TryGetValue(key, out info);
    }

    /// <summary>
    /// Ближайшая папка выше <paramref name="path"/>, личная по правилу доли: всё внутри неё считается личным.
    /// Сам путь не рассматривается.
    /// </summary>
    public PersonalFolderInfo? FindInheritingAncestor(string path)
    {
        var key = Key(path);
        if (key is null)
            return null;

        var snapshot = _merged;
        if (snapshot.Count == 0)
            return null;

        var directory = Path.GetDirectoryName(key);
        while (!string.IsNullOrEmpty(directory))
        {
            var ancestorKey = TrimSeparators(directory);
            if (snapshot.TryGetValue(ancestorKey, out var info) && info.InheritsToChildren)
                return info;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static string? Key(string path)
    {
        try
        {
            return TrimSeparators(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    // Корень диска ("D:\") остаётся с завершающим разделителем, как в DiskAnalyzer.
    private static string TrimSeparators(string path)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        return path.Length > rootLength
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }
}
