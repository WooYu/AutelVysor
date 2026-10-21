using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutelVysor.Models;
using AutelVysor.Services;

namespace AutelVysor.Controls;

public partial class MirrorSessionControl : UserControl, IDisposable
{
    private readonly AndroidBridge _bridge;
    private readonly ScrcpyService? _scrcpy;
    private readonly LegacyMinicapService? _minicap;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private AndroidDevice _device;
    private ScrcpySession? _scrcpySession;
    private LegacyMinicapSession? _minicapSession;
    private CancellationTokenSource? _legacyCts;
    private Task? _legacyLoop;
    private Point? _pointerDown;
    private Stopwatch? _pointerWatch;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _inputWidth;
    private int _inputHeight;
    private volatile bool _refreshRequested;
    private bool _disposed;
    private bool _forceLegacyFallback;
    private bool _orientationBusy;
    private DateTime _nextModernRetryUtc;

    public MirrorSessionControl(
        AndroidBridge bridge,
        ScrcpyService? scrcpy,
        LegacyMinicapService? minicap,
        AppSettings settings,
        AndroidDevice device)
    {
        InitializeComponent();
        _bridge = bridge;
        _scrcpy = scrcpy;
        _minicap = minicap;
        _settings = settings;
        _device = device;
        UpdateHeader();
        Loaded += async (_, _) =>
        {
            Focus();
            await EnsureRunningAsync();
        };
    }

    public string Serial => _device.Serial;
    public AndroidDevice Device => _device;
    public bool UsesLegacyBackend => _forceLegacyFallback || _device.ApiLevel is >= 19 and < 21 || (_device.ApiLevel == 0 && _scrcpy is null);

    public async Task UpdateConnectionAsync(AndroidDevice? current)
    {
        if (_disposed)
            return;

        if (current is null || !current.IsOnline)
        {
            StopModernSession();
            StopLegacySession();
            ShowOverlay("Device disconnected", "Autel Vysor will keep this tab and try to resume when the same device is available again.");
            StatusText.Text = "Waiting for device…";
            return;
        }

        _device = current;
        UpdateHeader();
        await EnsureRunningAsync();
    }

    public async Task RestartAsync()
    {
        if (_disposed)
            return;

        await _startGate.WaitAsync(_lifetime.Token);
        try
        {
            StopModernSession();
            StopLegacySession();
            _refreshRequested = true;
            if (UsesLegacyBackend)
                await StartLegacyIfNeededAsync();
            else
                await StartModernAsync();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task EnsureRunningAsync()
    {
        if (_disposed || !_device.IsOnline)
            return;

        await _startGate.WaitAsync(_lifetime.Token);
        try
        {
            if (UsesLegacyBackend)
            {
                await StartLegacyIfNeededAsync();
                return;
            }

            if ((_scrcpySession is null || _scrcpySession.HasExited) && DateTime.UtcNow >= _nextModernRetryUtc)
                await StartModernAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _nextModernRetryUtc = DateTime.UtcNow.AddSeconds(6);
            if (_device.ApiLevel == 0)
            {
                _forceLegacyFallback = true;
                UpdateHeader();
                await StartLegacyIfNeededAsync();
                StatusText.Text = $"scrcpy unavailable; compatibility fallback enabled: {ex.Message}";
            }
            else
            {
                ShowOverlay("Mirror unavailable", ex.Message);
                StatusText.Text = ex.Message;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartModernAsync()
    {
        if (_scrcpy is null || !File.Exists(_scrcpy.ExecutablePath))
            throw new FileNotFoundException("scrcpy.exe was not found. Run scripts/prepare-tools.ps1 or place the official scrcpy Windows files in the tools folder.");

        StopModernSession();
        LegacyHost.Visibility = Visibility.Collapsed;
        ScrcpyHost.Visibility = Visibility.Visible;
        ShowOverlay("Starting mirror…", $"{_device.DisplayName} · {_device.Serial}");
        StatusText.Text = "Starting scrcpy…";

        var session = await _scrcpy.StartEmbeddedAsync(_device, _lifetime.Token);
        if (_disposed)
        {
            session.Dispose();
            return;
        }

        try
        {
            ScrcpyHost.AttachExternalWindow(session.WindowHandle);
            _scrcpySession = session;
            session.Exited += ScrcpySessionOnExited;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _nextModernRetryUtc = DateTime.MinValue;
        Overlay.Visibility = Visibility.Collapsed;
        StatusText.Text = _settings.TurnScreenOffOnMirror
            ? "Connected · embedded scrcpy · device screen off"
            : "Connected · embedded scrcpy";
        BackendText.Text = "scrcpy · Android 5.0+";
    }

    private void ScrcpySessionOnExited(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
                return;
            try { ScrcpyHost.DetachExternalWindow(); } catch { }
            StatusText.Text = "Mirror process stopped · waiting for automatic restart";
            ShowOverlay("Mirror stopped", "If the device is still connected, the monitor will restart this session automatically.");
        });
    }

    private void StopModernSession()
    {
        try { ScrcpyHost.DetachExternalWindow(); } catch { }
        if (_scrcpySession is null)
            return;
        _scrcpySession.Exited -= ScrcpySessionOnExited;
        _scrcpySession.Dispose();
        _scrcpySession = null;
    }

    private async Task StartLegacyIfNeededAsync()
    {
        ScrcpyHost.Visibility = Visibility.Collapsed;
        LegacyHost.Visibility = Visibility.Visible;
        if (_legacyLoop is { IsCompleted: false })
            return;

        StopLegacySession();
        _legacyCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _legacyCts.Token;

        if (_settings.PreferLegacyAcceleration
            && _device.ApiLevel == 19
            && _minicap is not null
            && _minicap.CanAccelerate(_device))
        {
            ShowOverlay("Starting Android 4.4 accelerated mirror…", "Trying DeviceFarmer minicap; Autel Vysor will fall back to ADB screenshots automatically if it is not compatible with this device.");
            try
            {
                var session = await _minicap.StartAsync(_device, ct);
                _minicapSession = session;
                BackendText.Text = "minicap accelerated · Android 4.4 / API 19";
                _legacyLoop = Task.Run(() => LegacyMinicapLoopAsync(session, ct), ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                try { _minicapSession?.Dispose(); } catch { }
                _minicapSession = null;
                StatusText.Text = $"minicap unavailable; using ADB compatibility fallback: {ex.Message}";
            }
        }

        BackendText.Text = "ADB compatibility · Android 4.4";
        ShowOverlay("Starting Android 4.4 mirror…", "Compatibility mode uses ADB screenshots and input injection.");
        _legacyLoop = Task.Run(() => LegacyRefreshLoopAsync(ct), ct);
    }

    private async Task LegacyMinicapLoopAsync(LegacyMinicapSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var data = await session.ReadFrameAsync(ct);
                ct.ThrowIfCancellationRequested();
                var bitmap = DecodePng(data);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || ct.IsCancellationRequested || !ReferenceEquals(_minicapSession, session))
                        return;
                    _pixelWidth = bitmap.PixelWidth;
                    _pixelHeight = bitmap.PixelHeight;
                    _inputWidth = session.RealWidth;
                    _inputHeight = session.RealHeight;
                    LegacyImage.Source = bitmap;
                    Overlay.Visibility = Visibility.Collapsed;
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    StatusText.Text = $"Connected · minicap · {_pixelWidth}×{_pixelHeight} stream · input {_inputWidth}×{_inputHeight} · {elapsed:0} ms";
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Disposing a canceled socket may surface IOException rather than cancellation.
        }
        catch (Exception ex)
        {
            var shouldFallback = await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !ReferenceEquals(_minicapSession, session))
                    return false;
                _minicapSession = null;
                BackendText.Text = "ADB compatibility · minicap fallback";
                StatusText.Text = $"minicap stream stopped; falling back to ADB screenshots: {ex.Message}";
                return true;
            });

            try { session.Dispose(); } catch { }
            if (shouldFallback && !ct.IsCancellationRequested)
                await LegacyRefreshLoopAsync(ct);
        }
    }

    private void StopLegacySession()
    {
        try { _legacyCts?.Cancel(); } catch { }
        try { _minicapSession?.Dispose(); } catch { }
        _minicapSession = null;
        _legacyLoop = null;
        try { _legacyCts?.Dispose(); } catch { }
        _legacyCts = null;
    }

    private async Task LegacyRefreshLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var data = await _bridge.CaptureScreenshotBytesAsync(_device.Serial, ct);
                ct.ThrowIfCancellationRequested();
                var bitmap = DecodePng(data);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || ct.IsCancellationRequested)
                        return;
                    _pixelWidth = bitmap.PixelWidth;
                    _pixelHeight = bitmap.PixelHeight;
                    _inputWidth = _pixelWidth;
                    _inputHeight = _pixelHeight;
                    LegacyImage.Source = bitmap;
                    Overlay.Visibility = Visibility.Collapsed;
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    StatusText.Text = $"Connected · {_pixelWidth}×{_pixelHeight} · frame {elapsed:0} ms";
                });
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || ct.IsCancellationRequested)
                        return;
                    StatusText.Text = $"Compatibility capture failed ({failures}): {ex.Message}";
                    if (failures >= 2)
                        ShowOverlay("Waiting for Android 4.4 device", "The session will resume automatically after ADB connectivity returns.");
                });
            }

            try
            {
                var delay = _refreshRequested ? 30 : failures > 0 ? 800 : 160;
                _refreshRequested = false;
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static BitmapImage DecodePng(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void UpdateHeader()
    {
        DeviceTitleText.Text = _device.DisplayName;
        var api = _device.ApiLevel > 0 ? _device.ApiLevel.ToString() : "?";
        DeviceMetaText.Text = $"{_device.Serial} · Android {Fallback(_device.AndroidVersion)} · API {api} · {Fallback(_device.Abi)} · {_device.Connection}";
        BackendText.Text = UsesLegacyBackend ? "ADB compatibility · API 19/20" : "scrcpy · API 21+";
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;

    private void ShowOverlay(string title, string detail)
    {
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        Overlay.Visibility = Visibility.Visible;
    }

    private async Task ExecuteKeyAsync(int keyCode)
    {
        try
        {
            await _bridge.KeyEventAsync(_device.Serial, keyCode, _lifetime.Token);
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(4);
    private async void Home_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(3);
    private async void Recents_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(187);
    private async void VolumeDown_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(25);
    private async void VolumeUp_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(24);
    private async void Power_Click(object sender, RoutedEventArgs e) => await ExecuteKeyAsync(26);

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        try { await RestartAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void FitView_Click(object sender, RoutedEventArgs e)
    {
        if (UsesLegacyBackend)
        {
            LegacyImage.Stretch = Stretch.Uniform;
            StatusText.Text = "View mode: Fit.";
            return;
        }

        ScrcpyHost.SetFitMode();
        StatusText.Text = "View mode: Fit.";
    }

    private async void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (UsesLegacyBackend)
            {
                LegacyImage.Stretch = Stretch.None;
                StatusText.Text = "View mode: 1:1. Large Android displays may be cropped by the current window.";
                return;
            }

            var (width, height) = await _bridge.GetDisplaySizeAsync(_device.Serial, _lifetime.Token);
            var rotation = await _bridge.GetCurrentRotationDegreesAsync(_device.Serial, _lifetime.Token);
            if (rotation is 90 or 270)
                (width, height) = (height, width);

            ScrcpyHost.SetActualSize(width, height);
            StatusText.Text = $"View mode: 1:1 · {width}×{height}. Large displays may be cropped by the current window.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Rotate_Click(object sender, RoutedEventArgs e) => await ChangeOrientationAsync(automatic: false);

    private async void AutoRotate_Click(object sender, RoutedEventArgs e) => await ChangeOrientationAsync(automatic: true);

    private async Task ChangeOrientationAsync(bool automatic)
    {
        if (_disposed || _orientationBusy)
            return;
        _orientationBusy = true;
        RotateButton.IsEnabled = false;
        AutoRotateButton.IsEnabled = false;
        try
        {
            // Orientation changes always return to Fit, including from a cropped 1:1 view.
            ScrcpyHost.SetFitMode();
            LegacyImage.Stretch = Stretch.Uniform;
            if (automatic)
            {
                await _bridge.EnableAutoRotationAsync(_device.Serial, _lifetime.Token);
                StatusText.Text = "Android auto-rotation enabled · view fitted to window.";
            }
            else
            {
                var degrees = await _bridge.ToggleDeviceOrientationAsync(_device.Serial, _lifetime.Token);
                var started = Stopwatch.StartNew();
                var applied = false;
                while (started.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (await _bridge.GetCurrentRotationDegreesAsync(_device.Serial, _lifetime.Token) == degrees)
                    {
                        applied = true;
                        break;
                    }
                    await Task.Delay(100, _lifetime.Token);
                }
                StatusText.Text = applied
                    ? "Android orientation switched · view fitted to window."
                    : "Orientation requested · the current Android app may lock its orientation.";
            }
            if (_minicapSession is not null)
                await RestartAsync(); // minicap's capture projection is set when its stream starts.
            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _orientationBusy = false;
            if (!_disposed)
            {
                RotateButton.IsEnabled = true;
                AutoRotateButton.IsEnabled = true;
            }
        }
    }

    private void LegacyImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pointerDown = e.GetPosition(LegacyImage);
        _pointerWatch = Stopwatch.StartNew();
        LegacyImage.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private async void LegacyImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!UsesLegacyBackend)
            return;
        e.Handled = true;
        await ExecuteKeyAsync(4);
    }

    private void LegacyHost_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void LegacyHost_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        try
        {
            foreach (var file in files.Where(File.Exists))
            {
                if (string.Equals(Path.GetExtension(file), ".apk", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = $"Installing {Path.GetFileName(file)}…";
                    await _bridge.InstallAsync(_device.Serial, file, _lifetime.Token);
                    StatusText.Text = $"Installed {Path.GetFileName(file)}";
                }
                else
                {
                    var remote = "/sdcard/Download/" + Path.GetFileName(file);
                    StatusText.Text = $"Sending {Path.GetFileName(file)}…";
                    await _bridge.PushAsync(_device.Serial, file, remote, _lifetime.Token);
                    StatusText.Text = $"Sent to {remote}";
                }
            }
            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void LegacyImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pointerDown is null || _pixelWidth <= 0 || _pixelHeight <= 0)
            return;

        try
        {
            var startView = _pointerDown.Value;
            var endView = e.GetPosition(LegacyImage);
            LegacyImage.ReleaseMouseCapture();
            _pointerDown = null;
            _pointerWatch?.Stop();

            if (!TryMapToDevice(startView, out var start) || !TryMapToDevice(endView, out var end))
                return;

            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 12)
                await _bridge.TapAsync(_device.Serial, start.X, start.Y, _lifetime.Token);
            else
                await _bridge.SwipeAsync(_device.Serial, start.X, start.Y, end.X, end.Y, (int)Math.Clamp(_pointerWatch?.ElapsedMilliseconds ?? 250, 80, 1500), _lifetime.Token);

            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _pointerWatch = null;
        }
    }

    private bool TryMapToDevice(Point viewPoint, out (int X, int Y) devicePoint)
    {
        devicePoint = default;
        if (_pixelWidth <= 0 || _pixelHeight <= 0 || LegacyImage.ActualWidth <= 0 || LegacyImage.ActualHeight <= 0)
            return false;

        var scale = Math.Min(LegacyImage.ActualWidth / _pixelWidth, LegacyImage.ActualHeight / _pixelHeight);
        var renderedWidth = _pixelWidth * scale;
        var renderedHeight = _pixelHeight * scale;
        var offsetX = (LegacyImage.ActualWidth - renderedWidth) / 2.0;
        var offsetY = (LegacyImage.ActualHeight - renderedHeight) / 2.0;
        if (viewPoint.X < offsetX || viewPoint.Y < offsetY || viewPoint.X > offsetX + renderedWidth || viewPoint.Y > offsetY + renderedHeight)
            return false;

        var normalizedX = (viewPoint.X - offsetX) / renderedWidth;
        var normalizedY = (viewPoint.Y - offsetY) / renderedHeight;
        var targetWidth = _inputWidth > 0 ? _inputWidth : _pixelWidth;
        var targetHeight = _inputHeight > 0 ? _inputHeight : _pixelHeight;
        devicePoint = (
            (int)Math.Clamp(normalizedX * targetWidth, 0, targetWidth - 1),
            (int)Math.Clamp(normalizedY * targetHeight, 0, targetHeight - 1));
        return true;
    }

    private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!UsesLegacyBackend)
            return;

        var keyCode = e.Key switch
        {
            Key.Enter => 66,
            Key.Back => 67,
            Key.Delete => 112,
            Key.Escape => 4,
            Key.Left => 21,
            Key.Right => 22,
            Key.Up => 19,
            Key.Down => 20,
            Key.Tab => 61,
            _ => 0
        };
        if (keyCode == 0)
            return;

        e.Handled = true;
        await ExecuteKeyAsync(keyCode);
    }

    private async void Root_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!UsesLegacyBackend || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            return;

        try
        {
            await _bridge.InputTextAsync(_device.Serial, e.Text, _lifetime.Token);
            _refreshRequested = true;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        StopModernSession();
        StopLegacySession();
        GC.SuppressFinalize(this);
    }
}
