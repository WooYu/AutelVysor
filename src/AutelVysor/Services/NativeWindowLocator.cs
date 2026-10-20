using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutelVysor.Services;

internal static class NativeWindowLocator
{
    public static async Task<IntPtr> WaitForMainWindowAsync(Process process, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"scrcpy exited before its mirror window was created (exit code {process.ExitCode}).");

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            var enumerated = FindVisibleWindow(process.Id);
            if (enumerated != IntPtr.Zero)
                return enumerated;

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("Timed out waiting for the scrcpy mirror window.");
    }

    private static IntPtr FindVisibleWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId && IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == IntPtr.Zero) // GW_OWNER
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
