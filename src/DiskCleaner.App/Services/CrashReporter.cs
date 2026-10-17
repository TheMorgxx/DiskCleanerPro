using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DiskCleaner.Core.Services;

namespace DiskCleaner.App.Services;

/// <summary>
/// Глобальная обработка необработанных исключений. Каждое исключение всегда записывается в отдельный
/// crash_….log рядом с обычными журналами (%LOCALAPPDATA%\DiskCleaner\logs). Защита только двух видов:
/// от повторного входа в обработчик на том же потоке (повторный вход: запись есть, окна и завершения нет)
/// и от показа второго окна (окно за всё время работы процесса показывается не больше одного раза:
/// после него приложение всё равно завершается).
/// Сам обработчик никогда не бросает исключений наружу.
/// </summary>
internal static class CrashReporter
{
    private const string WindowTitle = "DiskCleaner — критическая ошибка";

    private static readonly object FileLock = new();

    [ThreadStatic]
    private static bool _handling;

    private static int _windowShown;

    /// <summary>Вызывать до создания главного окна.</summary>
    public static void Register(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // Ошибка UI-потока: запись, сообщение, завершение. Продолжать после неизвестной ошибки небезопасно:
    // программа удаляет файлы.
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var reentry = _handling;
        _handling = true;
        try
        {
            e.Handled = true;
            var outcome = Write("Application.DispatcherUnhandledException (поток UI)", e.Exception);
            if (reentry)
                return;

            try
            {
                ShowOnce(BuildMessage(outcome, e.Exception));
            }
            finally
            {
                Terminate();
            }
        }
        catch
        {
            // Обработчик не должен падать сам.
        }
        finally
        {
            if (!reentry)
                _handling = false;
        }
    }

    // Аварийное завершение отменить нельзя: пишем и показываем сообщение прямо в вызывающем потоке,
    // не дожидаясь UI-потока.
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var reentry = _handling;
        _handling = true;
        try
        {
            var outcome = Write($"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", e.ExceptionObject);
            if (reentry)
                return;

            ShowOnce(BuildMessage(outcome, e.ExceptionObject));
        }
        catch
        {
            // Обработчик не должен падать сам.
        }
        finally
        {
            if (!reentry)
                _handling = false;
        }
    }

    // Только запись в crash-лог и SetObserved(). Окна нет.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Write("TaskScheduler.UnobservedTaskException (окна нет, исключение помечено как обработанное)", e.Exception);
        }
        catch
        {
            // Обработчик не должен падать сам.
        }

        try
        {
            e.SetObserved();
        }
        catch
        {
            // Обработчик не должен падать сам.
        }
    }

    private readonly record struct WriteOutcome(string? Path, string? Error);

    private static WriteOutcome Write(string source, object? error)
    {
        try
        {
            var directory = new Logger().LogDirectory;
            var path = Path.Combine(directory, $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

            var text = new StringBuilder()
                .AppendLine("==== DiskCleaner crash report ====")
                .AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (UTC {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss})")
                .AppendLine($"Источник: {source}")
                .AppendLine($"Версия приложения: {GetVersion()}")
                .AppendLine("Исключение:")
                .AppendLine(error?.ToString() ?? "(данных об исключении нет)")
                .AppendLine()
                .ToString();

            lock (FileLock)
            {
                File.AppendAllText(path, text, Encoding.UTF8);
            }

            return new WriteOutcome(path, null);
        }
        catch (Exception writeError)
        {
            return new WriteOutcome(null, writeError.Message);
        }
    }

    private static string GetVersion()
    {
        try
        {
            return typeof(CrashReporter).Assembly.GetName().Version?.ToString() ?? "неизвестна";
        }
        catch
        {
            return "неизвестна";
        }
    }

    private static string BuildMessage(WriteOutcome outcome, object? error)
    {
        var head = "Произошла непредвиденная ошибка, приложение будет закрыто.\n\n";
        var brief = error is Exception ex ? $"{ex.GetType().Name}: {ex.Message}" : error?.ToString() ?? "";

        return outcome.Path is not null
            ? head + $"Подробности записаны в файл:\n{outcome.Path}\n\nПриложите его к баг-репорту.\n\n{brief}"
            : head + $"Записать файл crash-лога не удалось ({outcome.Error}).\n\n{brief}";
    }

    private static void ShowOnce(string text)
    {
        if (Interlocked.Exchange(ref _windowShown, 1) != 0)
            return;

        try
        {
            MessageBox.Show(text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Не удалось показать окно: запись в файл уже сделана.
        }
    }

    private static void Terminate()
    {
        try
        {
            Application.Current?.Shutdown(1);
        }
        catch
        {
            Environment.Exit(1);
        }
    }
}
