using System.IO;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>Сводка для окна подтверждения: число файлов, размер, диапазон дат, самые большие файлы.</summary>
public sealed class SensitiveSummaryBuilderTests
{
    private static SensitiveSummaryBuilder NewBuilder() => new(new ReparsePointGuard());

    private static string Write(TempTestDirectory dir, string relative, int bytes, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modifiedUtc is { } stamp)
            File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }

    [Fact]
    public void Counts_files_and_total_size_across_nested_folders()
    {
        using var dir = new TempTestDirectory();
        Write(dir, "a.bin", 100);
        Write(dir, Path.Combine("sub", "b.bin"), 200);
        Write(dir, Path.Combine("sub", "deep", "c.bin"), 300);

        var summary = NewBuilder().Build(new[] { dir.Path });

        Assert.Equal(3, summary.FileCount);
        Assert.Equal(600, summary.TotalBytes);
        Assert.Equal(0, summary.UnreadableCount);
    }

    [Fact]
    public void Sums_several_paths_folders_and_single_files_together()
    {
        using var dir = new TempTestDirectory();
        Write(dir, Path.Combine("one", "a.bin"), 10);
        Write(dir, Path.Combine("two", "b.bin"), 20);
        var single = Write(dir, "single.bin", 40);

        var summary = NewBuilder().Build(new[] { Path.Combine(dir.Path, "one"), Path.Combine(dir.Path, "two"), single });

        Assert.Equal(3, summary.FileCount);
        Assert.Equal(70, summary.TotalBytes);
    }

    [Fact]
    public void Date_range_is_the_oldest_and_newest_modification_time()
    {
        using var dir = new TempTestDirectory();
        var oldest = new DateTime(2019, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        var middle = new DateTime(2022, 7, 1, 12, 30, 0, DateTimeKind.Utc);
        var newest = new DateTime(2024, 11, 20, 8, 15, 0, DateTimeKind.Utc);
        Write(dir, "m.bin", 1, middle);
        Write(dir, Path.Combine("sub", "o.bin"), 1, oldest);
        Write(dir, Path.Combine("sub", "n.bin"), 1, newest);

        var summary = NewBuilder().Build(new[] { dir.Path });

        Assert.Equal(oldest, summary.OldestModifiedUtc);
        Assert.Equal(newest, summary.NewestModifiedUtc);
    }

    [Fact]
    public void Largest_files_are_top_five_in_descending_order_by_default()
    {
        using var dir = new TempTestDirectory();
        for (var i = 1; i <= 8; i++)
            Write(dir, $"f{i}.bin", i * 100);

        var summary = NewBuilder().Build(new[] { dir.Path });

        Assert.Equal(SensitiveSummaryBuilder.DefaultTopCount, summary.LargestFiles.Count);
        Assert.Equal(new long[] { 800, 700, 600, 500, 400 }, summary.LargestFiles.Select(f => f.SizeBytes).ToArray());
        Assert.Equal("f8.bin", Path.GetFileName(summary.LargestFiles[0].Path));
        Assert.Equal(8, summary.FileCount);
    }

    [Fact]
    public void Top_count_is_respected_and_zero_returns_no_largest_files_but_still_counts()
    {
        using var dir = new TempTestDirectory();
        for (var i = 1; i <= 4; i++)
            Write(dir, $"f{i}.bin", i * 10);

        var two = NewBuilder().Build(new[] { dir.Path }, topCount: 2);
        var none = NewBuilder().Build(new[] { dir.Path }, topCount: 0);

        Assert.Equal(new long[] { 40, 30 }, two.LargestFiles.Select(f => f.SizeBytes).ToArray());
        Assert.Empty(none.LargestFiles);
        Assert.Equal(4, none.FileCount);
        Assert.Equal(100, none.TotalBytes);
    }

    [Fact]
    public void Fewer_files_than_top_count_returns_all_of_them()
    {
        using var dir = new TempTestDirectory();
        Write(dir, "a.bin", 5);
        Write(dir, "b.bin", 7);

        var summary = NewBuilder().Build(new[] { dir.Path });

        Assert.Equal(2, summary.LargestFiles.Count);
        Assert.Equal(7, summary.LargestFiles[0].SizeBytes);
    }

    [Fact]
    public void Missing_path_is_ignored_without_error()
    {
        using var dir = new TempTestDirectory();
        Write(dir, "a.bin", 9);

        var summary = NewBuilder().Build(new[] { Path.Combine(dir.Path, "нет-такого"), dir.Path });

        Assert.Equal(1, summary.FileCount);
        Assert.Equal(9, summary.TotalBytes);
    }

    [Fact]
    public void Empty_input_and_empty_folder_give_an_empty_summary()
    {
        using var dir = new TempTestDirectory();

        var none = NewBuilder().Build(Array.Empty<string>());
        var empty = NewBuilder().Build(new[] { dir.Path });

        foreach (var summary in new[] { none, empty })
        {
            Assert.Equal(0, summary.FileCount);
            Assert.Equal(0, summary.TotalBytes);
            Assert.Null(summary.OldestModifiedUtc);
            Assert.Null(summary.NewestModifiedUtc);
            Assert.Empty(summary.LargestFiles);
        }
    }

    [Fact]
    public void Cancelled_token_throws_operation_canceled()
    {
        using var dir = new TempTestDirectory();
        Write(dir, "a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => NewBuilder().Build(new[] { dir.Path }, cancellationToken: cts.Token));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SensitiveSummaryBuilder(null!));
        Assert.Throws<ArgumentNullException>(() => NewBuilder().Build(null!));
    }
}
