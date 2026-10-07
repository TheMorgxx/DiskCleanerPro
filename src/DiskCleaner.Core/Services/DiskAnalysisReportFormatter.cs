using System.Text;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Превращает <see cref="DiskAnalysisResult"/> в текстовый отчёт того же вида, что делал disk_scan.py —
/// для кнопки «Скопировать отчёт» / «Сохранить отчёт».
/// </summary>
public static class DiskAnalysisReportFormatter
{
    private const string Thick = "======================================================================";
    private const string Thin = "----------------------------------------------------------------------";

    public static string Format(DiskAnalysisResult r)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Thick);
        sb.AppendLine($"ОТЧЁТ ПО ДИСКУ {r.RootPath}");
        sb.AppendLine(Thick);
        sb.AppendLine($"Система: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"Права администратора: {(r.IsAdministrator ? "да" : "НЕТ (отчёт неполный)")}");
        if (r.DiskTotalBytes is { } total && r.DiskFreeBytes is { } free && total > 0)
        {
            sb.AppendLine($"Диск: всего {CleanupTarget.FormatSize(total)}, занято {CleanupTarget.FormatSize(total - free)}, " +
                          $"свободно {CleanupTarget.FormatSize(free)} ({free * 100.0 / total:F1}% свободно)");
        }
        sb.AppendLine($"Просканировано: {r.FilesScanned:N0} файлов, {r.DirectoriesScanned:N0} папок за {r.Elapsed.TotalSeconds:F0} сек");
        sb.AppendLine($"Суммарно прочитано: {CleanupTarget.FormatSize(r.ScannedBytes)}");
        sb.AppendLine($"Пропущено ссылок/junction: {r.ReparsePointsSkippedCount:N0} | Ошибок доступа: {r.AccessDeniedCount:N0} | Прочих ошибок: {r.IoErrorCount:N0}");
        if (r.WasCancelled)
            sb.AppendLine("СКАНИРОВАНИЕ ПРЕРВАНО — данные частичные.");
        sb.AppendLine();

        Section(sb, "1) ПАПКИ ВЕРХНЕГО УРОВНЯ (по размеру)");
        foreach (var item in r.TopLevelFolders)
            sb.AppendLine($"{item.SizeDisplay,12}  {item.Path}");
        if (r.RootFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Файлы верхнего уровня:");
            foreach (var item in r.RootFiles)
                sb.AppendLine($"{item.SizeDisplay,12}  {item.Path}");
        }
        sb.AppendLine();

        Section(sb, "2) ИЗВЕСТНЫЕ МЕСТА (кандидаты на очистку)");
        if (r.KnownPlaces.Count == 0)
            sb.AppendLine("(ничего крупного не найдено)");
        foreach (var place in r.KnownPlaces)
        {
            sb.AppendLine($"{place.SizeDisplay,12}  {place.Label}" + (place.Hint is null ? "" : $" [{place.Hint}]"));
            sb.AppendLine($"{"",12}  {place.Path}");
        }
        sb.AppendLine();

        Section(sb, $"3) ТОП-{r.HeaviestDirectories.Count} ТЯЖЁЛЫХ ПАПОК");
        foreach (var item in r.HeaviestDirectories)
            sb.AppendLine($"{item.SizeDisplay,12}  {item.Path}");
        sb.AppendLine();

        Section(sb, $"4) ТОП-{r.HeaviestFiles.Count} ТЯЖЁЛЫХ ФАЙЛОВ");
        if (r.HeaviestFiles.Count == 0)
            sb.AppendLine("(нет файлов такого размера)");
        foreach (var item in r.HeaviestFiles)
            sb.AppendLine($"{item.SizeDisplay,12}  {item.Path}");
        sb.AppendLine();

        Section(sb, "5) ПАПКИ-ПОЖИРАТЕЛИ (node_modules, .gradle, Unity Library и т.п.)");
        if (r.SuspectGroups.Count == 0)
            sb.AppendLine("(не найдено)");
        foreach (var group in r.SuspectGroups)
        {
            sb.AppendLine($"[{group.Name}] всего {group.TotalDisplay}, найдено {group.Items.Count} шт:");
            foreach (var item in group.Items.Take(8))
                sb.AppendLine($"{item.SizeDisplay,12}  {item.Path}");
        }
        sb.AppendLine();

        Section(sb, "6) ТОП РАСШИРЕНИЙ ПО СУММАРНОМУ РАЗМЕРУ");
        foreach (var ext in r.TopExtensions)
            sb.AppendLine($"{ext.SizeDisplay,12}  {ext.Extension}");
        sb.AppendLine();

        if (r.SkippedSamples.Count > 0)
        {
            Section(sb, "7) ПРИМЕРЫ ПРОПУЩЕННОГО");
            foreach (var skipped in r.SkippedSamples.Take(15))
                sb.AppendLine(skipped.ToString());
        }

        return sb.ToString();

        static void Section(StringBuilder b, string title)
        {
            b.AppendLine(Thin);
            b.AppendLine(title);
            b.AppendLine(Thin);
        }
    }
}
