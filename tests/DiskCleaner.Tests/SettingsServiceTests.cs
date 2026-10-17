using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>Загрузка и сохранение настроек, в том числе старый файл без списка «Мои проекты».</summary>
public sealed class SettingsServiceTests
{
    [Fact]
    public void Old_settings_file_without_ProjectFolders_loads_with_an_empty_list_and_keeps_other_values()
    {
        using var dir = new TempTestDirectory();
        var file = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(file, """
            {
              "SelectedRuleNames": ["Temp пользователя", "Кэш Chrome"],
              "DryRunEnabled": false,
              "LastSelectedDrives": ["C:\\", "D:\\"],
              "ExcludedPaths": ["D:\\Keep"]
            }
            """);

        var settings = new SettingsService(file).Load();

        Assert.NotNull(settings.ProjectFolders);
        Assert.Empty(settings.ProjectFolders);
        Assert.Equal(new[] { "Temp пользователя", "Кэш Chrome" }, settings.SelectedRuleNames.ToArray());
        Assert.False(settings.DryRunEnabled);
        Assert.Equal(new[] { "C:\\", "D:\\" }, settings.LastSelectedDrives.ToArray());
        Assert.Equal(new[] { "D:\\Keep" }, settings.ExcludedPaths.ToArray());
    }

    [Fact]
    public void Project_folders_survive_a_save_and_load_round_trip()
    {
        using var dir = new TempTestDirectory();
        var service = new SettingsService(Path.Combine(dir.Path, "settings.json"));
        var settings = new AppSettings { DryRunEnabled = false };
        settings.ProjectFolders.Add(@"D:\Projects\App");
        settings.ProjectFolders.Add(@"E:\Work");
        settings.ExcludedPaths.Add(@"D:\Keep");

        var saved = service.Save(settings);
        var loaded = service.Load();

        Assert.True(saved);
        Assert.Equal(new[] { @"D:\Projects\App", @"E:\Work" }, loaded.ProjectFolders.ToArray());
        Assert.Equal(new[] { @"D:\Keep" }, loaded.ExcludedPaths.ToArray());
        Assert.False(loaded.DryRunEnabled);
    }

    [Fact]
    public void Missing_file_gives_default_settings()
    {
        using var dir = new TempTestDirectory();

        var settings = new SettingsService(Path.Combine(dir.Path, "нет.json")).Load();

        Assert.Empty(settings.ProjectFolders);
        Assert.Empty(settings.ExcludedPaths);
        Assert.True(settings.DryRunEnabled);
    }

    [Fact]
    public void Broken_json_gives_default_settings_instead_of_an_exception()
    {
        using var dir = new TempTestDirectory();
        var file = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(file, "{ это не json");

        var settings = new SettingsService(file).Load();

        Assert.Empty(settings.ProjectFolders);
        Assert.True(settings.DryRunEnabled);
    }

    [Fact]
    public void Save_creates_the_missing_settings_folder()
    {
        using var dir = new TempTestDirectory();
        var file = Path.Combine(dir.Path, "новая", "вложенная", "settings.json");

        var saved = new SettingsService(file).Save(new AppSettings());

        Assert.True(saved);
        Assert.True(File.Exists(file));
    }
}
