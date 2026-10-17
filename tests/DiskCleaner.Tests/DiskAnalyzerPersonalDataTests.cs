using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Анализатор диска и личные данные: накопление статистики по поддереву, пороги на реальных файлах, игнорируемые
/// поддеревья, известные места и группы папок-пожирателей, отмена, несколько корней. Файлы маленькие или пустые.
/// Крупные пороги (100 МБ, 1 ГБ) проверяются в PersonalDataClassifierTests; здесь они уменьшаются через
/// DiskAnalysisOptions, чтобы убедиться, что анализатор передаёт значения без искажений.
/// </summary>
public sealed class DiskAnalyzerPersonalDataTests
{
    private const long Kb = 1024;

    // Малые пороги: учитываются файлы от 1 байта, правила доли и абсолютного размера выключены,
    // остаётся правило «количество» (по умолчанию 5 файлов).
    private static DiskAnalysisOptions Small(
        int minCount = 5,
        long countMinBytes = 1,
        long shareMinFolder = long.MaxValue,
        long absolute = long.MaxValue,
        double share = 0.5) => new()
    {
        MinRecordedDirBytes = 1,
        BigDirBytes = 1,
        MinReportedDepth = 1,
        PersonalMinFileCount = minCount,
        PersonalCountMinFileBytes = countMinBytes,
        PersonalShareMinFolderBytes = shareMinFolder,
        PersonalAbsoluteBytes = absolute,
        PersonalMinShare = share,
    };

    private static DiskAnalysisResult Run(
        string root,
        DiskAnalysisOptions? options = null,
        IEnumerable<KnownPlaceDefinition>? known = null,
        IProgress<DiskAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => new DiskAnalyzer(new ReparsePointGuard(), known ?? Array.Empty<KnownPlaceDefinition>())
            .Analyze(root, options, progress, cancellationToken);

    private static void WriteFiles(string directory, string extension, int count, long bytes, string prefix = "f")
    {
        Directory.CreateDirectory(directory);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(directory, $"{prefix}{i}{extension}"), new byte[bytes]);
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static PersonalFolderInfo? Find(DiskAnalysisResult result, string path)
        => result.PersonalFolders.FirstOrDefault(f => string.Equals(Trim(f.Path), Trim(path), StringComparison.OrdinalIgnoreCase));

    private static bool Has(DiskAnalysisResult result, string path) => Find(result, path) is not null;

    // ------------------------------------------------------------------ правило «количество»: 19/20 и 31/32 КБ (реальные пороги)

    [Fact]
    public void Default_options_19_files_of_32_KB_are_not_personal_and_20_are()
    {
        using var nineteen = new TempTestDirectory();
        WriteFiles(Path.Combine(nineteen.Path, "photos"), ".jpg", 19, 32 * Kb);

        using var twenty = new TempTestDirectory();
        WriteFiles(Path.Combine(twenty.Path, "photos"), ".jpg", 20, 32 * Kb);

        var below = Run(nineteen.Path);
        var at = Run(twenty.Path);

        Assert.Empty(below.PersonalFolders);
        Assert.True(Has(at, Path.Combine(twenty.Path, "photos")));
        Assert.Equal(PersonalFolderReason.ByFileCount, Find(at, Path.Combine(twenty.Path, "photos"))!.Reasons);
    }

    [Fact]
    public void Default_options_files_of_31_KB_are_not_counted_and_32_KB_are()
    {
        using var small = new TempTestDirectory();
        WriteFiles(Path.Combine(small.Path, "photos"), ".jpg", 20, 32 * Kb - 1);

        using var enough = new TempTestDirectory();
        WriteFiles(Path.Combine(enough.Path, "photos"), ".jpg", 20, 32 * Kb);

        Assert.Empty(Run(small.Path).PersonalFolders);
        Assert.True(Has(Run(enough.Path), Path.Combine(enough.Path, "photos")));
    }

    [Fact]
    public void Default_options_mixed_sizes_count_only_files_from_32_KB()
    {
        using var temp = new TempTestDirectory();
        var photos = Path.Combine(temp.Path, "photos");
        WriteFiles(photos, ".jpg", 19, 32 * Kb, prefix: "big");
        WriteFiles(photos, ".jpg", 50, 32 * Kb - 1, prefix: "small");

        var result = Run(temp.Path);

        Assert.Empty(result.PersonalFolders);
    }

    [Fact]
    public void Count_rule_stats_describe_the_folder_contents()
    {
        using var temp = new TempTestDirectory();
        var media = Path.Combine(temp.Path, "media");
        WriteFiles(media, ".jpg", 4, 10, prefix: "p");
        WriteFiles(media, ".mp3", 2, 20, prefix: "a");
        WriteFiles(media, ".txt", 3, 100, prefix: "t");

        var info = Find(Run(temp.Path, Small()), media);

        Assert.NotNull(info);
        Assert.Equal(4, info!.Stats.Image.Files);
        Assert.Equal(40, info.Stats.Image.Bytes);
        Assert.Equal(2, info.Stats.Audio.Files);
        Assert.Equal(6, info.Stats.TotalFiles);
        Assert.Equal(6, info.Stats.CountedFiles);
        Assert.Equal(40 + 40 + 300, info.FolderBytes); // размер всей папки, включая нелюбые файлы
    }

    // ------------------------------------------------------------------ правила доли и абсолютного размера (уменьшенные пороги)

    [Fact]
    public void Share_rule_boundary_99_and_100_bytes_with_scaled_options()
    {
        var options = Small(minCount: int.MaxValue, shareMinFolder: 100);

        using var exact = new TempTestDirectory();      // 50 личных из 100 — граница и по размеру, и по доле
        WriteFiles(exact.Path, ".mp4", 1, 50, prefix: "v");
        WriteFiles(exact.Path, ".bin", 1, 50, prefix: "d");

        using var tooSmall = new TempTestDirectory();   // 99 из 99 — доля 100%, но папка меньше минимума
        WriteFiles(tooSmall.Path, ".mp4", 1, 99, prefix: "v");

        using var lowShare = new TempTestDirectory();   // 49 из 100 — размер подходит, доля 49%
        WriteFiles(lowShare.Path, ".mp4", 1, 49, prefix: "v");
        WriteFiles(lowShare.Path, ".bin", 1, 51, prefix: "d");

        Assert.Equal(PersonalFolderReason.ByShare, Find(Run(exact.Path, options), exact.Path)!.Reasons);
        Assert.Empty(Run(tooSmall.Path, options).PersonalFolders);
        Assert.Empty(Run(lowShare.Path, options).PersonalFolders);
    }

    [Fact]
    public void Absolute_rule_boundary_999_and_1000_bytes_with_scaled_options()
    {
        var options = Small(minCount: int.MaxValue, absolute: 1000);

        using var below = new TempTestDirectory();      // 999 личных среди 5000 прочих: доля мала, порог не достигнут
        WriteFiles(below.Path, ".mkv", 1, 999, prefix: "v");
        WriteFiles(below.Path, ".bin", 1, 5000, prefix: "d");

        using var at = new TempTestDirectory();
        WriteFiles(at.Path, ".mkv", 1, 1000, prefix: "v");
        WriteFiles(at.Path, ".bin", 1, 5000, prefix: "d");

        Assert.Empty(Run(below.Path, options).PersonalFolders);

        var info = Find(Run(at.Path, options), at.Path);
        Assert.NotNull(info);
        Assert.Equal(PersonalFolderReason.ByTotalSize, info!.Reasons);
        Assert.Equal(6000, info.FolderBytes);
    }

    // ------------------------------------------------------------------ накопление по вложенным папкам

    [Fact]
    public void Stats_accumulate_into_parent_folders_but_siblings_stay_separate()
    {
        using var temp = new TempTestDirectory();
        var media = Path.Combine(temp.Path, "media");
        var y2019 = Path.Combine(media, "2019");
        var y2020 = Path.Combine(media, "2020");
        WriteFiles(y2019, ".jpg", 3, 10, prefix: "p");
        WriteFiles(y2019, ".mp3", 1, 10, prefix: "a");
        WriteFiles(y2020, ".jpg", 3, 10, prefix: "p");
        WriteFiles(media, ".txt", 1, 10, prefix: "readme");

        var result = Run(temp.Path, Small(minCount: 5));

        Assert.False(Has(result, y2019));   // 4 личных — меньше порога
        Assert.False(Has(result, y2020));   // 3 личных
        var info = Find(result, media);
        Assert.NotNull(info);               // 4 + 3 = 7 при накоплении
        Assert.Equal(6, info!.Stats.Image.Files);
        Assert.Equal(1, info.Stats.Audio.Files);
        Assert.Equal(7, info.Stats.TotalFiles);
        Assert.True(Has(result, temp.Path)); // корень анализа тоже накопил 7
    }

    [Fact]
    public void Deep_chain_every_level_collects_the_files_of_the_leaf()
    {
        using var temp = new TempTestDirectory();
        var a = Path.Combine(temp.Path, "a");
        var b = Path.Combine(a, "b");
        var c = Path.Combine(b, "c");
        WriteFiles(c, ".png", 5, 10);

        var result = Run(temp.Path, Small(minCount: 5));

        Assert.True(Has(result, a));
        Assert.True(Has(result, b));
        Assert.True(Has(result, c));
        Assert.Equal(5, Find(result, a)!.Stats.Image.Files);
    }

    // ------------------------------------------------------------------ М1: игнорируемые поддеревья

    [Theory]
    [InlineData("node_modules")]
    [InlineData(".cache")]
    [InlineData(".git")]
    [InlineData("site-packages")]
    [InlineData("__pycache__")]
    [InlineData(".gradle")]
    [InlineData(".m2")]
    [InlineData("Pods")]
    [InlineData("venv")]
    [InlineData(".venv")]
    [InlineData("steamapps")]
    [InlineData("SteamLibrary")]
    [InlineData("Epic Games")]
    [InlineData("GOG Games")]
    [InlineData("WindowsApps")]
    [InlineData("Cache")]
    [InlineData("Code Cache")]
    [InlineData("GPUCache")]
    [InlineData("cache2")]
    [InlineData("ShaderCache")]
    public void Personal_files_inside_an_ignored_folder_make_nothing_personal(string ignoredName)
    {
        using var temp = new TempTestDirectory();
        var app = Path.Combine(temp.Path, "app");
        WriteFiles(Path.Combine(app, ignoredName, "inner"), ".jpg", 10, 1);
        WriteFiles(Path.Combine(app, "src"), ".cs", 3, 1);

        var result = Run(temp.Path, Small(minCount: 5));

        Assert.Empty(result.PersonalFolders);
    }

    [Fact]
    public void Ignored_folder_files_do_not_enter_the_parent_stats_while_a_personal_sibling_is_still_found()
    {
        using var temp = new TempTestDirectory();
        var app = Path.Combine(temp.Path, "app");
        var photos = Path.Combine(app, "photos");
        var ignoredInner = Path.Combine(app, "node_modules", "img");
        WriteFiles(photos, ".jpg", 10, 1);
        WriteFiles(ignoredInner, ".jpg", 10, 1);

        var result = Run(temp.Path, Small(minCount: 5));

        Assert.True(Has(result, photos));
        Assert.False(Has(result, ignoredInner));
        Assert.False(Has(result, Path.Combine(app, "node_modules")));
        Assert.Equal(10, Find(result, app)!.Stats.Image.Files);          // а не 20
        Assert.Equal(10, Find(result, temp.Path)!.Stats.Image.Files);
    }

    [Fact]
    public void Ignored_folder_is_not_personal_even_with_the_share_rule()
    {
        using var temp = new TempTestDirectory();
        WriteFiles(Path.Combine(temp.Path, "steamapps", "common"), ".ogg", 3, 1000);
        var options = Small(minCount: int.MaxValue, shareMinFolder: 100);

        Assert.Empty(Run(temp.Path, options).PersonalFolders);
    }

    // ------------------------------------------------------------------ известные места не переклассифицируются

    [Fact]
    public void Known_place_contents_are_not_reclassified_by_content_and_do_not_count_for_the_parent()
    {
        using var temp = new TempTestDirectory();
        var place = Path.Combine(temp.Path, "ProgramStuff");
        WriteFiles(Path.Combine(place, "images"), ".jpg", 10, 1);
        var known = new[] { new KnownPlaceDefinition("Кэш программы", place, null, true) };

        var withKnown = Run(temp.Path, Small(minCount: 5), known);
        var control = Run(temp.Path, Small(minCount: 5));

        Assert.Empty(withKnown.PersonalFolders);
        Assert.True(Has(control, Path.Combine(place, "images")));  // без известного места та же папка личная
        Assert.True(Has(control, place));
    }

    [Fact]
    public void Known_place_still_appears_in_known_places_and_keeps_the_regenerates_flag()
    {
        using var temp = new TempTestDirectory();
        var cache = Path.Combine(temp.Path, "SomeCache");
        var data = Path.Combine(temp.Path, "SomeData");
        WriteFiles(cache, ".bin", 1, 10);
        WriteFiles(data, ".bin", 1, 10);
        var known = new[]
        {
            new KnownPlaceDefinition("Кэш", cache, null, true),
            new KnownPlaceDefinition("Данные", data),
        };

        var result = Run(temp.Path, Small(), known);

        Assert.True(Assert.Single(result.KnownPlaces, k => k.Label == "Кэш").Regenerates);
        Assert.False(Assert.Single(result.KnownPlaces, k => k.Label == "Данные").Regenerates);
    }

    // ------------------------------------------------------------------ откат статистики для группы папок-пожирателей

    [Fact]
    public void Rust_target_group_is_not_personal_and_its_files_do_not_count_for_the_project()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "proj");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "Cargo.toml"), "[package]");
        File.WriteAllText(Path.Combine(project, "src", "main.rs"), "fn main() {}");
        WriteFiles(Path.Combine(project, "target", "debug"), ".jpg", 10, 1);

        var result = Run(temp.Path, Small(minCount: 5));

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("Rust target", group.Name);
        Assert.Equal(BuildArtifactCatalog.Describe("Rust target"), group.Description);
        Assert.Empty(result.PersonalFolders); // ни target, ни target\debug, ни проект, ни корень
    }

    [Fact]
    public void Same_layout_without_a_marker_file_is_personal_because_target_is_not_a_suspect()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "proj");
        var target = Path.Combine(project, "target");
        WriteFiles(Path.Combine(target, "debug"), ".jpg", 10, 1);

        var result = Run(temp.Path, Small(minCount: 5));

        Assert.Empty(result.SuspectGroups);
        Assert.True(Has(result, Path.Combine(target, "debug")));
        Assert.True(Has(result, target));
        Assert.True(Has(result, project));
    }

    [Fact]
    public void Maven_target_group_gets_the_maven_description()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "svc");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "pom.xml"), "<project/>");
        WriteFiles(Path.Combine(project, "target"), ".jar", 2, 10);

        var result = Run(temp.Path, Small());

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("Maven target", group.Name);
        Assert.Contains("mvn package", group.Description);
    }

    [Fact]
    public void Node_modules_group_gets_its_description_and_unknown_groups_would_use_the_fallback()
    {
        using var temp = new TempTestDirectory();
        WriteFiles(Path.Combine(temp.Path, "web", "node_modules", "pkg"), ".js", 2, 10);

        var result = Run(temp.Path, Small());

        var group = Assert.Single(result.SuspectGroups);
        Assert.Equal("node_modules", group.Name);
        Assert.Contains("npm install", group.Description);
    }

    // ------------------------------------------------------------------ отмена анализа

    private sealed class SyncProgress : IProgress<DiskAnalysisProgress>
    {
        private readonly Action<DiskAnalysisProgress> _onReport;
        public SyncProgress(Action<DiskAnalysisProgress> onReport) => _onReport = onReport;
        public void Report(DiskAnalysisProgress value) => _onReport(value);
    }

    [Fact]
    public void Cancel_in_the_middle_keeps_the_personal_folders_found_so_far()
    {
        using var temp = new TempTestDirectory();
        foreach (var top in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, top));
            File.WriteAllText(Path.Combine(temp.Path, top, "readme.txt"), "x");
            WriteFiles(Path.Combine(temp.Path, top, "photos"), ".jpg", 2, 1);
        }

        // Анализатор сообщает о прогрессе не чаще раза в 200 мс, поэтому первые три сообщения «задерживаем»,
        // а на четвёртом (начало второй папки верхнего уровня, какой бы она ни оказалась по порядку обхода)
        // отменяем: к этому моменту первая верхняя папка и её photos полностью обработаны.
        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new SyncProgress(_ =>
        {
            reports++;
            if (reports < 4)
                Thread.Sleep(250);
            else
                cts.Cancel();
        });

        var result = Run(temp.Path, Small(minCount: 2), progress: progress, cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(4, reports);
        Assert.Equal(2, result.PersonalFolders.Count);                       // photos первой папки и сама первая папка
        var photos = Assert.Single(result.PersonalFolders, f => Trim(f.Path).EndsWith("photos", StringComparison.OrdinalIgnoreCase));
        Assert.True(Has(result, Path.GetDirectoryName(Trim(photos.Path))!));
        Assert.False(Has(result, temp.Path));                                // корень не был завершён
    }

    [Fact]
    public void Partial_result_after_cancel_goes_into_the_index()
    {
        using var temp = new TempTestDirectory();
        foreach (var top in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, top));
            File.WriteAllText(Path.Combine(temp.Path, top, "readme.txt"), "x");
            WriteFiles(Path.Combine(temp.Path, top, "photos"), ".jpg", 2, 1);
        }

        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new SyncProgress(_ =>
        {
            reports++;
            if (reports < 4) Thread.Sleep(250); else cts.Cancel();
        });
        var result = Run(temp.Path, Small(minCount: 2), progress: progress, cancellationToken: cts.Token);

        var index = new PersonalContentIndex();
        index.ReplaceForRoot(result.RootPath, result.PersonalFolders);

        Assert.True(result.WasCancelled);
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGetFolder(result.PersonalFolders[0].Path, out var info));
        Assert.NotNull(info);
    }

    [Fact]
    public void Cancelled_before_start_gives_an_empty_partial_result()
    {
        using var temp = new TempTestDirectory();
        WriteFiles(Path.Combine(temp.Path, "photos"), ".jpg", 10, 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = Run(temp.Path, Small(minCount: 2), cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Empty(result.PersonalFolders);
    }

    // ------------------------------------------------------------------ несколько корней

    [Fact]
    public void Several_roots_share_one_index_and_reanalysis_replaces_only_its_own_root()
    {
        using var temp = new TempTestDirectory();
        var root1 = Path.Combine(temp.Path, "R1");
        var root2 = Path.Combine(temp.Path, "R2");
        WriteFiles(Path.Combine(root1, "photos"), ".jpg", 3, 1);
        WriteFiles(Path.Combine(root2, "photos"), ".jpg", 3, 1);
        var options = Small(minCount: 2);
        var index = new PersonalContentIndex();

        var first = Run(root1, options);
        var second = Run(root2, options);
        index.ReplaceForRoot(first.RootPath, first.PersonalFolders);
        index.ReplaceForRoot(second.RootPath, second.PersonalFolders);

        Assert.Equal(4, index.Count); // корень и photos каждого из двух анализов
        Assert.True(index.TryGetFolder(Path.Combine(root1, "photos"), out _));
        Assert.True(index.TryGetFolder(Path.Combine(root2, "photos"), out _));

        // Файлы в первом корне удалили и проанализировали его заново: записи R1 заменяются, записи R2 остаются.
        Directory.Delete(Path.Combine(root1, "photos"), recursive: true);
        var again = Run(root1, options);
        index.ReplaceForRoot(again.RootPath, again.PersonalFolders);

        Assert.Equal(2, index.Count);
        Assert.False(index.TryGetFolder(Path.Combine(root1, "photos"), out _));
        Assert.True(index.TryGetFolder(Path.Combine(root2, "photos"), out _));
    }
}
