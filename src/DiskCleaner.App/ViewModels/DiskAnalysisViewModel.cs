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

    private CancellationTokenSource? _cts;
    private DiskAnalysisResult? _lastResult;

    public DiskAnalysisViewModel(
        DiskAnalyzer analyzer,
        ManualDeletionService deletion,
        IEnumerable<string> roots,
        string defaultRoot,
        Action<CleanupResult> recordResult,
        string reportDirectory,
        DiskAnalysisOptions? analysisOptions = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _recordResult = recordResult ?? throw new ArgumentNullException(nameof(recordResult));
        _reportDirectory = reportDirectory;
        _analysisOptions = analysisOptions;

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
    public Func<string, bool>? ConfirmSensitive { get; set; }
    public Action<CleanupResult>? ShowResults { get; set; }
    public Func<string, bool>? CopyToClipboard { get; set; }

    // --- Команды ---
    public ICommand AnalyzeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand SaveReportCommand { get; }

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
            var rows = await Task.Run(() => BuildRows(result), CancellationToken.None);

            _lastResult = result;
            Fill(KnownPlaceRows, rows.KnownPlaces);
            Fill(DirectoryRows, rows.Directories);
            Fill(FileRows, rows.Files);
            Fill(SuspectRows, rows.Suspects);
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
        AnalysisRowViewModel Row(string source, string title, string path, long size, bool isDir, DateTime? modified, string? hint)
            => new(source, title, path, size, isDir, modified, _deletion.Evaluate(path), hint);

        return new RowSet(
            r.KnownPlaces.Select(k => Row("Известное место", k.Label, k.Path, k.SizeBytes, true, k.LastModifiedUtc, k.Hint)).ToList(),
            r.HeaviestDirectories.Select(d => Row("Папка", LeafName(d.Path), d.Path, d.SizeBytes, true, d.LastModifiedUtc, null)).ToList(),
            r.HeaviestFiles.Select(f => Row("Файл", LeafName(f.Path), f.Path, f.SizeBytes, false, f.LastModifiedUtc, null)).ToList(),
            r.SuspectGroups.SelectMany(g => g.Items.Select(i => Row(g.Name, g.Name, i.Path, i.SizeBytes, true, i.LastModifiedUtc, null))).ToList());
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
        var confirmedSensitive = true; // в пробном прогоне ничего не удаляется — подтверждения не нужны

        if (!IsDryRun)
        {
            var message = BuildConfirmationMessage(allowed.Select(c => c.Row).ToList(), blockedCount);
            if (!(ConfirmDeletion?.Invoke(message) ?? false))
            {
                StatusText = "Удаление отменено пользователем.";
                return;
            }

            // Отказ во втором подтверждении не отменяет всё: чувствительные пути просто будут пропущены сервисом.
            confirmedSensitive = sensitive.Count == 0 ||
                                 (ConfirmSensitive?.Invoke(BuildSensitiveMessage(sensitive)) ?? false);
        }

        var options = new ManualDeletionOptions
        {
            DryRun = IsDryRun,
            UseRecycleBin = UseRecycleBin,
            ConfirmedSensitive = confirmedSensitive,
        };
        var paths = allowed.Select(c => c.Row.Path).ToList();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        StatusText = IsDryRun ? "Пробный прогон..." : "Удаляю...";

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await Task.Run(() => _deletion.Delete(paths, options, progress, token), token);

            _recordResult(result);

            var recycleNote = options.UseRecycleBin && !options.DryRun
                ? " Файлы в Корзине: место освободится после её очистки."
                : "";
            StatusText = IsDryRun
                ? $"Пробный прогон завершён. Освободилось бы: {result.TotalFreedDisplay}."
                : $"Готово. Освобождено: {result.TotalFreedDisplay}.{recycleNote}";

            ShowResults?.Invoke(result);

            if (!IsDryRun)
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
            IsBusy = false;
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

    private string BuildConfirmationMessage(IReadOnlyList<AnalysisRowViewModel> items, int blockedCount)
    {
        var total = CleanupTarget.FormatSize(items.Sum(r => r.SizeBytes));
        var how = UseRecycleBin
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

    private static string BuildSensitiveMessage(IReadOnlyList<(AnalysisRowViewModel Row, DeletionVerdict Verdict)> sensitive)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ВНИМАНИЕ: среди выбранного есть личные файлы или данные профиля браузера:");
        sb.AppendLine();
        foreach (var (row, verdict) in sensitive.Take(MaxListedInConfirmation))
            sb.AppendLine($"• {row.SizeDisplay,10}  {row.Path}\n    ({verdict.SensitivityNote})");
        if (sensitive.Count > MaxListedInConfirmation)
            sb.AppendLine($"… и ещё {sensitive.Count - MaxListedInConfirmation}");
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
    public void Shutdown() => _cts?.Cancel();

    private void RefreshCommands()
    {
        (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CopyReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
