using System.Diagnostics;
using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Анализ диска «что занимает место». ТОЛЬКО ЧТЕНИЕ — ничего не удаляет и не меняет.
/// Один проход по дереву: считает размеры папок, ищет самые тяжёлые файлы и папки,
/// известные места под очистку, папки-пожиратели (node_modules, .gradle, Unity Library и т.д.)
/// и суммарные размеры по расширениям. Не заходит в symlink/junction (через
/// <see cref="ReparsePointGuard"/>), устойчив к недоступным папкам, поддерживает отмену.
/// </summary>
public sealed class DiskAnalyzer
{
    private static readonly string[] SimpleSuspectNames =
    {
        "node_modules", "__pycache__", ".gradle", ".m2", ".cache", "Pods",
    };

    // По умолчанию EnumerationOptions пропускает Hidden и System файлы — а hiberfil.sys,
    // pagefile.sys и многие кэши именно такие, поэтому AttributesToSkip обязательно обнуляем.
    private static readonly EnumerationOptions EnumerationOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    private readonly ReparsePointGuard _guard;
    private readonly IReadOnlyList<KnownPlaceDefinition> _knownPlaces;

    public DiskAnalyzer(ReparsePointGuard guard, IEnumerable<KnownPlaceDefinition>? knownPlaces = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _knownPlaces = (knownPlaces ?? DefaultKnownPlaces()).ToList();
    }

    public Task<DiskAnalysisResult> AnalyzeAsync(
        string rootPath,
        DiskAnalysisOptions? options = null,
        IProgress<DiskAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(rootPath, options, progress, cancellationToken), cancellationToken);

    public DiskAnalysisResult Analyze(
        string rootPath,
        DiskAnalysisOptions? options = null,
        IProgress<DiskAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Путь для анализа не задан.", nameof(rootPath));

        options ??= new DiskAnalysisOptions();
        var root = NormalizeRoot(rootPath);
        var stopwatch = Stopwatch.StartNew();
        var state = new ScanState(options, progress, cancellationToken);

        long scannedBytes = 0;
        var cancelled = false;
        try
        {
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Путь не найден: {root}");

            if (_guard.IsReparsePoint(root))
            {
                state.AddSkip(root, SkipReason.ReparsePoint, "корень анализа — ссылка (reparse point)");
            }
            else
            {
                scannedBytes = ScanDirectory(root, 0, state).Size;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        stopwatch.Stop();
        return BuildResult(root, options, state, scannedBytes, stopwatch.Elapsed, cancelled);
    }

    // ---------------------------------------------------------------- сканирование

    private (long Size, DateTime? Newest) ScanDirectory(string path, int depth, ScanState s)
    {
        s.Token.ThrowIfCancellationRequested();
        s.DirectoriesScanned++;
        s.ReportProgress(path);

        if (depth > s.Options.MaxDepth)
        {
            s.AddSkip(path, SkipReason.IOError, "слишком глубокая вложенность");
            return (0, null);
        }

        long total = 0;
        DateTime? newest = null;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            foreach (var info in dirInfo.EnumerateFileSystemInfos("*", EnumerationOpts))
            {
                s.Token.ThrowIfCancellationRequested();
                try
                {
                    if (info is DirectoryInfo sub)
                    {
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || _guard.IsReparsePoint(sub.FullName))
                        {
                            s.AddSkip(sub.FullName, SkipReason.ReparsePoint, null);
                            continue;
                        }

                        var child = ScanDirectory(sub.FullName, depth + 1, s);
                        total += child.Size;
                        newest = Latest(newest, child.Newest);
                    }
                    else if (info is FileInfo file)
                    {
                        // Файл-ссылка / облачная заглушка (OneDrive): логический размер не равен
                        // занятому месту на диске, поэтому не считаем.
                        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            s.AddSkip(file.FullName, SkipReason.ReparsePoint, null);
                            continue;
                        }

                        var length = file.Length;
                        var written = file.LastWriteTimeUtc;
                        total += length;
                        newest = Latest(newest, written);
                        s.RegisterFile(file.FullName, file.Name, length, written, depth, depth == 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    s.AddSkip(info.FullName, ClassifyError(ex), ex.GetType().Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s.AddSkip(path, ClassifyError(ex), ex.GetType().Name);
        }

        if (total >= s.Options.MinRecordedDirBytes || depth <= 1)
            s.Records[path] = new DirRecord(total, depth, newest);

        if (depth >= 1 && total >= s.Options.BigDirBytes)
        {
            var group = ClassifySuspect(path);
            if (group is not null)
                s.Suspects.Add(new SuspectRecord(group, path, total, depth, newest));
        }

        return (total, newest);
    }

    private static SkipReason ClassifyError(Exception ex)
        => ex is UnauthorizedAccessException ? SkipReason.AccessDenied : SkipReason.IOError;

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);

    // ------------------------------------------------------- папки-пожиратели

    /// <summary>
    /// Возвращает имя группы, если папка — типичный «пожиратель места», иначе null.
    /// Неоднозначные имена (venv, Library, target) принимаются только при подтверждающем признаке рядом,
    /// чтобы не считать, например, модуль Lib\venv внутри Python за виртуальное окружение.
    /// </summary>
    private static string? ClassifySuspect(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            return null;

        foreach (var simple in SimpleSuspectNames)
        {
            if (string.Equals(simple, name, StringComparison.OrdinalIgnoreCase))
                return simple;
        }

        try
        {
            var parent = Path.GetDirectoryName(path);

            if (Eq(name, "venv") || Eq(name, ".venv"))
                return File.Exists(Path.Combine(path, "pyvenv.cfg")) ? "venv (Python)" : null;

            if (parent is null)
                return null;

            if (Eq(name, "Library"))
            {
                return Directory.Exists(Path.Combine(parent, "Assets")) &&
                       Directory.Exists(Path.Combine(parent, "ProjectSettings"))
                    ? "Unity Library"
                    : null;
            }

            if (Eq(name, "target"))
            {
                if (File.Exists(Path.Combine(parent, "Cargo.toml"))) return "Rust target";
                if (File.Exists(Path.Combine(parent, "pom.xml"))) return "Maven target";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Нет доступа проверить признак — считаем, что это не пожиратель.
        }

        return null;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- сборка результата

    private DiskAnalysisResult BuildResult(
        string root, DiskAnalysisOptions o, ScanState s, long scannedBytes, TimeSpan elapsed, bool cancelled)
    {
        var records = s.Records;

        AnalyzedItem DirItem(string path, DirRecord r) => new()
        {
            Path = path,
            SizeBytes = r.Size,
            IsDirectory = true,
            Depth = r.Depth,
            LastModifiedUtc = r.Newest,
        };

        var topLevel = records
            .Where(kv => kv.Value.Depth == 1)
            .OrderByDescending(kv => kv.Value.Size)
            .Take(o.TopLevelFoldersCount)
            .Select(kv => DirItem(kv.Key, kv.Value))
            .ToList();

        var rootFiles = s.RootFiles
            .OrderByDescending(f => f.SizeBytes)
            .Take(o.RootFilesCount)
            .ToList();

        // Известные места: размер берём из уже посчитанных папок (повторного обхода нет).
        var known = new List<KnownPlaceResult>();
        foreach (var def in _knownPlaces)
        {
            var key = SafeNormalize(def.Path);
            if (key is not null && records.TryGetValue(key, out var r) && r.Size >= o.MinRecordedDirBytes)
            {
                known.Add(new KnownPlaceResult
                {
                    Label = def.Label,
                    Path = key,
                    SizeBytes = r.Size,
                    Hint = def.Hint,
                    LastModifiedUtc = r.Newest,
                });
            }
        }
        known = known.OrderByDescending(k => k.SizeBytes).ToList();

        // Тяжёлые папки глубины [Min..Max] с «схлопыванием» цепочек.
        var maxChild = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (o.CollapseSingleChildChains)
        {
            foreach (var (path, r) in records)
            {
                if (r.Depth < 2 || r.Depth > o.MaxReportedDepth)
                    continue;
                var parent = Path.GetDirectoryName(path);
                if (parent is null)
                    continue;
                if (!maxChild.TryGetValue(parent, out var cur) || r.Size > cur)
                    maxChild[parent] = r.Size;
            }
        }

        var heaviestDirs = records
            .Where(kv => kv.Value.Depth >= o.MinReportedDepth &&
                         kv.Value.Depth <= o.MaxReportedDepth &&
                         kv.Value.Size >= o.BigDirBytes)
            .Where(kv => !(o.CollapseSingleChildChains &&
                           maxChild.TryGetValue(kv.Key, out var childSize) &&
                           childSize >= kv.Value.Size * o.CollapseRatio))
            .OrderByDescending(kv => kv.Value.Size)
            .Take(o.TopDirsCount)
            .Select(kv => DirItem(kv.Key, kv.Value))
            .ToList();

        var heaviestFiles = s.TopFiles.UnorderedItems
            .Select(x => x.Element)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();

        var suspectGroups = BuildSuspectGroups(s.Suspects);

        var extensions = s.ExtensionSizes
            .OrderByDescending(kv => kv.Value)
            .Take(o.TopExtensionsCount)
            .Select(kv => new ExtensionStat { Extension = kv.Key, SizeBytes = kv.Value })
            .ToList();

        var (total, free) = ReadDiskSpace(root);

        return new DiskAnalysisResult
        {
            RootPath = root,
            DiskTotalBytes = total,
            DiskFreeBytes = free,
            ScannedBytes = scannedBytes,
            FilesScanned = s.FilesScanned,
            DirectoriesScanned = s.DirectoriesScanned,
            Elapsed = elapsed,
            IsAdministrator = AdminRightsChecker.IsRunningAsAdministrator(),
            WasCancelled = cancelled,
            AccessDeniedCount = s.AccessDeniedCount,
            ReparsePointsSkippedCount = s.ReparsePointCount,
            IoErrorCount = s.IoErrorCount,
            TopLevelFolders = topLevel,
            RootFiles = rootFiles,
            KnownPlaces = known,
            HeaviestDirectories = heaviestDirs,
            HeaviestFiles = heaviestFiles,
            SuspectGroups = suspectGroups,
            TopExtensions = extensions,
            SkippedSamples = s.Skipped,
        };
    }

    private static IReadOnlyList<SuspectGroup> BuildSuspectGroups(List<SuspectRecord> suspects)
    {
        var groups = new List<SuspectGroup>();
        foreach (var byName in suspects.GroupBy(x => x.Group))
        {
            // Вложенные папки той же группы (node_modules внутри node_modules) не считаем второй раз.
            var kept = new List<SuspectRecord>();
            foreach (var item in byName.OrderBy(x => x.Path.Length))
            {
                var nested = kept.Any(k => item.Path.StartsWith(
                    k.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                if (!nested)
                    kept.Add(item);
            }

            groups.Add(new SuspectGroup
            {
                Name = byName.Key,
                Items = kept
                    .OrderByDescending(k => k.Size)
                    .Select(k => new AnalyzedItem
                    {
                        Path = k.Path,
                        SizeBytes = k.Size,
                        IsDirectory = true,
                        Depth = k.Depth,
                        LastModifiedUtc = k.Newest,
                    })
                    .ToList(),
            });
        }

        return groups.OrderByDescending(g => g.TotalBytes).ToList();
    }

    private static (long? Total, long? Free) ReadDiskSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return (drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    // ------------------------------------------------------------------- пути

    private static string NormalizeRoot(string rootPath)
    {
        var trimmed = rootPath.Trim();
        // "C:" без слеша означает «текущая папка на диске C», а не корень — приводим к "C:\".
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            trimmed += Path.DirectorySeparatorChar;
        return TrimTrailingSeparator(Path.GetFullPath(trimmed));
    }

    private static string? SafeNormalize(string path)
    {
        try
        {
            return TrimTrailingSeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string TrimTrailingSeparator(string path)
    {
        var rootLen = Path.GetPathRoot(path)?.Length ?? 0;
        return path.Length > rootLen
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    // ---------------------------------------------------- известные места по умолчанию

    /// <summary>Список мест из disk_scan.py: кандидаты на очистку и папки, которые трогать нельзя.</summary>
    public static IReadOnlyList<KnownPlaceDefinition> DefaultKnownPlaces()
    {
        var sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar;
        var win = Environment.GetEnvironmentVariable("WINDIR") ?? Path.Combine(sysDrive, "Windows");
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var roaming = Environment.GetEnvironmentVariable("APPDATA");
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");

        const string noTouch = "НЕ трогать вручную";
        var list = new List<KnownPlaceDefinition>();

        void Add(string label, string? baseDir, string? hint, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                return;
            list.Add(new KnownPlaceDefinition(label, Path.Combine(new[] { baseDir }.Concat(parts).ToArray()), hint));
        }

        Add("Windows.old (старая версия Windows)", sysDrive, null, "Windows.old");
        Add("Кэш Windows Update", win, null, "SoftwareDistribution", "Download");
        Add("Windows Temp", win, null, "Temp");
        Add("Windows Logs", win, null, "Logs");
        Add("Windows Minidump", win, null, "Minidump");
        Add("Windows LiveKernelReports", win, null, "LiveKernelReports");
        Add("WinSxS", win, noTouch, "WinSxS");
        Add("Windows Installer", win, noTouch, "Installer");
        Add("Temp пользователя", local, null, "Temp");
        Add("CrashDumps", local, null, "CrashDumps");
        Add("D3DSCache (шейдеры DirectX)", local, null, "D3DSCache");
        Add("NVIDIA (DXCache/GLCache)", local, null, "NVIDIA");
        Add("Кэш Chrome", local, null, "Google", "Chrome", "User Data", "Default", "Cache");
        Add("Кэш Edge", local, null, "Microsoft", "Edge", "User Data", "Default", "Cache");
        Add("Discord", roaming, null, "discord");
        Add("Steam (Local)", local, null, "Steam");
        Add("pip cache", local, null, "pip", "Cache");
        Add("npm cache", local, null, "npm-cache");
        Add("NuGet packages", profile, null, ".nuget", "packages");
        Add("Gradle cache", profile, null, ".gradle");
        Add("Maven repo", profile, null, ".m2");
        Add("Docker Desktop", local, null, "Docker");
        Add("Local\\Packages (приложения Store, WSL)", local, null, "Packages");
        Add("Загрузки", profile, null, "Downloads");
        Add("Рабочий стол", profile, null, "Desktop");
        Add("ProgramData", sysDrive, null, "ProgramData");
        Add("Корзина", sysDrive, null, "$Recycle.Bin");
        return list;
    }

    // ------------------------------------------------------------- внутреннее состояние

    private readonly record struct DirRecord(long Size, int Depth, DateTime? Newest);

    private readonly record struct SuspectRecord(string Group, string Path, long Size, int Depth, DateTime? Newest);

    private sealed class ScanState
    {
        private readonly IProgress<DiskAnalysisProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = -1000;

        public ScanState(DiskAnalysisOptions options, IProgress<DiskAnalysisProgress>? progress, CancellationToken token)
        {
            Options = options;
            _progress = progress;
            Token = token;
        }

        public DiskAnalysisOptions Options { get; }
        public CancellationToken Token { get; }

        public long FilesScanned { get; private set; }
        public long DirectoriesScanned { get; set; }
        public int AccessDeniedCount { get; private set; }
        public int ReparsePointCount { get; private set; }
        public int IoErrorCount { get; private set; }

        public Dictionary<string, DirRecord> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> ExtensionSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PriorityQueue<AnalyzedItem, long> TopFiles { get; } = new();
        public List<AnalyzedItem> RootFiles { get; } = new();
        public List<SuspectRecord> Suspects { get; } = new();
        public List<SkippedItem> Skipped { get; } = new();

        public void RegisterFile(string fullPath, string name, long length, DateTime writtenUtc, int parentDepth, bool isInRoot)
        {
            FilesScanned++;

            var ext = Path.GetExtension(name);
            ext = string.IsNullOrEmpty(ext) ? "(без расширения)" : ext.ToLowerInvariant();
            ExtensionSizes[ext] = ExtensionSizes.TryGetValue(ext, out var cur) ? cur + length : length;

            if (isInRoot)
            {
                RootFiles.Add(new AnalyzedItem
                {
                    Path = fullPath,
                    SizeBytes = length,
                    IsDirectory = false,
                    Depth = parentDepth + 1,
                    LastModifiedUtc = writtenUtc,
                });
            }

            if (length >= Options.BigFileBytes)
            {
                TopFiles.Enqueue(new AnalyzedItem
                {
                    Path = fullPath,
                    SizeBytes = length,
                    IsDirectory = false,
                    Depth = parentDepth + 1,
                    LastModifiedUtc = writtenUtc,
                }, length);

                if (TopFiles.Count > Options.TopFilesCount)
                    TopFiles.Dequeue(); // выкидываем самый маленький
            }
        }

        public void AddSkip(string path, SkipReason reason, string? detail)
        {
            switch (reason)
            {
                case SkipReason.AccessDenied: AccessDeniedCount++; break;
                case SkipReason.ReparsePoint: ReparsePointCount++; break;
                default: IoErrorCount++; break;
            }

            if (Skipped.Count < Options.MaxSkippedSamples)
                Skipped.Add(new SkippedItem { Path = path, Reason = reason, Detail = detail });
        }

        public void ReportProgress(string currentPath)
        {
            if (_progress is null)
                return;
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastReportMs < 200)
                return;
            _lastReportMs = now;
            _progress.Report(new DiskAnalysisProgress(FilesScanned, DirectoriesScanned, currentPath));
        }
    }
}
