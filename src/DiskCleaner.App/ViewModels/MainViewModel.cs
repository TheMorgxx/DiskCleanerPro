using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using DiskCleaner.App.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;

namespace DiskCleaner.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DriveScanner _driveScanner;
    private readonly CleanupRuleProvider _ruleProvider;
    private readonly CleanupService _cleanupService;
    private readonly Logger _logger;
    private readonly SettingsService _settingsService;

    // Для окон «Анализ диска» и «Firefox» (создаются из этих же зависимостей, см. Create...ViewModel).
    private readonly ReparsePointGuard _reparseGuard;
    private readonly SizeCalculator _sizeCalculator;
    private readonly ManualDeletionService _manualDeletion;
    private readonly DiskAnalyzer _diskAnalyzer;
    private readonly PersonalContentIndex _personalIndex = new();
    private volatile string[] _excludedSnapshot = [];
    private volatile string[] _projectSnapshot = [];

    private CancellationTokenSource? _currentOperationCts;

    public MainViewModel()
    {
        var reparseGuard = new ReparsePointGuard();
        var settings = new SettingsService().Load();
        ExcludedPaths = new ObservableCollection<string>(settings.ExcludedPaths);
        ProjectFolders = new ObservableCollection<string>(settings.ProjectFolders);

        var safety = new PathSafetyService(reparseGuard, () => _excludedSnapshot);
        var sizeCalculator = new SizeCalculator(reparseGuard);

        _driveScanner = new DriveScanner(new DiskUsageScanner());
        _ruleProvider = new CleanupRuleProvider(safety, sizeCalculator, reparseGuard);
        _cleanupService = new CleanupService(safety, reparseGuard, sizeCalculator);
        _logger = new Logger();
        _settingsService = new SettingsService();

        _reparseGuard = reparseGuard;
        _sizeCalculator = sizeCalculator;

        // Снимок списка исключений для фоновых потоков: PathSafetyService (сканирование и очистка) и ручное удаление
        // читают его при каждой проверке, поэтому изменения в «Настройках» действуют сразу, без перезапуска и без
        // обращения к ObservableCollection из чужого потока.
        _excludedSnapshot = ExcludedPaths.ToArray();
        ExcludedPaths.CollectionChanged += (_, _) => _excludedSnapshot = ExcludedPaths.ToArray();
        _projectSnapshot = ProjectFolders.ToArray();
        ProjectFolders.CollectionChanged += (_, _) => _projectSnapshot = ProjectFolders.ToArray();
        _manualDeletion = new ManualDeletionService(reparseGuard, ManualDeletionPaths.FromSystem(),
            () => _excludedSnapshot, projectFoldersProvider: () => _projectSnapshot, personalIndex: _personalIndex);
        _diskAnalyzer = new DiskAnalyzer(reparseGuard);

        _isDryRun = settings.DryRunEnabled;
        _lastSelectedRuleNames = settings.SelectedRuleNames.ToHashSet();

        ScanCommand = new AsyncRelayCommand(ScanAsync);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => Targets.Any(t => t.IsSelected));
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        OpenLogsCommand = new RelayCommand(() => ShowLogsWindow?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => ShowSettingsWindow?.Invoke());
        OpenDiskAnalysisCommand = new RelayCommand(() => ShowDiskAnalysisWindow?.Invoke());
        OpenFirefoxCommand = new RelayCommand(() => ShowFirefoxWindow?.Invoke());
        OpenSkippedItemsCommand = new RelayCommand(
            () => ShowSkippedItemsWindow?.Invoke(_skippedItems),
            () => _skippedItems.Count > 0);

        TargetsView = CollectionViewSource.GetDefaultView(Targets);
        TargetsView.Filter = FilterTarget;
        Targets.CollectionChanged += (_, _) => TargetsView.Refresh();

        LoadDrives(settings.LastSelectedDrives);
    }

    // --- Коллекции для UI ---
    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<CleanupTargetViewModel> Targets { get; } = [];
    public ObservableCollection<string> ExcludedPaths { get; }
    public ObservableCollection<string> ProjectFolders { get; }
    public ObservableCollection<string> LogLines { get; } = [];

    public ICollectionView TargetsView { get; private set; } = null!;

    // --- Внешние колбэки, устанавливаются из code-behind (MainWindow) ---
    public Func<string, bool>? ConfirmDeletion { get; set; }
    public Action<CleanupResult>? ShowResults { get; set; }
    public Action? ShowSettingsWindow { get; set; }
    public Action? ShowLogsWindow { get; set; }
    public Action? ShowDiskAnalysisWindow { get; set; }
    public Action? ShowFirefoxWindow { get; set; }
    public Action<IReadOnlyList<SkippedItem>>? ShowSkippedItemsWindow { get; set; }

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenDiskAnalysisCommand { get; }
    public ICommand OpenFirefoxCommand { get; }
    public ICommand OpenSkippedItemsCommand { get; }

    private readonly HashSet<string> _lastSelectedRuleNames;

    // --- Фильтры ---
    private string _filterDrive = "Все диски";
    public string FilterDrive
    {
        get => _filterDrive;
        set { _filterDrive = value; OnPropertyChanged(); TargetsView.Refresh(); }
    }

    public ObservableCollection<string> FilterDriveOptions { get; } = ["Все диски"];

    private string _filterRisk = "Все уровни";
    public string FilterRisk
    {
        get => _filterRisk;
        set { _filterRisk = value; OnPropertyChanged(); TargetsView.Refresh(); }
    }

    public ObservableCollection<string> FilterRiskOptions { get; } = ["Все уровни", UiNames.RiskSafe, UiNames.RiskReview, UiNames.RiskManual];

    private bool FilterTarget(object obj)
    {
        if (obj is not CleanupTargetViewModel vm)
            return false;

        if (FilterDrive != "Все диски" && !string.Equals(vm.DriveLetter, FilterDrive, StringComparison.OrdinalIgnoreCase))
            return false;

        if (FilterRisk != "Все уровни" && vm.RiskDisplay != FilterRisk)
            return false;

        return true;
    }

    // --- Состояние ---
    private bool _isDryRun;
    public bool IsDryRun
    {
        get => _isDryRun;
        set { _isDryRun = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _statusText = "Выберите диски и нажмите «Сканировать».";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // Пропущенные элементы последнего сканирования (включая частичный результат при отмене).
    // При новом сканировании список сбрасывается.
    private IReadOnlyList<SkippedItem> _skippedItems = [];
    public string SkippedButtonText => $"Пропущенные элементы ({_skippedItems.Count})";

    private void SetSkippedItems(IReadOnlyList<SkippedItem> items)
    {
        _skippedItems = items;
        OnPropertyChanged(nameof(SkippedButtonText));
        (OpenSkippedItemsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private string _totalSelectedSizeText = "0 Б";
    public string TotalSelectedSizeText
    {
        get => _totalSelectedSizeText;
        set { _totalSelectedSizeText = value; OnPropertyChanged(); }
    }

    private string _totalReclaimableText = "0 Б";
    public string TotalReclaimableText
    {
        get => _totalReclaimableText;
        set { _totalReclaimableText = value; OnPropertyChanged(); }
    }

    // --- Загрузка списка дисков ---
    private void LoadDrives(List<string> lastSelected)
    {
        Drives.Clear();
        FilterDriveOptions.Clear();
        FilterDriveOptions.Add("Все диски");

        var drives = _driveScanner.GetAvailableDrives();
        foreach (var drive in drives)
        {
            var initiallySelected = lastSelected.Count == 0
                ? drive.IsSystemDrive
                : lastSelected.Any(d => string.Equals(d.TrimEnd('\\'), drive.RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

            Drives.Add(new DriveViewModel(drive, initiallySelected));
            FilterDriveOptions.Add(drive.Letter);
        }
    }

    // --- Сканирование ---
    private async Task ScanAsync()
    {
        var selectedDrives = Drives.Where(d => d.IsSelected).Select(d => d.RootPath).ToList();
        if (selectedDrives.Count == 0)
        {
            StatusText = "Выберите хотя бы один диск для сканирования.";
            return;
        }

        _currentOperationCts = new CancellationTokenSource();
        IsBusy = true;
        Targets.Clear();
        SetSkippedItems([]);
        StatusText = $"Сканирование дисков: {string.Join(", ", selectedDrives)}...";
        _logger.WriteScanStart(selectedDrives);

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var rules = _ruleProvider.GetBuiltInRules();

            var scanResult = await Task.Run(
                () => _ruleProvider.ResolveTargets(rules, selectedDrives, progress, _currentOperationCts.Token),
                _currentOperationCts.Token);

            SetSkippedItems(scanResult.SkippedItems.ToList());

            foreach (var target in scanResult.Targets.OrderByDescending(t => t.SizeBytes))
            {
                var vm = new CleanupTargetViewModel(target);
                // Сохранённый выбор восстанавливается только для Safe. Review и Manual при каждом скане
                // снова стартуют без галочки: пользователь должен сам просмотреть и выбрать их.
                if (target.RiskLevel == CleanupRiskLevel.Safe && _lastSelectedRuleNames.Contains(target.RuleName))
                    vm.IsSelected = vm.CanSelect;
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CleanupTargetViewModel.IsSelected))
                        RecalculateSizes();
                };
                Targets.Add(vm);
            }

            _logger.WriteScanEnd(scanResult);
            RecalculateSizes();

            StatusText = scanResult.WasCancelled
                ? $"Сканирование отменено. Найдено до отмены: {Targets.Count}."
                : $"Сканирование завершено. Найдено категорий: {Targets.Count}. Пропущено элементов: {scanResult.SkippedItems.Count}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка сканирования: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _currentOperationCts = null;
            SaveCurrentSettings();
        }
    }

    // --- Очистка ---
    private async Task CleanAsync()
    {
        var selected = Targets.Where(t => t.IsSelected && t.CanSelect).Select(t => t.Model).ToList();
        if (selected.Count == 0)
            return;

        if (!IsDryRun)
        {
            var hasReviewOrManual = selected.Any(t => t.RiskLevel != CleanupRiskLevel.Safe);
            var totalSize = CleanupTarget.FormatSize(selected.Sum(t => t.SizeBytes));
            var list = string.Join("\n", selected.Select(t => $"• [{UiNames.Risk(t.RiskLevel)}] {t.DisplayName} ({t.DriveLetter}:) — {t.SizeDisplay}"));
            var warning = hasReviewOrManual
                ? $"\n\nВНИМАНИЕ: выбраны категории уровня «{UiNames.RiskReview}» или «{UiNames.RiskManual}» — они найдены эвристически или требуют повышенного внимания.\n"
                : "\n";
            var message = $"Будет удалено содержимое {selected.Count} категорий (≈{totalSize}):{warning}\n{list}\n\nПродолжить?";

            var confirmed = ConfirmDeletion?.Invoke(message) ?? false;
            if (!confirmed)
            {
                StatusText = "Удаление отменено пользователем.";
                return;
            }
        }

        _currentOperationCts = new CancellationTokenSource();
        IsBusy = true;
        var mode = IsDryRun ? "тестовый прогон" : "очистка";
        StatusText = $"Выполняю: {mode}...";

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await Task.Run(
                () => _cleanupService.Clean(selected, IsDryRun, progress, _currentOperationCts.Token),
                _currentOperationCts.Token);

            foreach (var entry in result.Entries)
            {
                _logger.WriteCleanupEntry(entry);
                LogLines.Insert(0, entry.ToString());
            }
            _logger.WriteCleanupSummary(result);

            StatusText = IsDryRun
                ? $"Тестовый прогон завершён. Было бы освобождено примерно: {result.TotalFreedDisplay}."
                : $"Очистка завершена. Освобождено: {result.TotalFreedDisplay}.";

            ShowResults?.Invoke(result);

            if (!IsDryRun)
                await ScanAsync(); // пересчитываем категории и размеры после реального удаления
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка очистки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _currentOperationCts = null;
            SaveCurrentSettings();
        }
    }

    private void CancelCurrentOperation()
    {
        _currentOperationCts?.Cancel();
        StatusText = "Отмена... завершаю текущий шаг.";
    }

    private void RecalculateSizes()
    {
        var selectedTotal = Targets.Where(t => t.IsSelected).Sum(t => t.SizeBytes);
        var reclaimableTotal = Targets.Where(t => t.CanSelect).Sum(t => t.SizeBytes);
        TotalSelectedSizeText = CleanupTarget.FormatSize(selectedTotal);
        TotalReclaimableText = CleanupTarget.FormatSize(reclaimableTotal);
        (CleanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public string LogDirectory => _logger.LogDirectory;

    /// <summary>Окно «Анализ диска»: анализатор + ручное удаление на тех же зависимостях и с тем же списком исключений.</summary>
    public DiskAnalysisViewModel CreateDiskAnalysisViewModel()
    {
        var roots = Drives.Select(d => d.RootPath).ToList();
        var defaultRoot = Drives.FirstOrDefault(d => d.IsSelected)?.RootPath
                          ?? Drives.FirstOrDefault(d => d.IsSystemDrive)?.RootPath
                          ?? roots.FirstOrDefault()
                          ?? "C:\\";

        return new DiskAnalysisViewModel(_diskAnalyzer, _manualDeletion, roots, defaultRoot, RecordCleanupResult,
            _logger.LogDirectory, _personalIndex, new SensitiveSummaryBuilder(_reparseGuard),
            () => _excludedSnapshot, ExcludeAnalysisPath);
    }

    /// <summary>Окно «Firefox»: точечная очистка данных сайтов поверх того же ручного удаления.</summary>
    public FirefoxCleanupViewModel CreateFirefoxViewModel()
    {
        var analyzer = new FirefoxSiteStorageAnalyzer(_sizeCalculator, _reparseGuard);
        var cleanup = new FirefoxSiteCleanupService(_manualDeletion);
        return new FirefoxCleanupViewModel(new FirefoxProfileLocator(), analyzer, cleanup, RecordCleanupResult);
    }

    /// <summary>Пишет результат ручной операции в тот же журнал и список логов, что и основная очистка. Вызывать из потока UI.</summary>
    public void RecordCleanupResult(CleanupResult result)
    {
        foreach (var entry in result.Entries)
        {
            _logger.WriteCleanupEntry(entry);
            LogLines.Insert(0, entry.ToString());
        }

        _logger.WriteCleanupSummary(result);
    }

    private bool ExcludeAnalysisPath(string path)
    {
        var added = !ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (added)
            ExcludedPaths.Add(path);

        if (SaveCurrentSettings())
            return true;

        // Строки скрываются только после успешной записи; не оставляем новое несохранённое исключение.
        if (added)
            ExcludedPaths.Remove(path);
        return false;
    }

    public bool SaveCurrentSettings()
    {
        var settings = new AppSettings
        {
            DryRunEnabled = IsDryRun,
            LastSelectedDrives = Drives.Where(d => d.IsSelected).Select(d => d.RootPath).ToList(),
            SelectedRuleNames = Targets.Where(t => t.IsSelected).Select(t => t.Model.RuleName).Distinct().ToList(),
            ExcludedPaths = ExcludedPaths.ToList(),
            ProjectFolders = ProjectFolders.ToList(),
        };
        var saved = _settingsService.Save(settings);
        if (!saved)
            StatusText = "Не удалось сохранить настройки. Проверьте доступ к файлу настроек.";
        return saved;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
