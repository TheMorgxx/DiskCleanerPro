namespace DiskCleaner.Core.Models;

/// <summary>
/// Настройки анализатора диска. Значения по умолчанию соответствуют
/// Python-скрипту disk_scan.py, на основе которого сделан DiskAnalyzer.
/// </summary>
public sealed class DiskAnalysisOptions
{
    private const long Mb = 1024L * 1024L;

    /// <summary>Папки меньше этого размера не запоминаются (кроме корня и папок первого уровня).</summary>
    public long MinRecordedDirBytes { get; init; } = 10 * Mb;

    /// <summary>Папки от этого размера попадают в «тяжёлые папки» и в поиск папок-пожирателей.</summary>
    public long BigDirBytes { get; init; } = 50 * Mb;

    /// <summary>Файлы от этого размера попадают в «тяжёлые файлы».</summary>
    public long BigFileBytes { get; init; } = 100 * Mb;

    public int TopFilesCount { get; init; } = 40;
    public int TopDirsCount { get; init; } = 50;
    public int TopExtensionsCount { get; init; } = 15;
    public int TopLevelFoldersCount { get; init; } = 20;
    public int RootFilesCount { get; init; } = 10;

    /// <summary>Глубина считается от корня: корень = 0, C:\Users = 1, C:\Users\name = 2.</summary>
    public int MinReportedDepth { get; init; } = 2;
    public int MaxReportedDepth { get; init; } = 5;

    /// <summary>
    /// Если почти весь размер папки (см. <see cref="CollapseRatio"/>) лежит в одной её подпапке,
    /// родитель не показывается отдельной строкой — остаётся самая глубокая информативная папка.
    /// Это убирает цепочки вида Visual Studio → 18 → Community.
    /// </summary>
    public bool CollapseSingleChildChains { get; init; } = true;
    public double CollapseRatio { get; init; } = 0.9;

    /// <summary>Защита от патологически глубокой вложенности.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>Сколько примеров пропущенных элементов хранить в результате.</summary>
    public int MaxSkippedSamples { get; init; } = 200;
}
