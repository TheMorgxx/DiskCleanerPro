using System.IO;

namespace DiskCleaner.Core.Services;

/// <summary>
/// Единая точка определения "это symlink/junction (reparse point) или нет".
/// Используется SizeCalculator (чтобы не считать размер через ссылку) и
/// CleanupService (чтобы не удалять содержимое через ссылку) — оба обязаны
/// спрашивать этот сервис перед тем, как зайти внутрь директории или
/// прочитать атрибуты файла.
/// </summary>
public sealed class ReparsePointGuard
{
    /// <summary>
    /// true, если путь (файл или директория) сам является reparse point —
    /// symlink, junction, mount point и т.п. Не бросает исключений: если
    /// путь недоступен/исчез, считается "не reparse point, но и не
    /// проверено" — вызывающий код в любом случае оборачивает доступ к пути
    /// в свой try/catch.
    /// </summary>
    public bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Дополнительная проверка "не привела ли нормализация path к выходу за
    /// пределы ожидаемого корня root" — используется как последняя линия
    /// защиты после Path.GetFullPath, на случай если что-то в цепочке
    /// каталогов оказалось symlink'ом, указывающим наружу. Обе стороны уже
    /// должны быть нормализованы (PathSafetyService.Normalize).
    /// </summary>
    public bool WouldEscapeRoot(string normalizedRoot, string normalizedPath)
    {
        var rootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !normalizedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
