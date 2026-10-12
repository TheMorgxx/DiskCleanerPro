using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;

namespace DiskCleaner.App.ViewModels;

/// <summary>
/// Окно «Firefox»: точечная очистка данных сайтов (IndexedDB, Cache API, localStorage, файлы сайта).
/// Куки, пароли, история и закладки не затрагиваются — см. <see cref="FirefoxSiteCleanupService"/>.
/// </summary>
public sealed class FirefoxCleanupViewModel : ObservableObject
{
    private readonly FirefoxProfileLocator _locator;
    private readonly FirefoxSiteStorageAnalyzer _analyzer;
    private readonly FirefoxSiteCleanupService _cleanup;
    private readonly Action<CleanupResult> _recordResult;
    private readonly Func<bool> _isFirefoxRunning;
    private readonly FirefoxAnalysisOptions? _analysisOptions;

    private CancellationTokenSource? _cts;

    public FirefoxCleanupViewModel(
        FirefoxProfileLocator locator,
        FirefoxSiteStorageAnalyzer analyzer,
        FirefoxSiteCleanupService cleanup,
        Action<CleanupResult> recordResult,
        Func<bool>? isFirefoxRunning = null,
        FirefoxAnalysisOptions? analysisOptions = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _recordResult = recordResult ?? throw new ArgumentNullException(nameof(recordResult));
        _isFirefoxRunning = isFirefoxRunning ?? DefaultIsFirefoxRunning;
        _analysisOptions = analysisOptions;

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && SelectedProfile is not null);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy && HasSelection);
        SelectSuggestedCommand = new RelayCommand(SelectSuggested);
        ClearSelectionCommand = new RelayCommand(ClearSelection);

        LoadProfiles();
    }

    public ObservableCollection<FirefoxProfile> Profiles { get; } = [];
    public ObservableCollection<FirefoxPartRowViewModel> Rows { get; } = [];

    // --- Колбэки из окна (code-behind) ---
    public Func<string, bool>? ConfirmDeletion { get; set; }
    public Action<CleanupResult>? ShowResults { get; set; }

    // --- Команды ---
    public ICommand AnalyzeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand SelectSuggestedCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    // --- Состояние ---
    private FirefoxProfile? _selectedProfile;
    public FirefoxProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
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

    private string _statusText = "Выберите профиль и нажмите «Анализ».";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private string _summaryText = "";
    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    private string _firefoxStateText = "";
    public string FirefoxStateText
    {
        get => _firefoxStateText;
        private set => SetField(ref _firefoxStateText, value);
    }

    private bool _isFirefoxRunningNow;
    public bool IsFirefoxRunningNow
    {
        get => _isFirefoxRunningNow;
        private set => SetField(ref _isFirefoxRunningNow, value);
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

    public bool HasSelection => Rows.Any(r => r.IsSelected);

    // ------------------------------------------------------------------ профили и анализ

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _locator.FindProfiles())
            Profiles.Add(profile);

        SelectedProfile = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();
        RefreshFirefoxState();

        if (Profiles.Count == 0)
            StatusText = "Профили Firefox не найдены (проверяется %APPDATA%\\Mozilla\\Firefox).";
    }

    private async Task AnalyzeAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        ClearRows();
        RefreshFirefoxState();
        StatusText = $"Анализирую данные сайтов в профиле «{profile.Name}» (только чтение)...";

        try
        {
            var report = await Task.Run(() => _analyzer.Analyze(profile, _analysisOptions, token), token);

            var rows = report.Sites
                .SelectMany(site => site.Parts.Select(part => new FirefoxPartRowViewModel(site, part)))
                .ToList();
            foreach (var row in rows)
            {
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

            var hidden = report.HiddenSmallSitesCount > 0
                ? $" Скрыто мелких сайтов: {report.HiddenSmallSitesCount} ({CleanupTarget.FormatSize(report.HiddenSmallSitesBytes)})."
                : "";
            SummaryText = $"Данных сайтов всего: {CleanupTarget.FormatSize(report.TotalBytes)}, показано сайтов: {report.Sites.Count}.{hidden}";
            StatusText = report.WasCancelled
                ? "Анализ отменён — данные частичные."
                : report.Sites.Count == 0
                    ? "Крупных данных сайтов в этом профиле не найдено."
                    : "Готово. Отметьте, что удалить, или нажмите «Выбрать рекомендуемые».";
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
            IsBusy = false;
            _cts = null;
        }
    }

    private void ClearRows()
    {
        foreach (var row in Rows)
            row.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();
        SummaryText = "";
        RecalculateSelection();
    }

    // ------------------------------------------------------------------ выбор

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirefoxPartRowViewModel.IsSelected))
            RecalculateSelection();
    }

    private void RecalculateSelection()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        SelectedSizeText = selected.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {CleanupTarget.FormatSize(selected.Sum(r => r.SizeBytes))} ({selected.Count} шт.)";
        OnPropertyChanged(nameof(HasSelection));
        RefreshCommands();
    }

    private void SelectSuggested()
    {
        foreach (var row in Rows)
            row.IsSelected = row.IsSuggested;
    }

    private void ClearSelection()
    {
        foreach (var row in Rows)
            row.IsSelected = false;
    }

    // ------------------------------------------------------------------ очистка

    private async Task CleanAsync()
    {
        var selected = Rows.Where(r => r.IsSelected && r.CanSelect).ToList();
        if (selected.Count == 0)
            return;

        RefreshFirefoxState();

        if (!IsDryRun)
        {
            if (IsFirefoxRunningNow)
            {
                StatusText = "Firefox запущен. Закройте его полностью (включая фоновые процессы) и повторите.";
                return;
            }

            if (!(ConfirmDeletion?.Invoke(BuildConfirmationMessage(selected)) ?? false))
            {
                StatusText = "Удаление отменено пользователем.";
                return;
            }

            // Данные высокого риска (localStorage, файлы сайта) — отдельное, второе подтверждение.
            var highRisk = selected.Where(r => r.Risk == FirefoxStorageRisk.High).ToList();
            if (highRisk.Count > 0 && !(ConfirmDeletion?.Invoke(BuildHighRiskMessage(highRisk)) ?? false))
            {
                StatusText = "Удаление отменено пользователем.";
                return;
            }
        }

        var selections = selected
            .GroupBy(r => r.Site.OriginPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FirefoxSiteSelection(g.First().Site, g.Select(r => r.Part).ToList()))
            .ToList();

        // Confirmed = true: здесь подтверждение уже получено в диалогах выше (в пробном прогоне ничего не удаляется).
        var options = new FirefoxCleanupOptions
        {
            DryRun = IsDryRun,
            UseRecycleBin = UseRecycleBin,
            Confirmed = true,
        };

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        StatusText = IsDryRun ? "Пробный прогон..." : "Удаляю выбранные данные сайтов...";

        var reanalyze = false;
        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await Task.Run(() => _cleanup.Clean(selections, options, progress, token), token);

            _recordResult(result);

            var recycleNote = options.UseRecycleBin && !options.DryRun
                ? " Данные в Корзине: место освободится после её очистки."
                : "";
            StatusText = IsDryRun
                ? $"Пробный прогон завершён. Освободилось бы: {result.TotalFreedDisplay}."
                : result.AllErrors.Count > 0
                    ? $"Завершено с замечаниями. Освобождено: {result.TotalFreedDisplay}. Подробности в окне результатов.{recycleNote}"
                    : $"Готово. Освобождено: {result.TotalFreedDisplay}.{recycleNote}";

            ShowResults?.Invoke(result);
            reanalyze = !IsDryRun;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Операция отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка очистки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }

        // Обновляем список после реального удаления — уже вне блока busy, чтобы команда анализа была доступна.
        if (reanalyze)
            await AnalyzeAsync();
    }

    private string BuildConfirmationMessage(IReadOnlyList<FirefoxPartRowViewModel> selected)
    {
        var how = UseRecycleBin
            ? "будут отправлены в Корзину (место освободится только после её очистки)"
            : "будут удалены БЕЗВОЗВРАТНО";

        var sb = new StringBuilder();
        sb.AppendLine($"Данные сайтов Firefox (профиль «{SelectedProfile?.Name}»), ≈{CleanupTarget.FormatSize(selected.Sum(r => r.SizeBytes))}, {how}:");
        sb.AppendLine();
        foreach (var site in selected.GroupBy(r => r.Site.OriginPath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = string.Join(", ", site.Select(r => $"{r.KindTitle} {r.SizeDisplay}"));
            sb.AppendLine($"• {site.First().SiteName}: {parts}");
        }

        sb.AppendLine();
        sb.AppendLine("Куки, пароли, история и закладки НЕ затрагиваются.");
        sb.AppendLine("Часть сайтов хранит состояние входа в IndexedDB/localStorage — на них вас могут попросить войти заново.");
        sb.AppendLine().Append("Продолжить?");
        return sb.ToString();
    }

    private static string BuildHighRiskMessage(IReadOnlyList<FirefoxPartRowViewModel> highRisk)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ВНИМАНИЕ: выбраны данные ВЫСОКОГО риска:");
        sb.AppendLine();
        foreach (var row in highRisk)
            sb.AppendLine($"• {row.SiteName} — {row.KindTitle} ({row.SizeDisplay})\n    {row.Note}");
        sb.AppendLine().Append("Удалить их тоже?");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ служебное

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Отмена... завершаю текущий шаг.";
    }

    /// <summary>Вызывается при закрытии окна: останавливает фоновую операцию.</summary>
    public void Shutdown() => _cts?.Cancel();

    private void RefreshFirefoxState()
    {
        bool running;
        try
        {
            running = _isFirefoxRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            running = true; // не смогли проверить — считаем запущенным
        }

        IsFirefoxRunningNow = running;
        FirefoxStateText = running
            ? "Firefox запущен — для удаления его нужно закрыть (анализ и пробный прогон работают и так)."
            : "Firefox закрыт — можно удалять.";
    }

    private void RefreshCommands()
    {
        (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CleanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static bool DefaultIsFirefoxRunning()
        => Process.GetProcessesByName("firefox").Length > 0;
}
