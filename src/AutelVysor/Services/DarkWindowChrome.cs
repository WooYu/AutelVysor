using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AutelVysor.Services;

internal static class DarkWindowChrome
{
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
