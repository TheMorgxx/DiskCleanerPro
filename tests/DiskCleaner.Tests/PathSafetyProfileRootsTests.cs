using System.IO;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Равенство цели с профильными корнями. Корни подставляются через PathSafetyRoots во временной папке,
/// поэтому результат не зависит от настоящего профиля и ОС: AppData, .gradle, .nuget, .android целиком — нельзя,
/// их вложенные папки — можно, Temp целиком — можно.
/// </summary>
public sealed class PathSafetyProfileRootsTests : IDisposable
{
    private readonly TempTestDirectory _dir = new();
    private readonly string _profile;
    private readonly string _local;
    private readonly string _roaming;
    private readonly string _temp;
    private readonly PathSafetyRoots _roots;
    private readonly PathSafetyService _service;

    public PathSafetyProfileRootsTests()
    {
        _profile = Make("Users", "bob");
        _local = Make("Users", "bob", "AppData", "Local");
        _roaming = Make("Users", "bob", "AppData", "Roaming");
        _temp = Make("Users", "bob", "AppData", "Local", "Temp");
        Make("Users", "bob", ".gradle");
        Make("Users", "bob", ".nuget");
        Make("Users", "bob", ".android");
        var windows = Make("Windows");

        _roots = new PathSafetyRoots(_profile, _local, _roaming, _temp, windows);
        _service = new PathSafetyService(new ReparsePointGuard(), Array.Empty<string>(), _roots);
    }

    public void Dispose() => _dir.Dispose();

    private string Make(params string[] parts)
    {
        var path = Path.Combine(new[] { _dir.Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string P(params string[] parts) => Path.Combine(new[] { _profile }.Concat(parts).ToArray());

    // ------------------------------------------------------------------ сами корни — запрещены

    [Fact]
    public void Local_app_data_root_itself_is_rejected_with_a_profile_root_reason()
    {
        var safe = _service.IsPathSafeForContentDeletion(_local, out var reason);

        Assert.False(safe);
        Assert.Contains("корень профильной папки", reason!);
    }

    [Fact]
    public void Roaming_app_data_root_itself_is_rejected_with_a_profile_root_reason()
    {
        var safe = _service.IsPathSafeForContentDeletion(_roaming, out var reason);

        Assert.False(safe);
        Assert.Contains("корень профильной папки", reason!);
    }

    [Theory]
    [InlineData(".gradle")]
    [InlineData(".nuget")]
    [InlineData(".android")]
    public void Dot_folders_of_the_profile_are_rejected_as_a_whole(string name)
    {
        var safe = _service.IsPathSafeForContentDeletion(P(name), out var reason);

        Assert.False(safe);
        Assert.Contains("корень профильной папки", reason!);
    }

    [Fact]
    public void Profile_roots_are_rejected_with_trailing_separator_and_dot_dot()
    {
        var withSeparator = _local + Path.DirectorySeparatorChar;
        var withDotDot = Path.Combine(_local, "Temp", "..");

        foreach (var candidate in new[] { withSeparator, withDotDot })
        {
            var safe = _service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe, candidate);
            Assert.Contains("корень профильной папки", reason!);
        }
    }

    [Fact]
    public void Profile_root_checks_are_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var nested = Make("Users", "bob", "AppData", "Local", "pip", "Cache");

        foreach (var candidate in new[] { _local.ToUpperInvariant(), _local.ToLowerInvariant() })
        {
            var safe = _service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe, candidate);
            Assert.Contains("корень профильной папки", reason!);
        }

        foreach (var candidate in new[] { nested.ToUpperInvariant(), nested.ToLowerInvariant(),
                     _temp.ToUpperInvariant(), _temp.ToLowerInvariant() })
        {
            var safe = _service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.True(safe, reason);
            Assert.Null(reason);
        }
    }

    // ------------------------------------------------------------------ вложенные папки — разрешены

    [Theory]
    [InlineData("AppData/Local/Microsoft/Edge/Cache")]
    [InlineData("AppData/Local/pip/Cache")]
    [InlineData("AppData/Roaming/discord/Cache")]
    [InlineData(".gradle/caches")]
    [InlineData(".nuget/packages")]
    [InlineData(".android/cache")]
    public void Folders_inside_profile_roots_are_allowed(string relative)
    {
        var parts = relative.Split('/');
        Make(new[] { "Users", "bob" }.Concat(parts).ToArray());

        var safe = _service.IsPathSafeForContentDeletion(P(parts), out var reason);

        Assert.True(safe, reason);
        Assert.Null(reason);
    }

    // ------------------------------------------------------------------ Temp — разрешён и целиком

    [Fact]
    public void Temp_root_itself_is_allowed()
    {
        var safe = _service.IsPathSafeForContentDeletion(_temp, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void Temp_root_with_trailing_separator_is_allowed()
    {
        var safe = _service.IsPathSafeForContentDeletion(_temp + Path.DirectorySeparatorChar, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void Folder_inside_temp_is_allowed()
    {
        var inner = Make("Users", "bob", "AppData", "Local", "Temp", "build-123");

        var safe = _service.IsPathSafeForContentDeletion(inner, out var reason);

        Assert.True(safe, reason);
    }

    [Fact]
    public void When_temp_is_a_separate_root_outside_app_data_it_is_still_allowed_and_not_treated_as_profile_root()
    {
        var elsewhere = Make("SeparateTemp");
        var roots = _roots with { Temp = elsewhere };
        var service = new PathSafetyService(new ReparsePointGuard(), Array.Empty<string>(), roots);

        var safe = service.IsPathSafeForContentDeletion(elsewhere, out var reason);

        Assert.True(safe, reason);
    }

    // ------------------------------------------------------------------ родители профильных корней

    [Fact]
    public void Parents_of_profile_roots_are_rejected()
    {
        var appData = Make("Users", "bob", "AppData");

        foreach (var candidate in new[] { appData, _profile, Path.GetDirectoryName(_profile)! })
        {
            var safe = _service.IsPathSafeForContentDeletion(candidate, out var reason);

            Assert.False(safe, candidate);
            Assert.NotNull(reason);
        }
    }

    [Fact]
    public void Dot_dot_cannot_be_used_to_reach_a_profile_root_through_an_allowed_folder()
    {
        var inner = Make("Users", "bob", ".gradle", "caches");
        var sneaky = Path.Combine(inner, "..");

        var safe = _service.IsPathSafeForContentDeletion(sneaky, out var reason);

        Assert.False(safe);
        Assert.Contains("корень профильной папки", reason!);
    }

    [Fact]
    public void Sibling_with_a_common_name_prefix_is_not_treated_as_a_profile_root_child()
    {
        var lookalike = Make("Users", "bob", ".gradle-other");

        var safe = _service.IsPathSafeForContentDeletion(lookalike, out var reason);

        Assert.False(safe);
        Assert.False(reason!.Contains("корень профильной папки"), reason);
    }
}
