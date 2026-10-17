using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Определение личных данных: расширения, пороги папки (на самих границах), накопление статистики.
/// Здесь нет ввода-вывода: статистика собирается из значений, поэтому реальные пороги 100 МБ и 1 ГБ проверяются
/// без создания больших файлов.
/// </summary>
public sealed class PersonalDataClassifierTests
{
    private const long Kb = 1024;
    private const long Mb = 1024 * Kb;
    private const long Gb = 1024 * Mb;

    // ------------------------------------------------------------------ расширения

    private static void AssertCategory(PersonalDataCategory expected, params string[] extensions)
    {
        var wrong = extensions
            .Select(ext => (Ext: ext, Actual: PersonalDataClassifier.Classify("file" + ext)))
            .Where(x => x.Actual != expected)
            .Select(x => $"{x.Ext} -> {(x.Actual?.ToString() ?? "не личный")}")
            .ToList();

        Assert.True(wrong.Count == 0, $"Ожидалась категория {expected}, но: {string.Join("; ", wrong)}");
    }

    [Fact]
    public void Video_extensions_are_classified_as_video()
        => AssertCategory(PersonalDataCategory.Video,
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".vob", ".3gp", ".ogv");

    [Fact]
    public void Audio_extensions_are_classified_as_audio()
        => AssertCategory(PersonalDataCategory.Audio,
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".ape", ".alac");

    [Fact]
    public void Image_and_raw_extensions_are_classified_as_image()
        => AssertCategory(PersonalDataCategory.Image,
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".psd", ".xcf",
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".raw");

    [Fact]
    public void Document_extensions_are_classified_as_document()
        => AssertCategory(PersonalDataCategory.Document,
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".odt", ".ods", ".odp", ".rtf", ".epub", ".djvu");

    [Fact]
    public void Archive_extensions_are_classified_as_archive()
        => AssertCategory(PersonalDataCategory.Archive,
            ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso");

    [Fact]
    public void Vm_images_password_vaults_and_mail_stores_form_the_sixth_category()
        => AssertCategory(PersonalDataCategory.VmImagesAndVaults,
            ".vhd", ".vhdx", ".vmdk", ".vdi", ".kdbx", ".pst");

    [Fact]
    public void Typescript_ts_is_not_video()
    {
        Assert.Null(PersonalDataClassifier.Classify("app.ts"));
        Assert.Null(PersonalDataClassifier.Classify("APP.TS"));
        Assert.Null(PersonalDataClassifier.Classify(Path.Combine("src", "index.ts")));
    }

    [Fact]
    public void Kdbx_vhdx_and_pst_are_personal_in_the_dedicated_category()
    {
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("passwords.kdbx"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("win11.vhdx"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("mail.pst"));
    }

    [Fact]
    public void Extensions_are_case_insensitive()
    {
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.Image, PersonalDataClassifier.Classify("PHOTO.JPG"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.Video, PersonalDataClassifier.Classify("Movie.MkV"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("Vault.KDBX"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("Disk.VHDX"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.VmImagesAndVaults, PersonalDataClassifier.Classify("Archive.Pst"));
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.Document, PersonalDataClassifier.Classify("Report.PdF"));
    }

    [Fact]
    public void Code_assets_and_technical_formats_are_not_personal()
    {
        var notPersonal = new[]
        {
            ".txt", ".md", ".csv", ".json", ".xml", ".html", ".svg", ".ico", ".cs", ".js", ".dll", ".exe", ".log",
            ".jar", ".war", ".nupkg", ".cab", ".msi", ".ts",
        };

        var wrong = notPersonal.Where(ext => PersonalDataClassifier.Classify("file" + ext) is not null).ToList();
        Assert.True(wrong.Count == 0, "Не должны считаться личными: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Name_without_extension_or_empty_is_not_personal()
    {
        Assert.Null(PersonalDataClassifier.Classify("README"));
        Assert.Null(PersonalDataClassifier.Classify("movie."));
        Assert.Null(PersonalDataClassifier.Classify(""));
        Assert.Null(PersonalDataClassifier.Classify(null));
    }

    [Fact]
    public void Only_the_last_extension_counts_and_the_directory_part_is_ignored()
    {
        Assert.Equal((PersonalDataCategory?)PersonalDataCategory.Archive, PersonalDataClassifier.Classify("backup.tar.gz"));
        Assert.Null(PersonalDataClassifier.Classify("movie.mp4.bak"));
        Assert.Null(PersonalDataClassifier.Classify(Path.Combine("photos.jpg", "notes")));
    }

    [Fact]
    public void Every_category_has_a_distinct_display_name()
    {
        var names = Enum.GetValues<PersonalDataCategory>().Select(PersonalDataClassifier.DisplayName).ToList();

        Assert.Equal(6, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.All(n => !string.IsNullOrWhiteSpace(n)));
    }

    // ------------------------------------------------------------------ накопление статистики

    private static PersonalStats Files(PersonalDataCategory category, int count, long bytesEach, bool counted)
    {
        var stats = default(PersonalStats);
        for (var i = 0; i < count; i++)
            stats = stats.Add(category, bytesEach, counted);
        return stats;
    }

    private static PersonalStats One(PersonalDataCategory category, long bytes)
        => default(PersonalStats).Add(category, bytes, counted: false);

    [Fact]
    public void Stats_add_counts_files_and_bytes_per_category_and_counted_files_separately()
    {
        var stats = default(PersonalStats)
            .Add(PersonalDataCategory.Video, 100, counted: true)
            .Add(PersonalDataCategory.Video, 50, counted: false)
            .Add(PersonalDataCategory.Audio, 7, counted: true);

        Assert.Equal(2, stats.Video.Files);
        Assert.Equal(150, stats.Video.Bytes);
        Assert.Equal(1, stats.Audio.Files);
        Assert.Equal(0, stats.Image.Files);
        Assert.Equal(3, stats.TotalFiles);
        Assert.Equal(157, stats.TotalBytes);
        Assert.Equal(2, stats.CountedFiles);
    }

    [Fact]
    public void Stats_subtraction_gives_the_subtree_delta()
    {
        var before = Files(PersonalDataCategory.Image, 3, 10, counted: true);
        var after = before
            .Add(PersonalDataCategory.Video, 100, counted: false)
            .Add(PersonalDataCategory.Image, 5, counted: true);

        var delta = after - before;

        Assert.Equal(1, delta.Video.Files);
        Assert.Equal(100, delta.Video.Bytes);
        Assert.Equal(1, delta.Image.Files);
        Assert.Equal(5, delta.Image.Bytes);
        Assert.Equal(1, delta.CountedFiles);
        Assert.Equal(2, delta.TotalFiles);
        Assert.Equal(105, delta.TotalBytes);
    }

    // ------------------------------------------------------------------ пороги: значения и связь с DiskAnalysisOptions

    [Fact]
    public void Threshold_constants_have_the_agreed_values()
    {
        Assert.Equal(20, PersonalDataRules.MinFolderFileCount);
        Assert.Equal(32L * 1024, PersonalDataRules.CountRuleMinFileBytes);
        Assert.Equal(0.5, PersonalDataRules.MinFolderShare);
        Assert.Equal(100L * 1024 * 1024, PersonalDataRules.ShareRuleMinFolderBytes);
        Assert.Equal(1024L * 1024 * 1024, PersonalDataRules.AbsolutePersonalBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, PersonalDataRules.TypedConfirmationBytes);
        Assert.Equal("УДАЛИТЬ", PersonalDataRules.ConfirmWord);
    }

    [Fact]
    public void Analysis_options_defaults_reference_the_constants()
    {
        var options = new DiskAnalysisOptions();

        Assert.Equal(PersonalDataRules.MinFolderFileCount, options.PersonalMinFileCount);
        Assert.Equal(PersonalDataRules.CountRuleMinFileBytes, options.PersonalCountMinFileBytes);
        Assert.Equal(PersonalDataRules.MinFolderShare, options.PersonalMinShare);
        Assert.Equal(PersonalDataRules.ShareRuleMinFolderBytes, options.PersonalShareMinFolderBytes);
        Assert.Equal(PersonalDataRules.AbsolutePersonalBytes, options.PersonalAbsoluteBytes);
    }

    // ------------------------------------------------------------------ пороги: границы

    [Fact]
    public void Count_rule_19_files_is_not_enough_and_20_is()
    {
        var nineteen = Files(PersonalDataCategory.Image, 19, 32 * Kb, counted: true);
        var twenty = Files(PersonalDataCategory.Image, 20, 32 * Kb, counted: true);

        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(nineteen, nineteen.TotalBytes));
        Assert.Equal(PersonalFolderReason.ByFileCount, PersonalDataClassifier.GetFolderReasons(twenty, twenty.TotalBytes));
    }

    [Fact]
    public void Count_rule_uses_only_counted_files()
    {
        // 19 учитываемых + 100 «мелких» (analyzer не пометил их counted): правило количества не срабатывает.
        var stats = Files(PersonalDataCategory.Image, 19, 32 * Kb, counted: true);
        for (var i = 0; i < 100; i++)
            stats = stats.Add(PersonalDataCategory.Image, 31 * Kb, counted: false);

        Assert.Equal(119, stats.TotalFiles);
        Assert.Equal(19, stats.CountedFiles);
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(stats, stats.TotalBytes));
    }

    [Fact]
    public void Share_rule_folder_size_99_MB_is_not_enough_and_100_MB_is()
    {
        var below = One(PersonalDataCategory.Video, 99 * Mb);
        var edgeBelow = One(PersonalDataCategory.Video, 100 * Mb - 1);
        var atLimit = One(PersonalDataCategory.Video, 100 * Mb);

        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(below, 99 * Mb));
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(edgeBelow, 100 * Mb - 1));
        Assert.Equal(PersonalFolderReason.ByShare, PersonalDataClassifier.GetFolderReasons(atLimit, 100 * Mb));
    }

    [Fact]
    public void Share_rule_needs_half_of_the_folder_size()
    {
        var justUnderHalf = One(PersonalDataCategory.Video, 50 * Mb - 1);
        var exactlyHalf = One(PersonalDataCategory.Video, 50 * Mb);

        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(justUnderHalf, 100 * Mb));
        Assert.Equal(PersonalFolderReason.ByShare, PersonalDataClassifier.GetFolderReasons(exactlyHalf, 100 * Mb));
    }

    [Fact]
    public void Share_rule_ignores_empty_folder_without_dividing_by_zero()
    {
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(default, 0));
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(One(PersonalDataCategory.Image, 10), 0));
    }

    [Fact]
    public void Absolute_rule_1_GB_of_personal_files_is_enough_regardless_of_share()
    {
        // Личное занимает ~10% папки: правило доли не срабатывает, абсолютное — на самой границе.
        var below = One(PersonalDataCategory.Video, Gb - 1);
        var atLimit = One(PersonalDataCategory.Video, Gb);

        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(below, 10 * Gb));
        Assert.Equal(PersonalFolderReason.ByTotalSize, PersonalDataClassifier.GetFolderReasons(atLimit, 10 * Gb));
    }

    [Fact]
    public void All_rules_can_apply_at_once()
    {
        var stats = Files(PersonalDataCategory.Video, 20, 60 * Mb, counted: true); // 1.2 ГБ

        var reasons = PersonalDataClassifier.GetFolderReasons(stats, stats.TotalBytes);

        Assert.Equal(PersonalFolderReason.ByFileCount | PersonalFolderReason.ByShare | PersonalFolderReason.ByTotalSize, reasons);
    }

    [Fact]
    public void Custom_options_override_the_defaults()
    {
        var options = new DiskAnalysisOptions
        {
            PersonalMinFileCount = 3,
            PersonalShareMinFolderBytes = 10,
            PersonalMinShare = 0.75,
            PersonalAbsoluteBytes = 500,
        };

        var three = Files(PersonalDataCategory.Audio, 3, 1, counted: true);
        Assert.Equal(PersonalFolderReason.ByFileCount, PersonalDataClassifier.GetFolderReasons(three, 100, options));

        var share = One(PersonalDataCategory.Audio, 75);
        Assert.Equal(PersonalFolderReason.ByShare, PersonalDataClassifier.GetFolderReasons(share, 100, options));
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(One(PersonalDataCategory.Audio, 74), 100, options));

        var absolute = One(PersonalDataCategory.Audio, 500);
        Assert.Equal(PersonalFolderReason.ByTotalSize, PersonalDataClassifier.GetFolderReasons(absolute, 100_000, options));
        Assert.Equal(PersonalFolderReason.None, PersonalDataClassifier.GetFolderReasons(One(PersonalDataCategory.Audio, 499), 100_000, options));
    }

    // ------------------------------------------------------------------ игнорируемые папки и слово подтверждения

    [Fact]
    public void Known_game_cache_and_dependency_folders_are_ignored_by_name_case_insensitively()
    {
        var ignored = new[]
        {
            "node_modules", ".cache", ".git", "site-packages", "__pycache__", ".gradle", ".m2", "Pods", "venv", ".venv",
            "steamapps", "SteamLibrary", "Epic Games", "GOG Games", "WindowsApps",
            "Cache", "Code Cache", "GPUCache", "cache2", "ShaderCache",
        };

        var wrong = ignored.Where(name => !PersonalDataRules.IsIgnoredFolderName(name)).ToList();
        Assert.True(wrong.Count == 0, "Должны игнорироваться: " + string.Join(", ", wrong));

        Assert.True(PersonalDataRules.IsIgnoredFolderName("NODE_MODULES"));
        Assert.True(PersonalDataRules.IsIgnoredFolderName("SteamApps"));
    }

    [Fact]
    public void Ordinary_folder_names_are_not_ignored()
    {
        Assert.False(PersonalDataRules.IsIgnoredFolderName("Photos"));
        Assert.False(PersonalDataRules.IsIgnoredFolderName("src"));
        Assert.False(PersonalDataRules.IsIgnoredFolderName("Видео"));
        Assert.False(PersonalDataRules.IsIgnoredFolderName(""));
        Assert.False(PersonalDataRules.IsIgnoredFolderName(null));
    }

    [Fact]
    public void Confirm_word_matches_ignoring_case_and_outer_spaces()
    {
        Assert.True(PersonalDataRules.IsConfirmWord("УДАЛИТЬ"));
        Assert.True(PersonalDataRules.IsConfirmWord("удалить"));
        Assert.True(PersonalDataRules.IsConfirmWord("  Удалить  "));

        Assert.False(PersonalDataRules.IsConfirmWord("УДАЛИТ"));
        Assert.False(PersonalDataRules.IsConfirmWord("УДАЛИТЬ!"));
        Assert.False(PersonalDataRules.IsConfirmWord(""));
        Assert.False(PersonalDataRules.IsConfirmWord(null));
    }

    // ------------------------------------------------------------------ описание папки

    [Fact]
    public void Describe_folder_lists_at_most_three_biggest_categories()
    {
        var stats = default(PersonalStats)
            .Add(PersonalDataCategory.Video, 5 * Gb, counted: false)
            .Add(PersonalDataCategory.Image, 512 * Kb, counted: true)
            .Add(PersonalDataCategory.Image, 512 * Kb, counted: true)
            .Add(PersonalDataCategory.Document, 500 * Kb, counted: true)
            .Add(PersonalDataCategory.Archive, 10, counted: false);
        var info = new PersonalFolderInfo(Path.Combine("x", "Media"), stats, 6 * Gb, PersonalFolderReason.ByShare);

        var text = PersonalDataClassifier.DescribeFolder(info);

        Assert.Contains("личные файлы по составу папки", text);
        Assert.Contains("видео: 1 шт.", text);
        Assert.Contains("изображения: 2 шт.", text);
        Assert.Contains("документы: 1 шт.", text);
        Assert.DoesNotContain("архивы", text);
    }

    [Fact]
    public void Only_the_share_rule_makes_children_inherit_the_personal_status()
    {
        var stats = One(PersonalDataCategory.Video, 1);

        Assert.True(new PersonalFolderInfo("a", stats, 1, PersonalFolderReason.ByShare).InheritsToChildren);
        Assert.True(new PersonalFolderInfo("a", stats, 1, PersonalFolderReason.ByShare | PersonalFolderReason.ByFileCount).InheritsToChildren);
        Assert.False(new PersonalFolderInfo("a", stats, 1, PersonalFolderReason.ByFileCount).InheritsToChildren);
        Assert.False(new PersonalFolderInfo("a", stats, 1, PersonalFolderReason.ByTotalSize).InheritsToChildren);
        Assert.False(new PersonalFolderInfo("a", stats, 1, PersonalFolderReason.None).InheritsToChildren);
    }
}
