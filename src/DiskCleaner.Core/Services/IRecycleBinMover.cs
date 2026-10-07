using System.IO;

namespace DiskCleaner.Core.Services;

/// <summary>Отправка файла или папки в Корзину. Вынесено в интерфейс, чтобы ядро не зависело от Windows и проверялось тестами.</summary>
public interface IRecycleBinMover
{
    bool TryMoveToRecycleBin(string path, bool isDirectory, out string? error);
}

/// <summary>
/// Реализация для Windows через Microsoft.VisualBasic.FileIO.FileSystem (входит в .NET).
/// Диалоги показываются только при ошибках.
/// </summary>
public sealed class WindowsRecycleBinMover : IRecycleBinMover
{
    public bool TryMoveToRecycleBin(string path, bool isDirectory, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "Корзина доступна только в Windows.";
            return false;
        }

        try
        {
            if (isDirectory)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                       or InvalidOperationException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
