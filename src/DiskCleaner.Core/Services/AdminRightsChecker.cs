namespace DiskCleaner.Core.Services;

/// <summary>
/// Определяет, запущен ли процесс с правами администратора. На платформах,
/// где это не поддерживается (например при запуске юнит-тестов на Linux/macOS
/// в CI), безопасно возвращает false вместо падения — программа в любом
/// случае не ТРЕБУЕТ прав администратора для обычной работы (см. app.manifest,
/// requestedExecutionLevel="asInvoker"), это только влияет на то, какие
/// категории будут помечены как "requires admin".
/// </summary>
public static class AdminRightsChecker
{
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return false;

            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
