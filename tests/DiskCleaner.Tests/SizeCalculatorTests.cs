using System.IO;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class SizeCalculatorTests
{
    [Fact]
    public void Computes_total_size_and_file_count_recursively()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");        // 5 байт
        temp.CreateFile("sub/b.txt", "1234567890"); // 10 байт

        var calculator = new SizeCalculator(new ReparsePointGuard());
        var stats = calculator.ComputeStats(temp.Path, fileNamePatterns: null);

        Assert.Equal(15, stats.SizeBytes);
        Assert.Equal(2, stats.FileCount);
    }

    [Fact]
    public void Does_not_follow_symlinked_subdirectory()
    {
        using var temp = new TempTestDirectory();
        using var outside = new TempTestDirectory();
        outside.CreateFile("big.txt", new string('x', 1000));

        var linkPath = Path.Combine(temp.Path, "link-to-outside");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // среда без прав на создание symlink
        }

        temp.CreateFile("real.txt", "12345"); // 5 байт — должны посчитать только это

        var skipped = new List<Core.Models.SkippedItem>();
        var calculator = new SizeCalculator(new ReparsePointGuard());
        var stats = calculator.ComputeStats(temp.Path, fileNamePatterns: null, skipped);

        Assert.Equal(5, stats.SizeBytes);
        Assert.Equal(1, stats.FileCount);
        Assert.Contains(skipped, s => s.Reason == Core.Models.SkipReason.ReparsePoint);
    }

    [Fact]
    public void Empty_directory_has_zero_size_and_zero_files()
    {
        using var temp = new TempTestDirectory();
        var calculator = new SizeCalculator(new ReparsePointGuard());

        var stats = calculator.ComputeStats(temp.Path, fileNamePatterns: null);

        Assert.Equal(0, stats.SizeBytes);
        Assert.Equal(0, stats.FileCount);
    }

    [Fact]
    public void FileNamePatterns_restricts_to_matching_files_only()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("thumbcache_001.db", "12345");
        temp.CreateFile("thumbcache_002.db", "1234567890");
        temp.CreateFile("unrelated.txt", "should not be counted");

        var calculator = new SizeCalculator(new ReparsePointGuard());
        var stats = calculator.ComputeStats(temp.Path, fileNamePatterns: ["thumbcache_*.db"]);

        Assert.Equal(15, stats.SizeBytes);
        Assert.Equal(2, stats.FileCount);
    }

    [Fact]
    public void MatchesWildcard_is_case_insensitive_and_supports_star()
    {
        Assert.True(SizeCalculator.MatchesWildcard("Thumbcache_001.DB", "thumbcache_*.db"));
        Assert.False(SizeCalculator.MatchesWildcard("other.db", "thumbcache_*.db"));
    }
}
