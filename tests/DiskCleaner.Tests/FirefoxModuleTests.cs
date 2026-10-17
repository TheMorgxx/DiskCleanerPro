using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class FirefoxModuleTests
{
    private const string FirefoxRel = "Users/bob/AppData/Roaming/Mozilla/Firefox";
    private const string ProfileRel = FirefoxRel + "/Profiles/abc.default-release";
    private const string StorageRel = ProfileRel + "/storage/default";

    private static ManualDeletionPaths MakePaths(string root) => new()
    {
        UserProfile = Path.Combine(root, "Users", "bob"),
        UsersRoot = Path.Combine(root, "Users"),
        AppBaseDirectory = Path.Combine(root, "App"),
    };

    private static (FirefoxSiteStorageAnalyzer Analyzer, FirefoxSiteCleanupService Cleaner) Build(
        string root, Func<string, bool>? inUse = null)
    {
        var guard = new ReparsePointGuard();
        var deletion = new ManualDeletionService(guard, MakePaths(root));
        return (new FirefoxSiteStorageAnalyzer(new SizeCalculator(guard), guard),
                new FirefoxSiteCleanupService(deletion, inUse ?? (_ => false)));
    }

    private static readonly FirefoxAnalysisOptions TestOptions = new() { MinSiteBytes = 50, SuggestedMinBytes = 100 };

    /// <summary>
    /// tiktok: idb 300 + cache 200 + ls 10; opencut: fs 150; расширение: idb 500; мелкий сайт: ls 5 (скрывается).
    /// В корне профиля лежат файлы, которые модуль трогать не должен.
    /// </summary>
    private static FirefoxProfile SeedProfile(TempTestDirectory t)
    {
        t.CreateFile(ProfileRel + "/cookies.sqlite", "COOKIES");
        t.CreateFile(ProfileRel + "/logins.json", "LOGINS");
        t.CreateFile(ProfileRel + "/key4.db", "KEY4");
        t.CreateFile(ProfileRel + "/places.sqlite", "PLACES");

        t.CreateFile(StorageRel + "/https+++www.tiktok.com/idb/1.sqlite", new string('x', 300));
        t.CreateFile(StorageRel + "/https+++www.tiktok.com/cache/c.bin", new string('x', 200));
        t.CreateFile(StorageRel + "/https+++www.tiktok.com/ls/data.sqlite", new string('x', 10));
        t.CreateFile(StorageRel + "/https+++www.tiktok.com/.metadata-v2", "meta");

        t.CreateFile(StorageRel + "/https+++opencut.app/fs/project.bin", new string('x', 150));
        t.CreateFile(StorageRel + "/moz-extension+++1234-abcd/idb/e.sqlite", new string('x', 500));
        t.CreateFile(StorageRel + "/https+++tiny.example/ls/data.sqlite", "12345");

        return new FirefoxProfile { Name = "default-release", Path = Path.Combine(t.Path, ProfileRel), IsDefault = true };
    }

    private static FirefoxSiteEntry Site(FirefoxStorageReport report, string host)
        => report.Sites.First(s => s.Host == host);

    private static FirefoxSiteSelection Select(FirefoxSiteEntry site, params string[] folders)
        => new(site, site.Parts.Where(p => folders.Contains(p.FolderName)).ToList());

    private static FirefoxCleanupOptions Real(bool confirmed = false, bool wholeOrigin = true)
        => new() { DryRun = false, Confirmed = confirmed, RemoveEmptyOriginFolders = wholeOrigin };

    // ------------------------------------------------------------- профили

    [Fact]
    public void Locator_reads_profiles_ini_relative_absolute_and_default()
    {
        using var temp = new TempTestDirectory();
        temp.CreateSubDirectory(FirefoxRel + "/Profiles/abc.default-release");
        temp.CreateSubDirectory("work-profile");
        var ini = string.Join("\n",
            "[Install4F96D1932A9F858E]",
            "Default=Profiles/abc.default-release",
            "Locked=1",
            "",
            "[Profile1]",
            "Name=work",
            "IsRelative=0",
            "Path=" + Path.Combine(temp.Path, "work-profile"),
            "",
            "[Profile0]",
            "Name=default-release",
            "IsRelative=1",
            "Path=Profiles/abc.default-release",
            "",
            "[Profile2]",
            "Name=ghost",
            "IsRelative=1",
            "Path=Profiles/missing.default");
        temp.CreateFile(FirefoxRel + "/profiles.ini", ini);

        var profiles = new FirefoxProfileLocator(Path.Combine(temp.Path, FirefoxRel)).FindProfiles();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("default-release", profiles[0].Name); // профиль по умолчанию — первым
        Assert.True(profiles[0].IsDefault);
        Assert.Equal("work", profiles[1].Name);
        Assert.False(profiles[1].IsDefault);
    }

    [Fact]
    public void Locator_falls_back_to_profiles_folder_without_ini()
    {
        using var temp = new TempTestDirectory();
        temp.CreateSubDirectory(FirefoxRel + "/Profiles/abc.default-release");
        temp.CreateSubDirectory(FirefoxRel + "/Profiles/xyz.test");

        var profiles = new FirefoxProfileLocator(Path.Combine(temp.Path, FirefoxRel)).FindProfiles();

        Assert.Equal(2, profiles.Count);
        Assert.True(profiles[0].IsDefault);
        Assert.Contains(profiles, p => p.Name == "xyz.test");
    }

    // ------------------------------------------------------------- имена источников

    [Fact]
    public void Origin_parser_understands_hosts_ports_containers_partitions_and_extensions()
    {
        var plain = FirefoxOriginInfo.Parse("https+++www.tiktok.com");
        Assert.Equal("https", plain.Scheme);
        Assert.Equal("www.tiktok.com", plain.Host);
        Assert.Equal("www.tiktok.com", plain.DisplayName);

        var port = FirefoxOriginInfo.Parse("http+++localhost+3000");
        Assert.Equal("localhost", port.Host);
        Assert.Equal(3000, port.Port);

        var container = FirefoxOriginInfo.Parse("https+++example.com^userContextId=2");
        Assert.Equal("example.com", container.Host);
        Assert.Equal(2, container.ContainerId);
        Assert.Contains("контейнер 2", container.DisplayName);

        var partition = FirefoxOriginInfo.Parse("https+++cdn.example.com^partitionKey=%28https%2Csite.com%29");
        Assert.Equal("(https,site.com)", partition.PartitionKey);

        var extension = FirefoxOriginInfo.Parse("moz-extension+++1234-abcd");
        Assert.True(extension.IsExtension);
        Assert.False(plain.IsExtension);
    }

    // ------------------------------------------------------------- анализ

    [Fact]
    public void Analyzer_lists_sites_sorted_hides_tiny_ones_and_marks_suggested_parts()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);

        var report = Build(temp.Path).Analyzer.Analyze(profile, TestOptions);

        Assert.Equal(3, report.Sites.Count);
        Assert.Equal("www.tiktok.com", report.Sites[0].Host);   // 510
        Assert.Equal(510, report.Sites[0].TotalBytes);
        Assert.Equal("1234-abcd", report.Sites[1].Host);         // 500 — расширение
        Assert.Equal("opencut.app", report.Sites[2].Host);       // 150
        Assert.Equal(1, report.HiddenSmallSitesCount);
        Assert.Equal(5, report.HiddenSmallSitesBytes);

        var tiktok = Site(report, "www.tiktok.com");
        Assert.True(tiktok.Parts.First(p => p.FolderName == "idb").IsSuggested);
        Assert.True(tiktok.Parts.First(p => p.FolderName == "cache").IsSuggested);
        Assert.False(tiktok.Parts.First(p => p.FolderName == "ls").IsSuggested);
        Assert.Equal(FirefoxStorageRisk.High, tiktok.Parts.First(p => p.FolderName == "ls").Risk);

        var opencut = Site(report, "opencut.app");
        Assert.False(opencut.Parts[0].IsSuggested);              // файлы сайта никогда не «рекомендуются»
        Assert.Equal(FirefoxStorageKind.FileSystem, opencut.Parts[0].Kind);
    }

    [Fact]
    public void Analyzer_marks_extension_data_as_not_deletable()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);

        var report = Build(temp.Path).Analyzer.Analyze(profile, TestOptions);

        var extension = report.Sites.First(s => s.IsExtension);
        Assert.False(extension.CanDelete);
        Assert.False(extension.Parts[0].IsSuggested);
        Assert.True(Site(report, "www.tiktok.com").CanDelete);
    }

    [Fact]
    public void Analyzer_counts_unknown_folders_in_total_but_does_not_offer_them_for_deletion()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        temp.CreateFile(StorageRel + "/https+++www.tiktok.com/sdb/x.bin", new string('x', 40));

        var site = Site(Build(temp.Path).Analyzer.Analyze(profile, TestOptions), "www.tiktok.com");

        Assert.Equal(550, site.TotalBytes);
        Assert.Equal(40, site.OtherBytes);
        Assert.Contains("sdb", site.OtherFolders);
        Assert.Equal(3, site.Parts.Count);
    }

    [Fact]
    public void Analyzer_reports_newest_modification_time_of_a_part()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var idbFile = Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "idb", "1.sqlite");
        File.SetLastWriteTimeUtc(idbFile, new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var idb = Site(Build(temp.Path).Analyzer.Analyze(profile, TestOptions), "www.tiktok.com")
            .Parts.First(p => p.FolderName == "idb");

        Assert.Equal(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), idb.LastModifiedUtc);
    }

    [Fact]
    public void Analyzer_returns_empty_report_when_profile_has_no_storage()
    {
        using var temp = new TempTestDirectory();
        temp.CreateSubDirectory(ProfileRel);
        var profile = new FirefoxProfile { Name = "empty", Path = Path.Combine(temp.Path, ProfileRel) };

        var report = Build(temp.Path).Analyzer.Analyze(profile, TestOptions);

        Assert.Equal(0, report.Sites.Count);
        Assert.Equal(0, report.TotalBytes);
    }

    // ------------------------------------------------------------- очистка

    [Fact]
    public void Dry_run_deletes_nothing_and_does_not_require_firefox_to_be_closed()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path, inUse: _ => true); // Firefox «запущен»
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");

        var result = cleaner.Clean(new[] { Select(tiktok, "idb") }, new FirefoxCleanupOptions { DryRun = true, Confirmed = true });

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.DryRun);
        Assert.Equal(300, entry.SizeBeforeBytes);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "idb")));
    }

    [Fact]
    public void Idb_needs_confirmation_and_only_the_selected_part_is_deleted()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");
        var origin = Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com");

        var notConfirmed = cleaner.Clean(new[] { Select(tiktok, "idb") }, Real(confirmed: false));
        Assert.Contains(Assert.Single(notConfirmed.Entries).Errors, e => e.Contains("Нужно подтверждение"));
        Assert.True(Directory.Exists(Path.Combine(origin, "idb")));

        var confirmed = cleaner.Clean(new[] { Select(tiktok, "idb") }, Real(confirmed: true));
        var entry = Assert.Single(confirmed.Entries);
        Assert.Equal(1, entry.FilesDeleted);
        Assert.False(Directory.Exists(Path.Combine(origin, "idb")));

        // localStorage, Cache API и служебный файл сайта остались.
        Assert.True(File.Exists(Path.Combine(origin, "ls", "data.sqlite")));
        Assert.True(File.Exists(Path.Combine(origin, "cache", "c.bin")));
        Assert.True(File.Exists(Path.Combine(origin, ".metadata-v2")));
    }

    [Fact]
    public void Logins_cookies_history_and_keys_in_profile_root_are_never_touched()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var report = analyzer.Analyze(profile, TestOptions);

        // Чистим вообще всё, что модуль считает чистимым, с подтверждением.
        var all = report.Sites.Where(s => s.CanDelete).Select(s => new FirefoxSiteSelection(s, s.Parts)).ToList();
        cleaner.Clean(all, Real(confirmed: true));

        foreach (var name in new[] { "cookies.sqlite", "logins.json", "key4.db", "places.sqlite" })
            Assert.True(File.Exists(Path.Combine(temp.Path, ProfileRel, name)), name);

        // Данные расширения не тронуты.
        Assert.True(File.Exists(Path.Combine(temp.Path, StorageRel, "moz-extension+++1234-abcd", "idb", "e.sqlite")));
    }

    [Fact]
    public void Cache_api_part_is_deletable_without_confirmation()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");

        var result = cleaner.Clean(new[] { Select(tiktok, "cache") }, Real(confirmed: false));

        Assert.Equal(1, Assert.Single(result.Entries).FilesDeleted);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "cache")));
    }

    [Fact]
    public void Selecting_all_folders_of_a_site_removes_the_whole_origin_folder()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");
        var origin = Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com");

        var result = cleaner.Clean(new[] { new FirefoxSiteSelection(tiktok, tiktok.Parts) }, Real(confirmed: true));

        var entry = Assert.Single(result.Entries);
        Assert.Contains("весь сайт", entry.TargetDisplayName);
        Assert.False(Directory.Exists(origin));
        // Соседний сайт цел.
        Assert.True(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++opencut.app")));
    }

    [Fact]
    public void Origin_folder_stays_when_it_has_unknown_folders_or_whole_origin_removal_is_off()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        temp.CreateFile(StorageRel + "/https+++www.tiktok.com/sdb/x.bin", new string('x', 40));
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");
        var origin = Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com");

        cleaner.Clean(new[] { new FirefoxSiteSelection(tiktok, tiktok.Parts) }, Real(confirmed: true));

        Assert.True(Directory.Exists(origin));                       // sdb не входит в allowlist — остаётся
        Assert.True(File.Exists(Path.Combine(origin, "sdb", "x.bin")));
        Assert.False(Directory.Exists(Path.Combine(origin, "idb")));
        Assert.False(Directory.Exists(Path.Combine(origin, "ls")));
    }

    [Fact]
    public void Real_deletion_is_refused_while_firefox_is_running()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path, inUse: _ => true);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");

        var result = cleaner.Clean(new[] { Select(tiktok, "idb", "cache") }, Real(confirmed: true));

        var entry = Assert.Single(result.Entries);
        Assert.Contains(entry.Errors, e => e.Contains("Firefox запущен"));
        Assert.Equal(0, entry.FilesDeleted);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "idb")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "cache")));
    }

    [Fact]
    public void Extension_data_is_refused_even_if_the_model_is_tampered_with()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var extension = analyzer.Analyze(profile, TestOptions).Sites.First(s => s.IsExtension);

        // Подделанная модель: сайт «не расширение», хотя папка называется moz-extension+++...
        var forged = new FirefoxSiteEntry
        {
            ProfilePath = extension.ProfilePath,
            OriginFolderName = extension.OriginFolderName,
            OriginPath = extension.OriginPath,
            Scheme = "https",
            Host = "evil",
            IsExtension = false,
            DisplayName = "evil",
            Parts = extension.Parts,
        };

        var result = cleaner.Clean(new[] { new FirefoxSiteSelection(forged, forged.Parts) }, Real(confirmed: true));

        Assert.Contains(Assert.Single(result.Entries).Errors, e => e.Contains("расширений"));
        Assert.True(File.Exists(Path.Combine(temp.Path, StorageRel, "moz-extension+++1234-abcd", "idb", "e.sqlite")));
    }

    [Fact]
    public void Targets_outside_the_allowlist_are_rejected()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");
        var cookies = Path.Combine(temp.Path, ProfileRel, "cookies.sqlite");

        // 1) тип данных вне allowlist
        var unknownType = new FirefoxStoragePart { Kind = FirefoxStorageKind.IndexedDb, FolderName = "places.sqlite", Path = cookies };
        // 2) верное имя типа, но путь ведёт в другое место
        var wrongPath = new FirefoxStoragePart { Kind = FirefoxStorageKind.IndexedDb, FolderName = "idb", Path = cookies };
        // 3) сайт, лежащий не в storage\default
        var profileRootAsSite = new FirefoxSiteEntry
        {
            ProfilePath = profile.Path,
            OriginFolderName = "abc.default-release",
            OriginPath = profile.Path,
            Scheme = "https",
            Host = "x",
            DisplayName = "x",
        };

        var result = cleaner.Clean(new[]
        {
            new FirefoxSiteSelection(tiktok, new[] { unknownType }),
            new FirefoxSiteSelection(tiktok, new[] { wrongPath }),
            new FirefoxSiteSelection(profileRootAsSite, tiktok.Parts),
        }, Real(confirmed: true));

        Assert.Equal(3, result.Entries.Count);
        Assert.True(result.Entries.All(e => e.Errors.Any(m => m.Contains("Некорректная цель"))));
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(Path.Combine(temp.Path, ProfileRel, "logins.json")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, StorageRel, "https+++www.tiktok.com", "idb")));
    }

    [Fact]
    public void Journal_entries_name_the_site_and_the_data_type()
    {
        using var temp = new TempTestDirectory();
        var profile = SeedProfile(temp);
        var (analyzer, cleaner) = Build(temp.Path);
        var tiktok = Site(analyzer.Analyze(profile, TestOptions), "www.tiktok.com");

        var result = cleaner.Clean(new[] { Select(tiktok, "idb") }, Real(confirmed: true));

        var name = Assert.Single(result.Entries).TargetDisplayName;
        Assert.Contains("www.tiktok.com", name);
        Assert.Contains("IndexedDB", name);
    }
}
