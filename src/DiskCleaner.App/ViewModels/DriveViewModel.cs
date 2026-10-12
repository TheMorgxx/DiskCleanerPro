using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiskCleaner.Core.Models;

namespace DiskCleaner.App.ViewModels;

public sealed class DriveViewModel : INotifyPropertyChanged
{
    public DriveInfoModel Model { get; }

    public DriveViewModel(DriveInfoModel model, bool initiallySelected)
    {
        Model = model;
        _isSelected = initiallySelected;
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

    public string RootPath => Model.RootPath;
    public string Letter => Model.Letter;
    public string? VolumeLabel => Model.VolumeLabel;
    public string TotalDisplay => Model.TotalDisplay;
    public string UsedDisplay => Model.UsedDisplay;
    public string FreeDisplay => Model.FreeDisplay;
    public string? FileSystem => Model.FileSystem;
    public string DriveTypeDisplay => Model.DriveTypeDisplay;
    public bool IsSystemDrive => Model.IsSystemDrive;
    public double UsedFraction => Model.UsedFraction;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
