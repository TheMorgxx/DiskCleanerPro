using System.IO;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class ReparsePointGuardTests
{
    [Fact]
    public void Detects_regular_directory_as_not_reparse_point()
    {
        using var temp = new TempTestDirectory();
        var guard = new ReparsePointGuard();

        Assert.False(guard.IsReparsePoint(temp.Path));
    }

    [Fact]
    public void Detects_directory_symlink_as_reparse_point()
    {
        using var temp = new TempTestDirectory();
        var real = temp.CreateSubDirectory("real");
        var link = Path.Combine(temp.Path, "link");

        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // среда без прав на создание symlink — тест неприменим
        }

        var guard = new ReparsePointGuard();

        Assert.True(guard.IsReparsePoint(link));
    }

    [Fact]
    public void Detects_file_symlink_as_reparse_point()
    {
        using var temp = new TempTestDirectory();
        var realFile = temp.CreateFile("real.txt", "hello");
        var linkFile = Path.Combine(temp.Path, "link.txt");

        try
        {
            File.CreateSymbolicLink(linkFile, realFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var guard = new ReparsePointGuard();

        Assert.True(guard.IsReparsePoint(linkFile));
    }

    [Fact]
    public void Returns_false_for_nonexistent_path_instead_of_throwing()
    {
        var guard = new ReparsePointGuard();
        var missing = Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-" + Guid.NewGuid());

        var result = guard.IsReparsePoint(missing);

        Assert.False(result);
    }

    [Fact]
    public void WouldEscapeRoot_detects_path_outside_root()
    {
        var guard = new ReparsePointGuard();
        var root = PathSafetyService.Normalize(Path.GetTempPath());
        var outside = PathSafetyService.Normalize(Path.GetPathRoot(root)!);

        Assert.True(guard.WouldEscapeRoot(root, outside));
    }

    [Fact]
    public void WouldEscapeRoot_allows_path_inside_root()
    {
        using var temp = new TempTestDirectory();
        var guard = new ReparsePointGuard();
        var root = PathSafetyService.Normalize(temp.Path);
        var inside = PathSafetyService.Normalize(temp.CreateSubDirectory("inside"));

        Assert.False(guard.WouldEscapeRoot(root, inside));
    }
}
