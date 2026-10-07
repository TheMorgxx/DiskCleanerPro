using System.IO;

namespace DiskCleaner.Tests;

/// <summary>Создаёт уникальную временную директорию для теста и удаляет её при Dispose.</summary>
public sealed class TempTestDirectory : IDisposable
{
    public string Path { get; }

    public TempTestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskCleanerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string relativePath, string content = "test")
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string CreateSubDirectory(string relativePath)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Лучшее усилие — не роняем тест из-за неудачной уборки временной папки.
        }
    }
}
