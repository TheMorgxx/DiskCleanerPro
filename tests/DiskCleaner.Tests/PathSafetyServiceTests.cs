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

    // ------------------------------------------------------------------ A: "Users" и профильные корни

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = PathSafetyService.Normalize(root);
        var normalizedPath = PathSafetyService.Normalize(path);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string UsersRootFor(string path) =>
        Path.Combine(Path.GetPathRoot(PathSafetyService.Normalize(path))!, "Users");

    private static bool IsInsideUsers(string path) => IsInside(UsersRootFor(path), path);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Лучшее усилие — не роняем тест из-за неудачной уборки.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Allows_folder_under_LocalAppData_although_it_is_inside_Users()
    {
        if (!OperatingSystem.IsWindows())
            return; // правило про "Users" и профиль актуально для Windows-раскладки

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData) || !Directory.Exists(localAppData) || !IsInsideUsers(localAppData))
            return; // профиль перенесён за пределы {диск}\Users — шаг 4 здесь ничего не проверяет

        var candidate = Path.Combine(localAppData, "DiskCleanerTests_" + Guid.NewGuid().ToString("N"), "Cache");
        try
        {
            Directory.CreateDirectory(candidate);
            var service = CreateService();

            var safe = service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.True(safe, reason);
        }
        finally
        {
            DeleteQuietly(Path.GetDirectoryName(candidate)!);
        }
    }

    [Fact]
    public void Allows_folder_under_existing_nuget_cache_inside_Users()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nuget = Path.Combine(userProfile, ".nuget");
        if (!Directory.Exists(nuget) || !IsInsideUsers(nuget))
            return; // .nuget нет на этой машине — не создаём чужие каталоги в профиле

        var candidate = Path.Combine(nuget, "DiskCleanerTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(candidate);
            var service = CreateService();

            var safe = service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.True(safe, reason);
        }
        finally
        {
            DeleteQuietly(candidate);
        }
    }

    [Theory]
    [InlineData("Temp")]
    [InlineData("tmp")]
    [InlineData("ShaderCache")]
    public void Blocks_cache_named_folder_inside_foreign_profile(string leafName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!IsInsideUsers(userProfile))
            return;

        // C:\Users\Public — чужой для текущего пользователя профиль, в который можно писать без прав администратора.
        var publicProfile = Path.Combine(UsersRootFor(userProfile), "Public");
        if (!Directory.Exists(publicProfile) || string.Equals(PathSafetyService.Normalize(publicProfile),
                PathSafetyService.Normalize(userProfile), StringComparison.OrdinalIgnoreCase))
            return;

        var container = Path.Combine(publicProfile, "DiskCleanerTests_" + Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(container, leafName);
        try
        {
            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch (UnauthorizedAccessException)
            {
                return; // нет прав на запись — тест неприменим
            }
            catch (IOException)
            {
                return;
            }

            var service = CreateService();
            var safe = service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe);
            Assert.Contains("защищённой системной директории", reason);
        }
        finally
        {
            DeleteQuietly(container);
        }
    }

    [Theory]
    [InlineData("Temp")]
    [InlineData("tmp")]
    [InlineData("ShaderCache")]
    public void Blocks_cache_named_folder_inside_current_user_Projects(string leafName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!IsInsideUsers(userProfile))
            return;

        var container = Path.Combine(userProfile, "DiskCleanerTests_" + Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(container, "Projects", "MyApp", leafName);

        // Если TEMP/LOCALAPPDATA/APPDATA переопределены на сам профиль, путь окажется внутри профильного корня —
        // это другой сценарий, а не «Projects».
        string[] profileRoots =
        [
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];
        if (profileRoots.Any(root => !string.IsNullOrEmpty(root) && IsInside(root, candidate)))
            return;

        try
        {
            Directory.CreateDirectory(candidate);
            var service = CreateService();

            var safe = service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe);
            Assert.Contains("защищённой системной директории", reason);
        }
        finally
        {
            DeleteQuietly(container);
        }
    }

    [Fact]
    public void Blocks_Users_root_and_current_profile_root_themselves()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(userProfile) || !IsInsideUsers(userProfile))
            return;

        var usersRoot = UsersRootFor(userProfile);
        var service = CreateService();

        Assert.False(service.IsPathSafeForContentDeletion(usersRoot, out _));
        Assert.False(service.IsPathSafeForContentDeletion(userProfile, out _));
    }

    // ------------------------------------------------------------------ B: цель, внутри которой лежит исключение

    [Fact]
    public void Blocks_target_that_contains_excluded_subfolder()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("target");
        var keep = temp.CreateSubDirectory("target/keep");
        var service = CreateService(excluded: [keep]);

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("внутри лежит путь из списка исключений", reason);
        Assert.Contains(PathSafetyService.Normalize(keep), reason);
    }

    [Fact]
    public void Blocks_target_that_contains_excluded_file()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("target");
        var keepFile = temp.CreateFile("target/sub/keep.txt");
        var service = CreateService(excluded: [keepFile]);

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("внутри лежит путь из списка исключений", reason);
    }

    [Fact]
    public void Does_not_block_target_when_excluded_sibling_only_shares_name_prefix()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var sibling = temp.CreateSubDirectory("cache-old");
        var service = CreateService(excluded: [sibling]);

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.True(safe, reason);
    }

    // ------------------------------------------------------------------ C: список исключений читается при каждой проверке

    [Fact]
    public void Exclusion_added_after_construction_takes_effect_immediately()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var excluded = new List<string>();
        var service = new PathSafetyService(new ReparsePointGuard(), () => excluded);

        Assert.True(service.IsPathSafeForContentDeletion(target, out var before), before);

        excluded.Add(target);
        var safeAfterAdd = service.IsPathSafeForContentDeletion(target, out var reasonAfterAdd);

        Assert.False(safeAfterAdd);
        Assert.Contains("исключени", reasonAfterAdd);

        excluded.Clear();
        Assert.True(service.IsPathSafeForContentDeletion(target, out var afterRemove), afterRemove);
    }

    [Fact]
    public void Inner_exclusion_added_after_construction_blocks_the_target()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var keep = temp.CreateSubDirectory("cache/keep");
        var excluded = new List<string>();
        var service = new PathSafetyService(new ReparsePointGuard(), () => excluded);

        Assert.True(service.IsPathSafeForContentDeletion(target, out var before), before);

        excluded.Add(keep);
        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("внутри лежит путь из списка исключений", reason);
    }

    [Fact]
    public void Legacy_constructor_keeps_the_snapshot_taken_at_construction()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var excluded = new List<string>();
        var service = new PathSafetyService(new ReparsePointGuard(), excluded);

        excluded.Add(target); // после создания сервиса — старый конструктор этого не видит

        Assert.True(service.IsPathSafeForContentDeletion(target, out var reason), reason);
    }

    [Fact]
    public void Invalid_paths_in_exclusions_do_not_break_the_check()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var excludedSibling = temp.CreateSubDirectory("excluded");
        var excluded = new List<string> { "bad\0path", "", "   ", excludedSibling };
        var service = new PathSafetyService(new ReparsePointGuard(), () => excluded);

        Assert.True(service.IsPathSafeForContentDeletion(target, out var reason), reason);

        // Корректное исключение из того же списка продолжает работать.
        var blocked = service.IsPathSafeForContentDeletion(excludedSibling, out var blockedReason);
        Assert.False(blocked);
        Assert.Contains("исключени", blockedReason);
    }

    [Fact]
    public void Blocks_when_provider_keeps_failing_with_InvalidOperationException()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var service = new PathSafetyService(new ReparsePointGuard(),
            () => throw new InvalidOperationException("Collection was modified"));

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("список исключений", reason);
    }

    [Fact]
    public void Retries_when_provider_fails_transiently()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var calls = 0;
        var service = new PathSafetyService(new ReparsePointGuard(), () =>
        {
            calls++;
            if (calls < 3)
                throw new InvalidOperationException("Collection was modified");
            return [target];
        });

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("добавлен пользователем", reason); // список в итоге прочитан, а не отказ из-за ошибки чтения
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Blocks_when_provider_returns_null()
    {
        using var temp = new TempTestDirectory();
        var target = temp.CreateSubDirectory("cache");
        var service = new PathSafetyService(new ReparsePointGuard(), () => null!);

        var safe = service.IsPathSafeForContentDeletion(target, out var reason);

        Assert.False(safe);
        Assert.Contains("список исключений", reason);
    }

    [Fact]
    public void Constructor_rejects_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PathSafetyService(new ReparsePointGuard(), (Func<IEnumerable<string>>)null!));
    }
}