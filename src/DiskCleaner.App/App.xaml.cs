using System.Windows;
using DiskCleaner.App.Services;

namespace DiskCleaner.App;

public partial class App : Application
{
    public App()
    {
        // Обработчики необработанных исключений регистрируются до создания главного окна.
        CrashReporter.Register(this);
    }
}
