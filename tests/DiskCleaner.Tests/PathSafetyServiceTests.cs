using System.IO;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class PathSafetyServiceTests
{
    private static PathSafetyService CreateService(IEnumerable<string>? excluded = null) =>
        new(new ReparsePointGuard(), excluded);

    [Fact]
    public void Allows_folder_under_temp()
    {
        using var temp = new TempTestDirectory(); // сам находится под %TEMP%
        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(temp.Path, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void Normalizes_dot_dot_and_still_allows_path_under_temp()
    {
        using var temp = new TempTestDirectory();
        var sub = temp.CreateSubDirectory("sub");
        var trickyPath = Path.Combine(temp.Path, "sub", "..", "sub");
        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(trickyPath, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void Blocks_Documents_folder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Directory.Exists(documents))
            return; // на некоторых средах CI папки может не быть — тест не применим

        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(documents, out var reason);

        Assert.False(safe);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Blocks_Desktop_folder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop))
            return;

        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(desktop, out _);

        Assert.False(safe);
    }

    [Fact]
    public void Blocks_Windows_folder_itself()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Directory.Exists(windows))
            return;

        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(windows, out var reason);

        Assert.False(safe);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Allows_WindowsTemp_as_explicit_exception()
    {
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (!Directory.Exists(windowsTemp))
            return; // может быть недоступно без прав администратора в CI

        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(windowsTemp, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void Does_not_allow_deleting_the_drive_root_itself()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(driveRoot, out var reason);

        Assert.False(safe);
        Assert.Contains("корня диска", reason);
    }

    [Fact]
    public void Rejects_arbitrary_folder_not_in_whitelist()
    {
        // Папка рядом с тестовой сборкой — не под %TEMP%/%LOCALAPPDATA%/%APPDATA%
        // и не носит имя Temp/tmp/ShaderCache, значит не должна пройти ни один whitelist.
        var candidate = Path.Combine(AppContext.BaseDirectory, "NotAllowlistedFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(candidate);
        try
        {
            var service = CreateService();
            var safe = service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe);
            Assert.NotNull(reason);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public void Allows_drive_wide_ShaderCache_leaf_name_anywhere_safe()
    {
        using var temp = new TempTestDirectory();
        var shaderCache = temp.CreateSubDirectory("SomeGame/ShaderCache");
        var service = CreateService();

        var safe = service.IsPathSafeForContentDeletion(shaderCache, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void User_excluded_path_is_blocked_even_if_otherwise_whitelisted()
    {
        using var temp = new TempTestDirectory();
        var sub = temp.CreateSubDirectory("excluded-cache");
        var service = CreateService(excluded: [sub]);

        var safe = service.IsPathSafeForContentDeletion(sub, out var reason);

        Assert.False(safe);
        Assert.Contains("исключени", reason);
    }

    [Fact]
    public void Rejects_reparse_point_target_itself()
    {
        using var temp = new TempTestDirectory();
        var real = temp.CreateSubDirectory("real-target");
        var linkPath = Path.Combine(temp.Path, "link-to-target");

        try
        {
            Directory.CreateSymbolicLink(linkPath, real);
        }
        catch (IOException)
        {
            return; // недостаточно прав для создания symlink в этой среде — тест неприменим
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var service = CreateService();
        var safe = service.IsPathSafeForContentDeletion(linkPath, out var reason);

        Assert.False(safe);
        Assert.Contains("reparse", reason, StringComparison.OrdinalIgnoreCase);
    }
}
