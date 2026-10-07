using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class DiskAnalyzerTests
{
    // Маленькие пороги, чтобы тестам хватало нескольких байт.
    private static DiskAnalysisOptions SmallOptions(
        long bigFile = 100, bool collapse = true, int minDepth = 1, int topFiles = 40) => new()
    {
        MinRecordedDirBytes = 1,
        BigDirBytes = 1,
        BigFileBytes = bigFile,
        MinReportedDepth = minDepth,
        MaxReportedDepth = 5,
        CollapseSingleChildChains = collapse,
        TopFilesCount = topFiles,
    };

    private static DiskAnalyzer NewAnalyzer(IEnumerable<KnownPlaceDefinition>? known = null)
        => new(new ReparsePointGuard(), known ?? Array.Empty<KnownPlaceDefinition>());

    [Fact]
    public void Computes_total_size_and_counts_recursively()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");          // 5 байт
        temp.CreateFile("sub/b.txt", "1234567890"); // 10 байт

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        Assert.Equal(15, result.ScannedBytes);
        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(2, result.DirectoriesScanned); // корень + sub
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public void Heaviest_files_contain_only_files_above_threshold_sorted_desc()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("small.bin", new string('x', 10));
        temp.CreateFile("mid.bin", new string('x', 200));
        temp.CreateFile("sub/big.bin", new string('x', 500));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions(bigFile: 100));

        Assert.Equal(2, result.HeaviestFiles.Count);
        Assert.Equal(500, result.HeaviestFiles[0].SizeBytes);
        Assert.Equal(200, result.HeaviestFiles[1].SizeBytes);
        Assert.DoesNotContain(result.HeaviestFiles, f => f.Path.EndsWith("small.bin"));
    }

    [Fact]
    public void Heaviest_files_are_limited_to_top_count_keeping_the_biggest()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("f1.bin", new string('x', 100));
        temp.CreateFile("f2.bin", new string('x', 300));
        temp.CreateFile("f3.bin", new string('x', 200));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions(bigFile: 100, topFiles: 2));

        Assert.Equal(2, result.HeaviestFiles.Count);
        Assert.Equal(300, result.HeaviestFiles[0].SizeBytes);
        Assert.Equal(200, result.HeaviestFiles[1].SizeBytes);
    }

    [Fact]
    public void Files_in_root_are_listed_separately()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("hiberfil.sys", new string('x', 50));
        temp.CreateFile("sub/other.bin", new string('x', 10));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        Assert.Single(result.RootFiles);
        Assert.Contains(result.RootFiles, f => f.Path.EndsWith("hiberfil.sys"));
    }

    [Fact]
    public void Does_not_follow_symlinked_directory()
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

        temp.CreateFile("real.txt", "12345");

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        Assert.Equal(5, result.ScannedBytes);
        Assert.Equal(1, result.ReparsePointsSkippedCount);
    }

    [Fact]
    public void Detects_node_modules_and_does_not_count_nested_ones_twice()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("proj/node_modules/pkg/a.js", new string('x', 100));
        temp.CreateFile("proj/node_modules/pkg/node_modules/inner/b.js", new string('x', 50));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("node_modules", group.Name);
        Assert.Single(group.Items);
        Assert.Equal(150, group.TotalBytes);
    }

    [Fact]
    public void Venv_is_suspect_only_with_pyvenv_cfg()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("realenv/venv/pyvenv.cfg", "home = x");
        temp.CreateFile("realenv/venv/lib/a.py", new string('x', 100));
        temp.CreateFile("pythonlib/Lib/venv/__init__.py", new string('x', 100)); // модуль stdlib, не окружение

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("venv (Python)", group.Name);
        Assert.Single(group.Items);
        Assert.Contains(group.Items, i => i.Path.Contains("realenv"));
    }

    [Fact]
    public void Library_is_suspect_only_inside_unity_project()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("unityproj/Library/cache.bin", new string('x', 100));
        temp.CreateSubDirectory("unityproj/Assets");
        temp.CreateSubDirectory("unityproj/ProjectSettings");
        temp.CreateFile("other/Library/data.bin", new string('x', 100)); // просто папка Library

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("Unity Library", group.Name);
        Assert.Contains(group.Items, i => i.Path.Contains("unityproj"));
    }

    [Fact]
    public void Known_places_are_taken_from_scanned_directories()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("AppCache/data.bin", new string('x', 300));
        temp.CreateFile("Elsewhere/data.bin", new string('x', 100));

        var known = new[]
        {
            new KnownPlaceDefinition("Кэш приложения", Path.Combine(temp.Path, "AppCache")),
            new KnownPlaceDefinition("Не существует", Path.Combine(temp.Path, "Missing")),
        };

        var result = NewAnalyzer(known).Analyze(temp.Path, SmallOptions());

        var place = Assert.Single(result.KnownPlaces);
        Assert.Equal("Кэш приложения", place.Label);
        Assert.Equal(300, place.SizeBytes);
    }

    [Fact]
    public void Single_child_chains_are_collapsed_to_the_deepest_directory()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a/b/c/big.bin", new string('x', 500));

        var collapsed = NewAnalyzer().Analyze(temp.Path, SmallOptions(collapse: true));
        Assert.Single(collapsed.HeaviestDirectories);
        Assert.Contains(collapsed.HeaviestDirectories, d => d.Path.EndsWith("c"));

        var full = NewAnalyzer().Analyze(temp.Path, SmallOptions(collapse: false));
        Assert.Equal(3, full.HeaviestDirectories.Count);
    }

    [Fact]
    public void Directory_item_reports_newest_file_time()
    {
        using var temp = new TempTestDirectory();
        var oldFile = temp.CreateFile("d/old.txt", "1");
        var newFile = temp.CreateFile("d/new.txt", "1");
        File.SetLastWriteTimeUtc(oldFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newFile, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());

        var dir = Assert.Single(result.TopLevelFolders);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), dir.LastModifiedUtc);
    }

    [Fact]
    public void Cancelled_token_returns_partial_result_without_throwing()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("a.txt", "12345");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions(), progress: null, cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
    }

    [Fact]
    public void Missing_root_throws_directory_not_found()
    {
        var analyzer = NewAnalyzer();
        var missing = Path.Combine(Path.GetTempPath(), "DiskCleanerTests_missing_" + Guid.NewGuid().ToString("N"));

        // Не найденный корень — ошибка вызывающего кода, а не «пустой результат».
        // Analyze перехватывает только отмену, поэтому исключение должно дойти наверх.
        try
        {
            analyzer.Analyze(missing, SmallOptions());
            Assert.True(false, "Ожидалось DirectoryNotFoundException.");
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Report_formatter_contains_main_sections()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("sub/big.bin", new string('x', 500));

        var result = NewAnalyzer().Analyze(temp.Path, SmallOptions());
        var text = DiskAnalysisReportFormatter.Format(result);

        Assert.Contains("ОТЧЁТ ПО ДИСКУ", text);
        Assert.Contains("ТЯЖЁЛЫХ ФАЙЛОВ", text);
        Assert.Contains("big.bin", text);
    }
}
