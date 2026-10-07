using System.IO;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Находит профили Firefox через profiles.ini (а не по жёстко заданному пути): профиль может называться
/// как угодно и лежать вне стандартной папки. Если profiles.ini нет — смотрит папку Profiles.
/// Только чтение.
/// </summary>
public sealed class FirefoxProfileLocator
{
    private readonly string _firefoxBaseDirectory;

    /// <param name="firefoxBaseDirectory">
    /// Папка %APPDATA%\Mozilla\Firefox. По умолчанию берётся из системы; параметр нужен для тестов.
    /// </param>
    public FirefoxProfileLocator(string? firefoxBaseDirectory = null)
    {
        _firefoxBaseDirectory = firefoxBaseDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
    }

    public IReadOnlyList<FirefoxProfile> FindProfiles()
    {
        var profiles = new List<FirefoxProfile>();
        var iniPath = Path.Combine(_firefoxBaseDirectory, "profiles.ini");

        if (File.Exists(iniPath))
        {
            try
            {
                profiles.AddRange(ParseProfilesIni(File.ReadAllLines(iniPath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // profiles.ini не прочитался — ниже сработает запасной поиск по папке Profiles.
            }
        }

        if (profiles.Count == 0)
            profiles.AddRange(ScanProfilesFolder());

        return profiles
            .GroupBy(p => PathSafetyService.Normalize(p.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<FirefoxProfile> ParseProfilesIni(IEnumerable<string> lines)
    {
        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], current));
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0 && current is not null)
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        // Профиль, который указан как Default в секциях [Install...] — тот, которым реально пользуется установленный Firefox.
        var installDefaults = sections
            .Where(s => s.Name.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Values.TryGetValue("Default", out var d) ? d : null)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => ResolvePath(d!, isRelative: true))
            .ToList();

        foreach (var (name, values) in sections)
        {
            if (!name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) ||
                !values.TryGetValue("Path", out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
                continue;

            var isRelative = !values.TryGetValue("IsRelative", out var rel) || rel != "0";
            var fullPath = ResolvePath(rawPath, isRelative);
            if (!Directory.Exists(fullPath))
                continue;

            var isDefault = (values.TryGetValue("Default", out var def) && def == "1") ||
                            installDefaults.Any(d => string.Equals(PathSafetyService.Normalize(d), PathSafetyService.Normalize(fullPath), StringComparison.OrdinalIgnoreCase));

            yield return new FirefoxProfile
            {
                Name = values.TryGetValue("Name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : Path.GetFileName(fullPath),
                Path = fullPath,
                IsDefault = isDefault,
            };
        }
    }

    private IEnumerable<FirefoxProfile> ScanProfilesFolder()
    {
        var profilesDir = Path.Combine(_firefoxBaseDirectory, "Profiles");
        if (!Directory.Exists(profilesDir))
            yield break;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(profilesDir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            yield return new FirefoxProfile
            {
                Name = name,
                Path = dir,
                IsDefault = name.EndsWith(".default-release", StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    private string ResolvePath(string rawPath, bool isRelative)
    {
        var path = rawPath.Replace('/', Path.DirectorySeparatorChar);
        return isRelative ? Path.GetFullPath(Path.Combine(_firefoxBaseDirectory, path)) : Path.GetFullPath(path);
    }
}
