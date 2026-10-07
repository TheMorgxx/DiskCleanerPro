using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Пишет журнал сканирования/очистки в %LOCALAPPDATA%\DiskCleaner\logs
/// (папка самого приложения в AppData, не личные файлы пользователя).
/// Содержимое личных документов никогда не логируется — только пути,
/// размеры и счётчики.
/// </summary>
public sealed class Logger
{
    private readonly string _logDirectory;

    public Logger(string? overrideLogDirectory = null)
    {
        _logDirectory = overrideLogDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner", "logs");

        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public string CurrentLogFilePath => Path.Combine(_logDirectory, $"cleanup_{DateTime.Now:yyyy-MM-dd}.log");

    public void WriteLine(string line)
    {
        try
        {
            File.AppendAllText(CurrentLogFilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Логирование не должно ронять приложение.
        }
    }

    public void WriteScanStart(IReadOnlyCollection<string> selectedDrives) =>
        WriteLine($"=== Начало сканирования. Диски: {string.Join(", ", selectedDrives)} ===");

    public void WriteScanEnd(ScanResult result) =>
        WriteLine($"=== Сканирование завершено. Найдено целей: {result.Targets.Count}, " +
                   $"пропущено элементов: {result.SkippedItems.Count}, отменено: {result.WasCancelled} ===");

    public void WriteCleanupEntry(CleanupLogEntry entry) => WriteLine(entry.ToString());

    public void WriteCleanupSummary(CleanupResult result) =>
        WriteLine($"=== Итог очистки: освобождено {result.TotalFreedDisplay}, " +
                   $"удалено файлов: {result.TotalFilesDeleted}, пропущено: {result.TotalFilesSkipped}, " +
                   $"ошибок: {result.AllErrors.Count}, dry-run: {result.DryRun} ===");
}
