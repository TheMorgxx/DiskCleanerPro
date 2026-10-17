using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;

namespace DiskCleaner.App.ViewModels;

/// <summary>
/// Окно «Анализ диска»: показывает, что занимает место (<see cref="DiskAnalyzer"/>), и позволяет удалить
/// выбранные файлы и папки (<see cref="ManualDeletionService"/>). Блокированные правилами безопасности пути
/// выбрать нельзя; личные файлы и данные профиля браузера требуют отдельного подтверждения.
/// </summary>
public sealed class DiskAnalysisViewModel : ObservableObject
{
    private const int MaxListedInConfirmation = 40;

    private readonly DiskAnalyzer _analyzer;
    private readonly ManualDeletionService _deletion;
    private readonly Action<CleanupResult> _recordResult;
    private readonly string _reportDirectory;
    private readonly DiskAnalysisOptions? _analysisOptions;
    private readonly PersonalContentIndex _personalIndex;
    private readonly SensitiveSummaryBuilder _summaryBuilder;
    private readonly Func<IEnumerable<string>> _excludedPathsProvider;
    private readonly Func<string, bool> _excludePath;

    private CancellationTokenSource? _cts;
    private DiskAnalysisResult? _lastResult;
    private bool _isShuttingDown;

    public DiskAnalysisViewModel(
        DiskAnalyzer analyzer,
        ManualDeletionService deletion,
        IEnumerable<string> roots,
        string defaultRoot,
        Action<CleanupResult> recordResult,
        string reportDirectory,
        PersonalContentIndex personalIndex,
        SensitiveSummaryBuilder summaryBuilder,
        Func<IEnumerable<string>> excludedPathsProvider,
        Func<string, bool> excludePath,
        DiskAnalysisOptions? analysisOptions = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _recordResult = recordResult ?? throw new ArgumentNullException(nameof(recordResult));
        _reportDirectory = reportDirectory;
        _analysisOptions = analysisOptions;
        _personalIndex = personalIndex ?? throw new ArgumentNullException(nameof(personalIndex));
        _summaryBuilder = summaryBuilder ?? throw new ArgumentNullException(nameof(summaryBuilder));
        _excludedPathsProvider = excludedPathsProvider ?? throw new ArgumentNullException(nameof(excludedPathsProvider));
        _excludePath = excludePath ?? throw new ArgumentNullException(nameof(excludePath));

        foreach (var root in roots)
            Roots.Add(root);
        _selectedRoot = Roots.FirstOrDefault(r => string.Equals(r, defaultRoot, StringComparison.OrdinalIgnoreCase))
                        ?? Roots.FirstOrDefault()
                        ?? defaultRoot;

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedRoot));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => !IsBusy && HasSelection);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        CopyReportCommand = new RelayCommand(CopyReport, () => _lastResult is not null);
        SaveReportCommand = new RelayCommand(SaveReport, () => _lastResult is not null);
        ExcludePathCommand = new RelayCommand(
            parameter => ExcludePath(parameter as string),
            parameter => !IsBusy && parameter is string path && !string.IsNullOrWhiteSpace(path));
    }

    // --- Данные для UI ---
    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<AnalysisRowViewModel> KnownPlaceRows { get; } = [];
    public ObservableCollection<AnalysisRowViewModel> DirectoryRows { get; } = [];
    public ObservableCollection<AnalysisRowViewModel> FileRows { get; } = [];
    public ObservableCollection<AnalysisRowViewModel> SuspectRows { get; } = [];
    public ObservableCollection<ExtensionStat> Extensions { get; } = [];

    // --- Колбэки из окна (code-behind) ---
    public Func<string, bool>? ConfirmDeletion { get; set; }
    public Func<string, string?, bool>? ConfirmSensitive { get; set; }
    public Action<CleanupResult>? ShowResults { get; set; }
    public Func<string, bool>? CopyToClipboard { get; set; }

    // --- Команды ---
    public ICommand AnalyzeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand ExcludePathCommand { get; }

    // --- Состояние ---
    private string _selectedRoot;
    public string SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (SetField(ref _selectedRoot, value))
                RefreshCommands();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RefreshCommands();
        }
    }

    private string _statusText = "Выберите диск и нажмите «Анализ».";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    private string _headerText = "";
    public string HeaderText
    {
        get => _headerText;
        private set => SetField(ref _headerText, value);
    }

    private bool _showAdminWarning;
    public bool ShowAdminWarning
    {
        get => _showAdminWarning;
        private set => SetField(ref _showAdminWarning, value);
    }

    private bool _isDryRun = true;
    public bool IsDryRun
    {
        get => _isDryRun;
        set => SetField(ref _isDryRun, value);
    }

    private bool _useRecycleBin;
    public bool UseRecycleBin
    {
        get => _useRecycleBin;
        set => SetField(ref _useRecycleBin, value);
    }

    private string _selectedSizeText = "Ничего не выбрано";
    public string SelectedSizeText
    {
        get => _selectedSizeText;
        private set => SetField(ref _selectedSizeText, value);
    }

    public bool HasSelection => GetTopLevelSelection().Count > 0;

    // ------------------------------------------------------------------ анализ

    private async Task AnalyzeAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        ClearResults();
        StatusText = $"Анализирую {SelectedRoot} (только чтение, ничего не удаляется)...";
        ProgressText = "";

        try
        {
            var progress = new Progress<DiskAnalysisProgress>(p =>
                ProgressText = $"Файлов: {p.FilesScanned:N0} • папок: {p.DirectoriesScanned:N0} • {p.CurrentPath}");

            var result = await _analyzer.AnalyzeAsync(SelectedRoot, _analysisOptions, progress, token);
            // Частичный результат тоже заменяет снимок этого корня до проверки строк через Evaluate.
            _personalIndex.ReplaceForRoot(result.RootPath, result.PersonalFolders);
            var rows = await Task.Run(() => BuildRows(result), CancellationToken.None);

            if (_isShuttingDown)
                return;

            _lastResult = result;
            Fill(KnownPlaceRows, rows.KnownPlaces);
            Fill(DirectoryRows, rows.Directories);
            Fill(FileRows, rows.Files);
            Fill(SuspectRows, rows.Suspects);
            RemoveExcludedRows();
            foreach (var ext in result.TopExtensions)
                Extensions.Add(ext);

            HeaderText = BuildHeader(result);
            ShowAdminWarning = !result.IsAdministrator;
            StatusText = result.WasCancelled
                ? "Анализ отменён — данные частичные."
                : $"Готово: {result.FilesScanned:N0} файлов за {result.Elapsed.TotalSeconds:F0} сек.";
            RecalculateSelection();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Анализ отменён.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка анализа: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshCommands();
        }
    }

    private sealed record RowSet(
        List<AnalysisRowViewModel> KnownPlaces,
        List<AnalysisRowViewModel> Directories,
        List<AnalysisRowViewModel> Files,
        List<AnalysisRowViewModel> Suspects);

    /// <summary>Строит строки и проверяет каждый путь правилами безопасности (выполняется не в потоке UI).</summary>
    private RowSet BuildRows(DiskAnalysisResult r)
    {
        // Снимок именно этого анализа: другой открытый анализ не снимает блокировку частичных строк.
        var indexedFolders = r.PersonalFolders.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedPaths = ReadExcludedPaths();
        bool IsVisible(string path) => !excludedPaths.Any(excluded => IsUnderOrEqual(excluded, path));

        AnalysisRowViewModel Row(string source, string title, string path, long size, bool isDir,
            DateTime? modified, string? hint, bool regenerates = false, string? groupDescription = null)
            => new(source, title, path, size, isDir, modified, _deletion.Evaluate(path), hint,
                regenerates, groupDescription,
                contentUnchecked: r.WasCancelled && isDir && !indexedFolders.Contains(path));

        return new RowSet(
            r.KnownPlaces.Where(k => IsVisible(k.Path))
                .Select(k => Row("Известное место", k.Label, k.Path, k.SizeBytes, true, k.LastModifiedUtc, k.Hint, k.Regenerates)).ToList(),
            r.HeaviestDirectories.Where(d => IsVisible(d.Path))
                .Select(d => Row("Папка", LeafName(d.Path), d.Path, d.SizeBytes, true, d.LastModifiedUtc, null)).ToList(),
            r.HeaviestFiles.Where(f => IsVisible(f.Path))
                .Select(f => Row("Файл", LeafName(f.Path), f.Path, f.SizeBytes, false, f.LastModifiedUtc, null)).ToList(),
            r.SuspectGroups.SelectMany(g => g.Items.Where(i => IsVisible(i.Path))
                .Select(i => Row(g.Name, g.Name, i.Path, i.SizeBytes, true, i.LastModifiedUtc, null,
                    groupDescription: g.Description))).ToList());
    }

    private void ExcludePath(string? path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
            return;

        if (!_excludePath(path))
        {
            StatusText = "Не удалось сохранить исключение. Строки не скрыты; проверьте доступ к файлу настроек.";
            return;
        }

        RemoveExcludedRows();
        RecalculateSelection();
        StatusText = $"Путь больше не предлагается: {path}. Отменить можно в окне «Настройки».";
    }

    private string[] ReadExcludedPaths()
    {
        var paths = new List<string>();
        foreach (var path in _excludedPathsProvider())
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                paths.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Как и в Core, некорректная запись настроек не является исключённым путём.
            }
        }
        return paths.ToArray();
    }

    private void RemoveExcludedRows()
    {
        var paths = ReadExcludedPaths();
        foreach (var collection in AllRowCollections())
        {
            var excludedRows = collection.Where(row => paths.Any(path => IsUnderOrEqual(path, row.Path))).ToList();
            foreach (var row in excludedRows)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                collection.Remove(row);
            }
        }
    }

    private void Fill(ObservableCollection<AnalysisRowViewModel> target, IEnumerable<AnalysisRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            target.Add(row);
        }
    }

    private void ClearResults()
    {
        foreach (var collection in AllRowCollections())
        {
            foreach (var row in collection)
                row.PropertyChanged -= OnRowPropertyChanged;
            collection.Clear();
        }

        Extensions.Clear();
        _lastResult = null;
        HeaderText = "";
        ShowAdminWarning = false;
        RecalculateSelection();
    }

    private static string BuildHeader(DiskAnalysisResult r)
    {
        var sb = new StringBuilder(r.RootPath);
        if (r.DiskTotalBytes is { } total && r.DiskFreeBytes is { } free && total > 0)
        {
            sb.Append($" — всего {CleanupTarget.FormatSize(total)}, свободно {CleanupTarget.FormatSize(free)} ({free * 100.0 / total:F1}%)");
        }

        sb.Append($". Прочитано {CleanupTarget.FormatSize(r.ScannedBytes)}");
        if (r.AccessDeniedCount > 0)
            sb.Append($", недоступно папок: {r.AccessDeniedCount:N0}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ выбор

    private IEnumerable<ObservableCollection<AnalysisRowViewModel>> AllRowCollections()
    {
        yield return KnownPlaceRows;
        yield return DirectoryRows;
        yield return FileRows;
        yield return SuspectRows;
    }

    private IEnumerable<AnalysisRowViewModel> AllRows() => AllRowCollections().SelectMany(c => c);

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisRowViewModel.IsSelected))
            RecalculateSelection();
    }

    /// <summary>Выбранные строки без дублей и без вложенных (папка и файл внутри неё считаются один раз).</summary>
    private List<AnalysisRowViewModel> GetTopLevelSelection()
    {
        var chosen = AllRows()
            .Where(r => r.IsSelected && r.CanSelect)
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Path.Length)
            .ToList();

        var top = new List<AnalysisRowViewModel>();
        foreach (var row in chosen)
        {
            if (!top.Any(t => IsUnderOrEqual(t.Path, row.Path)))
                top.Add(row);
        }

        return top;
    }

    private void RecalculateSelection()
    {
        var selection = GetTopLevelSelection();
        SelectedSizeText = selection.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {CleanupTarget.FormatSize(selection.Sum(r => r.SizeBytes))} ({selection.Count} шт.)";
        OnPropertyChanged(nameof(HasSelection));
        RefreshCommands();
    }

    private void ClearSelection()
    {
        foreach (var row in AllRows())
            row.IsSelected = false;
    }

    // ------------------------------------------------------------------ удаление

    private async Task DeleteSelectedAsync()
    {
        if (IsBusy || _isShuttingDown)
            return;

        var selection = GetTopLevelSelection();
        if (selection.Count == 0)
            return;

        // Свежая проверка: за время работы с окном состояние диска и список исключений могли измениться.
        var checks = selection.Select(r => (Row: r, Verdict: _deletion.Evaluate(r.Path))).ToList();
        var allowed = checks.Where(c => c.Verdict.IsAllowed).ToList();
        var blockedCount = checks.Count - allowed.Count;

        if (allowed.Count == 0)
        {
            StatusText = $"Все выбранные пути заблокированы: {checks[0].Verdict.BlockReason}";
            return;
        }

        var sensitive = allowed.Where(c => c.Verdict.RequiresConfirmation).ToList();
        // Режим фиксируется до await: переключение флажков во время подсчёта не меняет эту операцию.
        var dryRun = IsDryRun;
        var useRecycleBin = UseRecycleBin;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;

        try
        {
            var confirmedSensitive = true; // в пробном прогоне ничего не удаляется — подтверждения не нужны
            if (!dryRun)
            {
                var message = BuildConfirmationMessage(allowed.Select(c => c.Row).ToList(), blockedCount, useRecycleBin);
                if (!(ConfirmDeletion?.Invoke(message) ?? false))
                {
                    StatusText = "Удаление отменено пользователем.";
                    return;
                }
                token.ThrowIfCancellationRequested();

                if (sensitive.Count > 0)
                {
                    // GetTopLevelSelection уже убрал дубли и вложенные выбранные пути.
                    var personalPaths = sensitive
                        .Where(c => c.Verdict.Sensitivity is DeletionSensitivity.PersonalFiles or DeletionSensitivity.PersonalData)
                        .Select(c => c.Row.Path).ToList();
                    SensitiveSummary? summary = null;
                    if (personalPaths.Count > 0)
                    {
                        StatusText = "считаю…";
                        ProgressText = "Сводка по всем файлам в выбранных личных путях. Подсчёт можно отменить.";
                        summary = await Task.Run(() => _summaryBuilder.Build(personalPaths, cancellationToken: token), token);
                        ProgressText = "";
                    }
                    token.ThrowIfCancellationRequested();

                    var requiredWord = summary is not null && summary.TotalBytes >= PersonalDataRules.TypedConfirmationBytes
                        ? PersonalDataRules.ConfirmWord
                        : null;
                    StatusText = "Ожидаю подтверждения удаления чувствительных данных.";
                    // Отказ во втором подтверждении сохраняет прежнее поведение: чувствительные пути пропускаются.
                    confirmedSensitive = ConfirmSensitive?.Invoke(BuildSensitiveMessage(sensitive, summary), requiredWord) ?? false;
                    token.ThrowIfCancellationRequested();
                }
            }

            var options = new ManualDeletionOptions
            {
                DryRun = dryRun,
                UseRecycleBin = useRecycleBin,
                ConfirmedSensitive = confirmedSensitive,
            };
            var paths = allowed.Select(c => c.Row.Path).ToList();
            StatusText = dryRun ? "Пробный прогон..." : "Удаляю...";
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await Task.Run(() => _deletion.Delete(paths, options, progress, token), token);

            _recordResult(result);

            var recycleNote = options.UseRecycleBin && !options.DryRun
                ? " Файлы в Корзине: место освободится после её очистки."
                : "";
            StatusText = dryRun
                ? $"Пробный прогон завершён. Освободилось бы: {result.TotalFreedDisplay}."
                : $"Готово. Освобождено: {result.TotalFreedDisplay}.{recycleNote}";

            if (!_isShuttingDown)
                ShowResults?.Invoke(result);

            if (!dryRun)
                PruneMissingRows();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Операция отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка удаления: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RecalculateSelection();
        }
    }

    /// <summary>После реального удаления убирает из таблиц строки, которых больше нет на диске.</summary>
    private void PruneMissingRows()
    {
        foreach (var collection in AllRowCollections())
        {
            var gone = collection.Where(r => !Directory.Exists(r.Path) && !File.Exists(r.Path)).ToList();
            foreach (var row in gone)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                collection.Remove(row);
            }
        }
    }

    private static string BuildConfirmationMessage(IReadOnlyList<AnalysisRowViewModel> items, int blockedCount, bool useRecycleBin)
    {
        var total = CleanupTarget.FormatSize(items.Sum(r => r.SizeBytes));
        var how = useRecycleBin
            ? "будут отправлены в Корзину (место на диске освободится только после очистки Корзины)"
            : "будут удалены БЕЗВОЗВРАТНО";

        var sb = new StringBuilder();
        sb.AppendLine($"Выбрано объектов: {items.Count} (≈{total}). Они {how}:");
        sb.AppendLine();
        foreach (var item in items.Take(MaxListedInConfirmation))
            sb.AppendLine($"• {item.SizeDisplay,10}  {item.Path}");
        if (items.Count > MaxListedInConfirmation)
            sb.AppendLine($"… и ещё {items.Count - MaxListedInConfirmation}");
        if (blockedCount > 0)
            sb.AppendLine().AppendLine($"Заблокировано правилами безопасности и пропущено: {blockedCount}.");
        sb.AppendLine().Append("Продолжить?");
        return sb.ToString();
    }

    private static string BuildSensitiveMessage(
        IReadOnlyList<(AnalysisRowViewModel Row, DeletionVerdict Verdict)> sensitive,
        SensitiveSummary? summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ВНИМАНИЕ: среди выбранного есть личные данные, содержимое папок проектов или данные профиля браузера:");
        sb.AppendLine();
        foreach (var (row, verdict) in sensitive.Take(MaxListedInConfirmation))
            sb.AppendLine($"• {row.SizeDisplay,10}  {row.Path}\n    ({verdict.SensitivityNote})");
        if (sensitive.Count > MaxListedInConfirmation)
            sb.AppendLine($"… и ещё {sensitive.Count - MaxListedInConfirmation}");

        if (summary is not null)
        {
            sb.AppendLine().AppendLine("Сводка по всем файлам под выбранными личными путями:");
            sb.AppendLine($"Файлов: {summary.FileCount:N0}");
            sb.AppendLine($"Суммарный размер: {CleanupTarget.FormatSize(summary.TotalBytes)} ({summary.TotalBytes:N0} байт)");
            if (summary.OldestModifiedUtc is { } oldest && summary.NewestModifiedUtc is { } newest)
                sb.AppendLine($"Даты изменения: {oldest.ToLocalTime():yyyy-MM-dd HH:mm} — {newest.ToLocalTime():yyyy-MM-dd HH:mm}");
            else
                sb.AppendLine("Даты изменения: нет данных.");

            if (summary.LargestFiles.Count > 0)
            {
                sb.AppendLine("Самые большие файлы (до 5):");
                foreach (var file in summary.LargestFiles)
                    sb.AppendLine($"• {CleanupTarget.FormatSize(file.SizeBytes),10}  {file.Path}");
            }
            if (summary.UnreadableCount > 0)
                sb.AppendLine($"Не удалось прочитать элементов: {summary.UnreadableCount:N0}. Сводка неполная.");
        }
        sb.AppendLine().AppendLine("«Удалить» — удалить и их тоже.");
        sb.Append("«Отмена» — удалить только остальное, эти пути будут пропущены.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ отчёт

    private void CopyReport()
    {
        if (_lastResult is null)
            return;

        var text = DiskAnalysisReportFormatter.Format(_lastResult);
        StatusText = CopyToClipboard?.Invoke(text) == true
            ? "Отчёт скопирован в буфер обмена."
            : "Не удалось скопировать в буфер обмена — попробуй «Сохранить отчёт».";
    }

    private void SaveReport()
    {
        if (_lastResult is null)
            return;

        try
        {
            Directory.CreateDirectory(_reportDirectory);
            var path = Path.Combine(_reportDirectory, $"disk_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, DiskAnalysisReportFormatter.Format(_lastResult), Encoding.UTF8);
            StatusText = $"Отчёт сохранён: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Не удалось сохранить отчёт: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ служебное

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Отмена... завершаю текущий шаг.";
    }

    /// <summary>Вызывается при закрытии окна: останавливает фоновую операцию.</summary>
    public void Shutdown()
    {
        _isShuttingDown = true;
        _cts?.Cancel();
    }

    private void RefreshCommands()
    {
        (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CopyReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExcludePathCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool IsUnderOrEqual(string root, string child)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(r, c, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
