using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutelVysor.Services;

internal sealed class RecordingSession(Process process, string path)
{
    public Process Process { get; } = process;

    public async Task StopAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                // A stop may race initial startup. Wait for the recorder's own
                // SDL window, then ask scrcpy to shut down and flush its muxer.
                var hwnd = await NativeWindowLocator.WaitForMainWindowAsync(Process, TimeSpan.FromSeconds(12));
                if (!PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException("Unable to request recording finalization.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Process.WaitForExitAsync(timeout.Token);
            }
            if (Process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException($"Recording did not complete successfully: {path} (exit {Process.ExitCode}).");
        }
        catch (Exception ex)
        {
            Exception? cleanupError = null;
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await Process.WaitForExitAsync(timeout.Token);
                }
            }
            catch (Exception cleanup) { cleanupError = cleanup; }
            var details = cleanupError is null ? ex.Message
                : $"{ex.Message} Cleanup could not confirm exit: {cleanupError.Message}";
            throw new InvalidOperationException($"Recording stopped with an error; verify the MP4 before using it: {path}. {details}", ex);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
