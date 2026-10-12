using System.IO;
using System.Text.Json;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Сохраняет/загружает AppSettings в %LOCALAPPDATA%\DiskCleaner\settings.json.
/// Любая ошибка чтения/записи не должна ронять приложение — при сбое
/// возвращаются настройки по умолчанию.
/// </summary>
public sealed class SettingsService
{
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsService(string? overrideSettingsFilePath = null)
    {
        _settingsFilePath = overrideSettingsFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
                return new AppSettings();

            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
