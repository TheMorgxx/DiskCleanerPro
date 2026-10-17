using System.IO;

namespace DiskCleaner.Core.Services;

public sealed record LargestFile(string Path, long SizeBytes);

/// <summary>Сводка по тому, что будет удалено: для окна подтверждения личных данных.</summary>
public sealed record SensitiveSummary(
    long FileCount,
    long TotalBytes,
    DateTime? OldestModifiedUtc,
    DateTime? NewestModifiedUtc,
    IReadOnlyList<LargestFile> LargestFiles,
    int UnreadableCount);

/// <summary>
/// Собирает сводку по выбранным путям: число файлов, суммарный размер, диапазон дат изменения и самые большие файлы.
/// Читает только метаданные (без содержимого), не заходит в symlink/junction. Предназначен для фонового потока.
/// </summary>
public sealed class SensitiveSummaryBuilder
{
    public const int DefaultTopCount = 5;

    private static readonly EnumerationOptions EnumerationOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    private readonly ReparsePointGuard _guard;

    public SensitiveSummaryBuilder(ReparsePointGuard guard)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    /// <exception cref="OperationCanceledException">Сборка отменена.</exception>
    public SensitiveSummary Build(IEnumerable<string> paths, int topCount = DefaultTopCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        long fileCount = 0;
        long totalBytes = 0;
        var unreadable = 0;
        DateTime? oldest = null;
        DateTime? newest = null;
        var top = new PriorityQueue<LargestFile, long>();

        void AddFile(string path, long length, DateTime modifiedUtc)
        {
            fileCount++;
            totalBytes += length;
            oldest = oldest is null || modifiedUtc < oldest ? modifiedUtc : oldest;
            newest = newest is null || modifiedUtc > newest ? modifiedUtc : newest;

            if (topCount <= 0)
                return;

            top.Enqueue(new LargestFile(path, length), length);
            if (top.Count > topCount)
                top.Dequeue(); // выкидываем самый маленький
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    if (_guard.IsReparsePoint(path))
                        continue; // удаляется как ссылка, содержимое цели не затрагивается

                    var pending = new Stack<string>();
                    pending.Push(path);

                    while (pending.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var directory = pending.Pop();

                        try
                        {
                            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EnumerationOpts))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                try
                                {
                                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                        continue;

                                    if (info is DirectoryInfo sub)
                                        pending.Push(sub.FullName);
                                    else if (info is FileInfo file)
                                        AddFile(file.FullName, file.Length, file.LastWriteTimeUtc);
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    unreadable++;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            unreadable++;
                        }
                    }
                }
                else if (File.Exists(path))
                {
                    var file = new FileInfo(path);
                    AddFile(file.FullName, file.Length, file.LastWriteTimeUtc);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        var largest = new List<LargestFile>();
        while (top.Count > 0)
            largest.Add(top.Dequeue());
        largest.Reverse(); // от большего к меньшему

        return new SensitiveSummary(fileCount, totalBytes, oldest, newest, largest, unreadable);
    }
}
