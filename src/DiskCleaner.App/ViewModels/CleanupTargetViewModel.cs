using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.ViewModels;

public sealed class CleanupTargetViewModel : INotifyPropertyChanged
{
    public CleanupTarget Model { get; }

    public CleanupTargetViewModel(CleanupTarget model)
    {
        Model = model;
        // Отмечаем галочкой по умолчанию только Safe-категории, доступные без
        // прав администратора — Review и Manual пользователь должен выбрать сам.
        _isSelected = model.RiskLevel == CleanupRiskLevel.Safe && CanSelect;

        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(Model.FullPath));
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool CanSelect => Model.IsSafeToClean && !Model.IsBlockedNoAdmin;

    public string DisplayName => Model.DisplayName;
    public string CategoryDisplay => Model.Category.ToString();
    public string? AppName => Model.AppName;
    public string DriveLetter => Model.DriveLetter;
    public string FullPath => Model.FullPath;
    public string SizeDisplay => Model.SizeDisplay;
    public long SizeBytes => Model.SizeBytes;
    public string Description => Model.Description;
    public CleanupRiskLevel RiskLevel => Model.RiskLevel;
    public string RiskDisplay => Model.RiskLevel switch
    {
        CleanupRiskLevel.Safe => "Safe",
        CleanupRiskLevel.Review => "Review",
        CleanupRiskLevel.Manual => "Manual",
        _ => Model.RiskLevel.ToString(),
    };
    public string LastModifiedDisplay => Model.LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    public string StatusText => Model switch
    {
        { IsBlockedNoAdmin: true } => "Требуются права администратора",
        { IsSafeToClean: false } => $"Заблокировано: {Model.UnsafeReason}",
        _ => $"{Model.FileCount} файлов",
    };

    public ICommand OpenFolderCommand { get; }

    private void OpenFolder()
    {
        if (!Directory.Exists(Model.FullPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Model.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Не удалось открыть проводник — не критично, просто игнорируем.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
