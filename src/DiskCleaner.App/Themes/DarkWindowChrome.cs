using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskCleaner.App.Themes;

/// <summary>Optional native title-bar appearance; never changes window behaviour.</summary>
public static class DarkWindowChrome
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DarkWindowChrome), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        if ((bool)args.NewValue) window.SourceInitialized += OnSourceInitialized;
        Apply(window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is Window window) Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var enabled = GetIsEnabled(window) ? 1 : 0;
            // Unsupported Windows versions keep their normal title bar.
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (ExternalException) { }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
