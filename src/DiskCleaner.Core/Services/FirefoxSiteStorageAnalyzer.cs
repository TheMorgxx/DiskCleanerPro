using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>Известные типы данных сайта и их описание. Это же — список разрешённых целей для удаления.</summary>
public static class FirefoxStorageLayout
{
    public sealed record PartInfo(FirefoxStorageKind Kind, string FolderName, string Title, FirefoxStorageRisk Risk, string Note);

    public static readonly IReadOnlyList<PartInfo> KnownParts = new[]
    {
        new PartInfo(FirefoxStorageKind.CacheApi, "cache", "Cache API", FirefoxStorageRisk.Low,
            "Кэш, который ведёт сам сайт. Сайт пересоздаст его при следующем заходе."),
        new PartInfo(FirefoxStorageKind.IndexedDb, "idb", "IndexedDB", FirefoxStorageRisk.Medium,
            "Локальная база сайта: могут быть черновики и офлайн-данные. Часть сайтов хранит здесь и состояние входа — тогда войти придётся заново."),
        new PartInfo(FirefoxStorageKind.LocalStorage, "ls", "localStorage", FirefoxStorageRisk.High,
            "Настройки сайта и часто токены входа. После удаления сайт может попросить войти заново."),
        new PartInfo(FirefoxStorageKind.FileSystem, "fs", "Файлы сайта (OPFS)", FirefoxStorageRisk.High,
            "Файлы, которые сайт хранит у тебя (например, проекты веб-редакторов). Могут не существовать больше нигде."),
    };

    public static PartInfo? Find(string folderName)
        => KnownParts.FirstOrDefault(p => string.Equals(p.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Разобранное имя папки источника из storage\default, например https+++www.tiktok.com.</summary>
public sealed record FirefoxOriginInfo(
    string Scheme,
    string Host,
    int? Port,
    int? ContainerId,
    string? PartitionKey,
    bool IsExtension,
    string DisplayName)
{
    public static FirefoxOriginInfo Parse(string folderName)
    {
        var main = folderName;
        int? container = null;
        string? partition = null;

        var caret = folderName.IndexOf('^');
        if (caret >= 0)
        {
            main = folderName[..caret];
            foreach (var attribute in folderName[(caret + 1)..].Split('^', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = attribute.IndexOf('=');
                if (eq <= 0)
                    continue;

                var key = attribute[..eq];
                var value = attribute[(eq + 1)..];
                if (key.Equals("userContextId", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var id))
                    container = id;
                else if (key.Equals("partitionKey", StringComparison.OrdinalIgnoreCase))
                    partition = Uri.UnescapeDataString(value);
            }
        }

        string scheme;
        var host = "";
        int? port = null;

        var marker = main.IndexOf("+++", StringComparison.Ordinal);
        if (marker < 0)
        {
            scheme = main;
        }
        else
        {
            scheme = main[..marker];
            host = main[(marker + 3)..];

            if (scheme is "http" or "https")
            {
                var plus = host.LastIndexOf('+');
                if (plus > 0 && int.TryParse(host[(plus + 1)..], out var parsedPort))
                {
                    port = parsedPort;
                    host = host[..plus];
                }
            }
        }

        var isExtension = scheme.Equals("moz-extension", StringComparison.OrdinalIgnoreCase);

        var display = host.Length > 0 ? host : folderName;
        if (port is not null)
            display += ":" + port;
        if (container is not null)
            display += $" [контейнер {container}]";
        if (partition is not null)
            display += $" [раздел {partition}]";
        if (isExtension)
            display = "Расширение " + display;

        return new FirefoxOriginInfo(scheme, host, port, container, partition, isExtension, display);
    }
}

/// <summary>
/// Анализ данных сайтов в профиле Firefox: сколько каждый сайт занимает и в каких типах данных
/// (IndexedDB, Cache API, localStorage, OPFS). Только чтение. Куки, пароли, историю и закладки не
/// читает и не показывает — они лежат вне storage\default.
/// </summary>
public sealed class FirefoxSiteStorageAnalyzer
{
    private readonly SizeCalculator _sizes;
    private readonly ReparsePointGuard _guard;

    public FirefoxSiteStorageAnalyzer(SizeCalculator sizes, ReparsePointGuard guard)
    {
        _sizes = sizes ?? throw new ArgumentNullException(nameof(sizes));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public FirefoxStorageReport Analyze(
        FirefoxProfile profile,
        FirefoxAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new FirefoxAnalysisOptions();

        var skipped = new List<SkippedItem>();
        var sites = new List<FirefoxSiteEntry>();
        var hiddenCount = 0;
        long hiddenBytes = 0;
        var cancelled = false;

        var storageDefault = profile.StorageDefaultPath;
        if (Directory.Exists(storageDefault))
        {
            try
            {
                List<string> originDirs;
                try
                {
                    originDirs = Directory.EnumerateDirectories(storageDefault).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new SkippedItem { Path = storageDefault, Reason = SkipReason.AccessDenied, Detail = ex.Message });
                    originDirs = new List<string>();
                }

                foreach (var originDir in originDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_guard.IsReparsePoint(originDir))
                    {
                        skipped.Add(new SkippedItem { Path = originDir, Reason = SkipReason.ReparsePoint });
                        continue;
                    }

                    var site = BuildSite(profile, originDir, options, skipped, cancellationToken);
                    if (site.TotalBytes < options.MinSiteBytes)
                    {
                        hiddenCount++;
                        hiddenBytes += site.TotalBytes;
                        continue;
                    }

                    sites.Add(site);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }

        return new FirefoxStorageReport
        {
            Profile = profile,
            Sites = sites.OrderByDescending(s => s.TotalBytes).ToList(),
            HiddenSmallSitesCount = hiddenCount,
            HiddenSmallSitesBytes = hiddenBytes,
            WasCancelled = cancelled,
            Skipped = skipped,
        };
    }

    private FirefoxSiteEntry BuildSite(
        FirefoxProfile profile,
        string originDir,
        FirefoxAnalysisOptions options,
        List<SkippedItem> skipped,
        CancellationToken ct)
    {
        var folderName = Path.GetFileName(originDir);
        var origin = FirefoxOriginInfo.Parse(folderName);

        var parts = new List<FirefoxStoragePart>();
        var otherFolders = new List<string>();
        long otherBytes = 0;
        DateTime? newest = null;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(originDir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedItem { Path = originDir, Reason = SkipReason.AccessDenied, Detail = ex.Message });
            children = Array.Empty<string>();
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            if (_guard.IsReparsePoint(child))
            {
                skipped.Add(new SkippedItem { Path = child, Reason = SkipReason.ReparsePoint });
                continue;
            }

            var name = Path.GetFileName(child);
            var stats = _sizes.ComputeStats(child, null, skipped);
            newest = Latest(newest, stats.LastModifiedUtc);

            var info = FirefoxStorageLayout.Find(name);
            if (info is null)
            {
                otherFolders.Add(name);
                otherBytes += stats.SizeBytes;
                continue;
            }

            var suggested = !origin.IsExtension &&
                            info.Kind is FirefoxStorageKind.CacheApi or FirefoxStorageKind.IndexedDb &&
                            stats.SizeBytes >= options.SuggestedMinBytes;

            parts.Add(new FirefoxStoragePart
            {
                Kind = info.Kind,
                FolderName = info.FolderName,
                Path = child,
                SizeBytes = stats.SizeBytes,
                FileCount = stats.FileCount,
                LastModifiedUtc = stats.LastModifiedUtc,
                Risk = info.Risk,
                Note = info.Note,
                IsSuggested = suggested,
            });
        }

        return new FirefoxSiteEntry
        {
            ProfilePath = profile.Path,
            OriginFolderName = folderName,
            OriginPath = originDir,
            Scheme = origin.Scheme,
            Host = origin.Host,
            Port = origin.Port,
            ContainerId = origin.ContainerId,
            PartitionKey = origin.PartitionKey,
            IsExtension = origin.IsExtension,
            DisplayName = origin.DisplayName,
            Parts = parts.OrderByDescending(p => p.SizeBytes).ToList(),
            OtherFolders = otherFolders,
            OtherBytes = otherBytes,
            LastModifiedUtc = newest,
        };
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
