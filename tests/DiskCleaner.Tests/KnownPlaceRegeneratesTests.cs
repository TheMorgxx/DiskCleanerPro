using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Таблица «известное место → Regenerates» из DiskAnalyzer.DefaultKnownPlaces. Место помечено как
/// пересоздаваемое только там, где программа или система создаёт содержимое заново; остальное в окне
/// подписывается нейтрально, потому что внутри могут лежать данные, которые не восстановить.
/// </summary>
[Collection("Environment variables")]
public sealed class KnownPlaceRegeneratesTests
{
    // Список строится из переменных окружения процесса. Подменяем их на время вызова и возвращаем обратно.
    private static readonly object EnvLock = new();

    private static readonly string[] EnvNames = { "SystemDrive", "WINDIR", "LOCALAPPDATA", "APPDATA", "USERPROFILE" };

    private static IReadOnlyList<KnownPlaceDefinition> LoadPlaces()
    {
        lock (EnvLock)
        {
            var saved = EnvNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "KnownPlacesTest");
                Environment.SetEnvironmentVariable("SystemDrive", "Z:");
                Environment.SetEnvironmentVariable("WINDIR", Path.Combine(root, "Windows"));
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "Local"));
                Environment.SetEnvironmentVariable("APPDATA", Path.Combine(root, "Roaming"));
                Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(root, "Profile"));

                return DiskAnalyzer.DefaultKnownPlaces();
            }
            finally
            {
                foreach (var pair in saved)
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static KnownPlaceDefinition Place(string label)
    {
        var matches = LoadPlaces().Where(p => p.Label == label).ToList();
        Assert.Single(matches);
        return matches[0];
    }

    [Fact]
    public void List_has_all_27_places_with_unique_labels()
    {
        var places = LoadPlaces();

        Assert.Equal(27, places.Count);
        Assert.Equal(27, places.Select(p => p.Label).Distinct().Count());
    }

    [Theory]
    [InlineData("Кэш Windows Update")]
    [InlineData("Windows Temp")]
    [InlineData("Windows Logs")]
    [InlineData("Windows Minidump")]
    [InlineData("Windows LiveKernelReports")]
    [InlineData("Temp пользователя")]
    [InlineData("CrashDumps")]
    [InlineData("D3DSCache (шейдеры DirectX)")]
    [InlineData("NVIDIA (DXCache/GLCache)")]
    [InlineData("Кэш Chrome")]
    [InlineData("Кэш Edge")]
    [InlineData("pip cache")]
    [InlineData("npm cache")]
    [InlineData("NuGet packages")]
    public void Caches_temp_files_and_logs_are_marked_as_regenerating(string label)
    {
        Assert.True(Place(label).Regenerates, label);
    }

    [Theory]
    [InlineData("Windows.old (старая версия Windows)")]
    [InlineData("WinSxS")]
    [InlineData("Windows Installer")]
    [InlineData("Discord")]
    [InlineData("Steam (Local)")]
    [InlineData("Gradle cache")]
    [InlineData("Maven repo")]
    [InlineData("Docker Desktop")]
    [InlineData("Local\\Packages (приложения Store, WSL)")]
    [InlineData("Загрузки")]
    [InlineData("Рабочий стол")]
    [InlineData("ProgramData")]
    [InlineData("Корзина")]
    public void Places_that_may_hold_unrecoverable_data_are_not_marked_as_regenerating(string label)
    {
        Assert.False(Place(label).Regenerates, label);
    }

    [Fact]
    public void Every_place_is_covered_by_exactly_one_of_the_two_tables()
    {
        var regenerating = LoadPlaces().Count(p => p.Regenerates);
        var neutral = LoadPlaces().Count(p => !p.Regenerates);

        Assert.Equal(14, regenerating);
        Assert.Equal(13, neutral);
    }

    [Fact]
    public void Winsxs_and_installer_carry_the_do_not_touch_hint_and_nuget_warns_about_network()
    {
        Assert.Equal("НЕ трогать вручную", Place("WinSxS").Hint);
        Assert.Equal("НЕ трогать вручную", Place("Windows Installer").Hint);
        Assert.Equal("скачивается заново, нужна сеть", Place("NuGet packages").Hint);
    }

    [Fact]
    public void Places_without_a_special_warning_have_no_hint()
    {
        var withHint = LoadPlaces().Where(p => p.Hint is not null).Select(p => p.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "NuGet packages", "WinSxS", "Windows Installer" }, withHint);
    }

    [Fact]
    public void Places_resolve_under_the_substituted_environment_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "KnownPlacesTest");

        Assert.Equal(Path.Combine(root, "Windows", "Temp"), Place("Windows Temp").Path);
        Assert.Equal(Path.Combine(root, "Local", "Temp"), Place("Temp пользователя").Path);
        Assert.Equal(Path.Combine(root, "Profile", ".nuget", "packages"), Place("NuGet packages").Path);
        Assert.Equal(Path.Combine(root, "Roaming", "discord"), Place("Discord").Path);
    }
}
