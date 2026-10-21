using System.Diagnostics;

namespace AutelVysor.Services;

public sealed class ScrcpySession : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    internal ScrcpySession(Process process, IntPtr windowHandle)
    {
        _process = process;
        WindowHandle = windowHandle;
        _process.EnableRaisingEvents = true;
        _process.Exited += ProcessOnExited;
    }

    public IntPtr WindowHandle { get; }
    public bool HasExited => _process.HasExited;
    public int ProcessId => _process.Id;
    public event EventHandler? Exited;

    private void ProcessOnExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                // Embedded SDL has no top-level main window to accept WM_CLOSE.
                // Only allow a grace period if the close request was actually sent.
                if (!_process.CloseMainWindow() || !_process.WaitForExit(1200))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
        _process.Exited -= ProcessOnExited;
        _process.Dispose();
        GC.SuppressFinalize(this);
    }
}
