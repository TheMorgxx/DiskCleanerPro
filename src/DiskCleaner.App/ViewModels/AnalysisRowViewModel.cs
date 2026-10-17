using System.Diagnostics;
using System.Windows.Input;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.ViewModels;

/// <summary>Строка таблицы результатов анализа диска: путь, который можно (или нельзя) удалить.</summary>
public sealed class AnalysisRowViewModel : ObservableObject
{
    private bool _isSelected;

    public AnalysisRowViewModel(
        string source,
        string title,
        string path,
        long sizeBytes,
        bool isDirectory,
        DateTime? lastModifiedUtc,
        DeletionVerdict verdict,
        string? hint)
    {
        Source = source;
        Title = title;
        Path = path;
        SizeBytes = sizeBytes;
        IsDirectory = isDirectory;
        LastModifiedUtc = lastModifiedUtc;
        Verdict = verdict;
        Hint = hint;

        ShowInExplorerCommand = new RelayCommand(ShowInExplorer);
    }

    public string Source { get; }
    public string Title { get; }
    public string Path { get; }
    public long SizeBytes { get; }
    public bool IsDirectory { get; }
    public DateTime? LastModifiedUtc { get; }
    public DeletionVerdict Verdict { get; }
    public string? Hint { get; }

    public string SizeDisplay => CleanupTarget.FormatSize(SizeBytes);
    public string LastModifiedDisplay => LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    /// <summary>Можно выбрать галочкой: путь не заблокирован правилами безопасности.</summary>
    public bool CanSelect => Verdict.IsAllowed;

    public bool RequiresConfirmation => Verdict.RequiresConfirmation;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // Заблокированный путь выбрать нельзя ни из интерфейса, ни из кода.
            var next = value && CanSelect;
            SetField(ref _isSelected, next);
        }
    }

    public string StatusText
    {
        get
        {
            if (!Verdict.IsAllowed)
                return $"Нельзя удалить: {Verdict.BlockReason}";
            if (Verdict.RequiresConfirmation)
                return $"Нужно подтверждение: {Verdict.SensitivityNote}";
            return string.IsNullOrWhiteSpace(Hint) ? "Можно удалить" : Hint!;
        }
    }

    public ICommand ShowInExplorerCommand { get; }

    private void ShowInExplorer()
    {
        try
        {
            // Для файла открываем проводник с выделением файла, для папки — саму папку.
            var arguments = IsDirectory ? $"\"{Path}\"" : $"/select,\"{Path}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Не удалось открыть проводник — не критично.
        }
    }
}
