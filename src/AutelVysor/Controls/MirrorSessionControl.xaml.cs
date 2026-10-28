using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutelVysor.Models;
using AutelVysor.Services;

namespace AutelVysor.Controls;

public partial class MirrorSessionControl : UserControl, IDisposable
{
    private readonly AndroidBridge _bridge;
    private readonly AdvancedAdbService _advanced;
    private readonly AdbKeyboardService _keyboard;
    private UnicodeSetupWindow? _unicodeWindow;
    private readonly ScrcpyService? _scrcpy;
    private readonly LegacyMinicapService? _minicap;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _inputGate = new(1, 1);
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
    private bool _updatingPhoneKeyboard;
    private DateTime _nextModernRetryUtc;
    private DateTime _lastRotationCheckUtc;
    private int _offlineChecks;
    private int _restartFailures;
    private bool _viewReady, _updatingView, _textSendBusy, _keyboardChanging, _audioBusy;
    private int _focusRequestVersion;
    private Window? _focusWindow;
    private int _viewChangeVersion;
    private double _viewZoom = 1;
    private string _keyboardMode = "auto";

    public MirrorSessionControl(
        AndroidBridge bridge,
        ScrcpyService? scrcpy,
        LegacyMinicapService? minicap,
        AppSettings settings,
        AndroidDevice device)
    {
        InitializeComponent();
        _bridge = bridge;
        _advanced = new AdvancedAdbService(bridge.ExecutablePath);
        _keyboard = new AdbKeyboardService(bridge.ExecutablePath);
        _scrcpy = scrcpy;
        _minicap = minicap;
        _settings = settings;
        _device = device;
        UpdateHeader();
        _viewReady = true;
        Loaded += async (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (_focusWindow != owner)
            {
                if (_focusWindow is not null) _focusWindow.Activated -= FocusWindow_Activated;
                _focusWindow = owner;
                if (owner is not null) owner.Activated += FocusWindow_Activated;
            }
            await EnsureRunningAsync();
            await RefreshPhoneKeyboardSettingAsync();
            if (!_disposed && IsLoaded && _scrcpySession is { HasExited: false } session && !ScrcpyHost.HasChild)
                ScrcpyHost.AttachExternalWindow(session.WindowHandle);
            FocusKeyboardWhenReady();
            RefreshKeyboardRecovery();
        };
    }

    public FrameworkElement DetachToolsForSidebar()
    {
        if (ReferenceEquals(SessionTools.Parent, SessionLayout)) SessionLayout.Children.Remove(SessionTools);
        InlineToolsColumn.Width = new GridLength(0);
        // MainWindow supplies the scrolling surface after rehosting.
        if (SessionTools.Child is ScrollViewer scroll) scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        return SessionTools;
    }

    public void FitToWindow()
    {
        if (!_disposed) ApplyZoom(1);
    }

    public string Serial => _device.Serial;
    public int RotationDegrees { get; private set; }
    public double DisplayAspectRatio { get; private set; } = 9d / 19d;
    public event EventHandler? OrientationChanged;

    private async Task RefreshPhoneKeyboardSettingAsync()
    {
        if (_disposed || !_device.IsOnline || _updatingPhoneKeyboard) return;
        _updatingPhoneKeyboard = true;
        PhoneKeyboardToggle.IsEnabled = false;
        try
        {
            PhoneKeyboardToggle.IsChecked = !await _bridge.GetShowSoftKeyboardWithHardwareAsync(_device.Serial, _lifetime.Token);
            PhoneKeyboardToggle.ToolTip = "Controls Android's setting for showing the on-screen keyboard while a hardware keyboard is connected.";
            PhoneKeyboardToggle.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { PhoneKeyboardToggle.IsChecked = null; PhoneKeyboardToggle.ToolTip = "Phone keyboard setting unavailable: " + ex.Message; }
        finally { _updatingPhoneKeyboard = false; }
    }

    private async void PhoneKeyboardToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_disposed || _updatingPhoneKeyboard || !IsLoaded || !PhoneKeyboardToggle.IsEnabled) return;
        var hide = PhoneKeyboardToggle.IsChecked == true;
        _updatingPhoneKeyboard = true;
        PhoneKeyboardToggle.IsEnabled = false;
        try
        {
            await _bridge.SetShowSoftKeyboardWithHardwareAsync(_device.Serial, !hide, _lifetime.Token);
            StatusText.Text = hide ? "Phone keyboard hidden while a hardware keyboard is connected." : "Phone keyboard may appear with a hardware keyboard.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PhoneKeyboardToggle.IsChecked = null;
            StatusText.Text = "Phone keyboard setting failed: " + ex.Message;
        }
        finally
        {
            _updatingPhoneKeyboard = false;
            if (!_disposed) await RefreshPhoneKeyboardSettingAsync();
        }
    }

    private void FocusWindow_Activated(object? sender, EventArgs e) => FocusKeyboardWhenReady();

    public void FocusKeyboardWhenReady() => RequestKeyboardFocus(false);

    private async void RequestKeyboardFocus(bool explicitRequest)
    {
        if (_disposed) return;
        var request = ++_focusRequestVersion;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        if (_disposed || request != _focusRequestVersion) return;
        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (_disposed || request != _focusRequestVersion) return;
                if (!IsVisible || Window.GetWindow(this)?.IsActive != true)
                {
                    if (explicitRequest) FocusKeyboardButton.Content = "Focus failed";
                    return;
                }
                // Do not take keys away from PC text entry, search, or mode selection.
                if (!(explicitRequest && attempt == 0) && Keyboard.FocusedElement is
                    (System.Windows.Controls.Primitives.TextBoxBase or ComboBox or PasswordBox)) return;
                var focused = false;
                if (UsesLegacyBackend) focused = Focus();
                else if (ScrcpyHost.HasChild)
                {
                    if (!ScrcpyHost.HasKeyboardFocus) Keyboard.Focus(ScrcpyHost);
                    focused = ScrcpyHost.FocusEmbeddedWindow();
                }
                if (focused)
                {
                    // Let WPF finish routing the click/layout before confirming native focus.
                    await Task.Delay(40, _lifetime.Token);
                    if (_disposed || request != _focusRequestVersion) return;
                    if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
                    if (!(UsesLegacyBackend ? IsKeyboardFocusWithin : ScrcpyHost.HasKeyboardFocus)) continue;
                    FocusKeyboardButton.Content = "Keyboard ready ✓";
                    FocusKeyboardButton.Background = new SolidColorBrush(Color.FromRgb(70, 96, 82));
                    if (explicitRequest) StatusText.Text = "Keyboard ready. Select a phone text field to type; PC-composed Chinese uses Paste text.";
                    return;
                }
                await Task.Delay(40, _lifetime.Token);
            }
            FocusKeyboardButton.Content = "Retry keyboard focus";
            FocusKeyboardButton.Background = Brushes.Maroon;
            if (explicitRequest) StatusText.Text = "Keyboard focus unavailable. Check that the mirror is running and visible.";
        }
        catch (OperationCanceledException) { }
    }
    public AndroidDevice Device => _device;
    public bool UsesLegacyBackend => _forceLegacyFallback || _device.ApiLevel is >= 19 and < 21 || (_device.ApiLevel == 0 && _scrcpy is null);

    public async Task UpdateConnectionAsync(AndroidDevice? current)
    {
        if (_disposed)
            return;

        if (current is null || !current.IsOnline)
        {
            _offlineChecks++;
            if (_offlineChecks == 1)
            {
                StatusText.Text = "Connection unstable · confirming ADB disconnect…";
                return;
            }
            StopModernSession();
            StopLegacySession();
            ShowOverlay("Device disconnected", "Waiting for the same device to return. USB/ADB interruptions are retried automatically.");
            StatusText.Text = "Waiting for device…";
            return;
        }

        if (_offlineChecks > 0)
        {
            _nextModernRetryUtc = DateTime.MinValue;
            _restartFailures = 0;
            _ = RefreshPhoneKeyboardSettingAsync();
        }
        _offlineChecks = 0;
        _device = current;
        UpdateHeader();
        await EnsureRunningAsync();
        if (_lastRotationCheckUtc == DateTime.MinValue ||
            DateTime.UtcNow - _lastRotationCheckUtc >= TimeSpan.FromSeconds(8))
        {
            _lastRotationCheckUtc = DateTime.UtcNow;
            try { await RefreshRotationStateAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RotateStateText.Text = "Rotation: unavailable";
            }
        }
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
            _restartFailures = Math.Min(_restartFailures + 1, 6);
            var retrySeconds = Math.Min(30, 1 << _restartFailures);
            _nextModernRetryUtc = DateTime.UtcNow.AddSeconds(retrySeconds);
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
                StatusText.Text = $"Mirror failed · retry in {Math.Min(30, 1 << _restartFailures)}s: {ex.Message}";
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

        var session = await _scrcpy.StartEmbeddedAsync(_device, _lifetime.Token, _keyboardMode);
        if (_disposed)
        {
            session.Dispose();
            return;
        }

        try
        {
            _scrcpySession = session;
            session.Exited += ScrcpySessionOnExited;
            if (IsLoaded) ScrcpyHost.AttachExternalWindow(session.WindowHandle);
            else NativeWindowHost.HideExternalWindow(session.WindowHandle);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _nextModernRetryUtc = DateTime.MinValue;
        _restartFailures = 0;
        Overlay.Visibility = Visibility.Collapsed;
        StatusText.Text = _settings.TurnScreenOffOnMirror
            ? "Connected · embedded scrcpy · device screen off"
            : "Connected · embedded scrcpy";
        BackendText.Text = "scrcpy · Android 5.0+";
        FocusKeyboardWhenReady();
    }

    private void ScrcpySessionOnExited(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
                return;
            try { ScrcpyHost.DetachExternalWindow(); } catch { }
            _nextModernRetryUtc = DateTime.UtcNow.AddSeconds(2);
            StatusText.Text = "Mirror process stopped · reconnecting when ADB is available";
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
        KeyboardModeBox.IsEnabled = !UsesLegacyBackend && !_keyboardChanging;
        SendTextButton.Content = !UsesLegacyBackend && _device.ApiLevel >= 24 ? "Paste text" : "Send text";
        AudioRouteText.Text = !_settings.EnableAudio || _device.ApiLevel < 30
            ? "Audio: device"
            : _settings.KeepDeviceAudio && _device.ApiLevel >= 33
                ? "Audio: PC + device (configured)"
                : _settings.KeepDeviceAudio
                    ? "Audio: device/BT priority"
                    : "Audio: PC priority";
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
            await _inputGate.WaitAsync(_lifetime.Token);
            try { await _bridge.KeyEventAsync(_device.Serial, keyCode, _lifetime.Token); }
            finally { _inputGate.Release(); }
            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
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

    private void ApplyZoom(double zoom)
    {
        ++_viewChangeVersion;
        _viewZoom = zoom;
        LegacyImage.Stretch = Stretch.Uniform;
        ScrcpyHost.SetZoom(zoom);
        _updatingView = true;
        try
        {
            PanHorizontal.Value = PanVertical.Value = .5;
            ZoomBox.SelectedIndex = zoom switch { 1 => 0, 1.25 => 1, 1.5 => 2, 2 => 3, _ => 4 };
        }
        finally { _updatingView = false; }
        SetPanVisibility(zoom > 1);
        ApplyPan();
        StatusText.Text = zoom == 1 ? "View mode: Fit." : $"View: {zoom:P0} · use the edge scrollbars to reach the cropped areas; Fit restores the full frame.";
    }

    private async void ZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady || _updatingView) return;
        var tag = (ZoomBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (tag == "actual") { await SetActualSizeAsync(); return; }
        if (double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom))
            ApplyZoom(zoom);
    }

    private void SetPanVisibility(bool visible)
        => PanHorizontal.Visibility = PanVertical.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void PanChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_viewReady && !_updatingView) ApplyPan();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewReady) ApplyLegacyTransform();
    }

    private void ApplyPan()
    {
        ScrcpyHost.SetPan(PanHorizontal.Value, PanVertical.Value);
        ApplyLegacyTransform();
    }

    private void ApplyLegacyTransform()
    {
        LegacyImage.RenderTransformOrigin = new Point(.5, .5);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(_viewZoom, _viewZoom));
        transform.Children.Add(new TranslateTransform(
            (.5 - PanHorizontal.Value) * (_viewZoom - 1) * LegacyImage.ActualWidth,
            (.5 - PanVertical.Value) * (_viewZoom - 1) * LegacyImage.ActualHeight));
        LegacyImage.RenderTransform = transform;
    }

    private async void ActualSize_Click(object sender, RoutedEventArgs e)
        => await SetActualSizeAsync();

    private async Task SetActualSizeAsync()
    {
        var request = ++_viewChangeVersion;
        try
        {
            if (UsesLegacyBackend)
            {
                _viewZoom = 1;
                LegacyImage.Stretch = Stretch.None;
                ApplyLegacyTransform();
                SetPanVisibility(false);
                SelectActualSize();
                StatusText.Text = "View mode: 1:1. Large Android displays may be cropped by the current window.";
                return;
            }

            var (width, height) = await _bridge.GetDisplaySizeAsync(_device.Serial, _lifetime.Token);
            var rotation = await _bridge.GetCurrentRotationDegreesAsync(_device.Serial, _lifetime.Token);
            if (_disposed || request != _viewChangeVersion) return;
            if (rotation is 90 or 270)
                (width, height) = (height, width);

            ScrcpyHost.SetActualSize(width, height);
            _updatingView = true;
            try { PanHorizontal.Value = PanVertical.Value = .5; }
            finally { _updatingView = false; }
            SelectActualSize();
            SetPanVisibility(true);
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

    private void SelectActualSize()
    {
        _updatingView = true;
        try { ZoomBox.SelectedIndex = 5; }
        finally { _updatingView = false; }
    }

    private async void KeyboardModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady || _disposed || _keyboardChanging) return;
        _keyboardMode = (KeyboardModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _keyboardChanging = true;
        KeyboardModeBox.IsEnabled = false;
        try
        {
            if (IsLoaded) await RestartAsync();
            StatusText.Text = "Keyboard mode changed. Hardware uses the phone's input method; use Paste text for Chinese composed on the PC.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            _keyboardChanging = false;
            if (!_disposed) KeyboardModeBox.IsEnabled = !UsesLegacyBackend;
        }
    }

    private void InputOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void PhoneKeyboardSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await _bridge.RunShellCommandAsync(_device.Serial, "am start -a android.settings.HARD_KEYBOARD_SETTINGS", _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task RefreshRotationStateAsync()
    {
        var (rotation, automatic) = await _advanced.GetRotationStateAsync(_device.Serial, _lifetime.Token);
        var ratio = DisplayAspectRatio;
        try
        {
            var (width, height) = await _bridge.GetDisplaySizeAsync(_device.Serial, _lifetime.Token);
            ratio = rotation is 90 or 270 ? (double)height / width : (double)width / height;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (RotationDegrees != rotation || Math.Abs(DisplayAspectRatio - ratio) > 0.001)
        {
            RotationDegrees = rotation;
            DisplayAspectRatio = ratio;
            OrientationChanged?.Invoke(this, EventArgs.Empty);
        }
        RotateStateText.Text = $"Rotation: {rotation}° · {(automatic ? "Auto" : "Locked")}";
        AutoRotateButton.Content = automatic ? "Auto rotate · On" : "Auto rotate · Off";
        AutoRotateButton.Background = automatic ? new SolidColorBrush(Color.FromRgb(70, 96, 82)) : new SolidColorBrush(Color.FromRgb(48, 54, 51));
        AutoRotateButton.ToolTip = automatic ? "Sensor rotation enabled; actual orientation depends on the device and foreground app." : "Orientation is locked. Click to enable sensor rotation.";
    }

    private void FocusMirror_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        FocusKeyboardButton.Content = "Focusing…";
        RequestKeyboardFocus(true);
    }

    private async void SendText_Click(object sender, RoutedEventArgs e) => await SendTextAsync(false);

    private async void AndroidTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await SendTextAsync(true);
    }

    private async Task SendTextAsync(bool returnToEditor)
    {
        if (_textSendBusy || _disposed) return;
        var text = AndroidTextBox.Text;
        if (string.IsNullOrEmpty(text) || text.Length > 4000)
        {
            StatusText.Text = "Enter between 1 and 4000 characters in the text box.";
            return;
        }

        _textSendBusy = true;
        SendTextButton.IsEnabled = false;
        try
        {
            if (!UsesLegacyBackend && _device.ApiLevel >= 24)
            {
                var session = _scrcpySession;
                if (session is null || session.HasExited)
                {
                    StatusText.Text = "Mirror is not connected. Restart it, tap a phone input field and retry Paste text.";
                    return;
                }
                // Let the toolbar click finish before moving native input focus.
                var requested = await Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || !IsVisible || !ReferenceEquals(session, _scrcpySession) || session.HasExited) return false;
                    Clipboard.SetText(text);
                    return ScrcpyHost.PasteClipboard();
                }, DispatcherPriority.Input);
                StatusText.Text = requested
                    ? "Paste requested through scrcpy. Confirm the text in the focused phone field; no ADBKeyBoard is required."
                    : "Paste not sent. Activate this window, release modifier keys, tap a phone input field and retry.";
                if (requested && returnToEditor)
                    await Task.Delay(250, _lifetime.Token);
                return;
            }
            if (text.Any(ch => ch > 127))
            {
                // adb shell input text cannot reliably type CJK / Unicode.
                // The helper IME is opt-in, and its absence is reported explicitly.
                var result = await _keyboard.SendAsync(_device.Serial, text, _lifetime.Token);
                StatusText.Text = result.Message;
            }
            else
            {
                await _bridge.InputTextAsync(_device.Serial, text, _lifetime.Token);
                StatusText.Text = "ASCII input sent to Android via ADB. Tap the target text field first.";
            }
            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _textSendBusy = false;
            if (!_disposed) SendTextButton.IsEnabled = true;
            if (returnToEditor && !_disposed && IsVisible && Window.GetWindow(this)?.IsActive == true)
            {
                AndroidTextBox.Focus();
                if (AndroidTextBox.Text == text) AndroidTextBox.SelectAll();
            }
        }
    }

    private void CopyAndroidText_Click(object sender, RoutedEventArgs e)
    {
        var text = AndroidTextBox.Text;
        if (string.IsNullOrEmpty(text)) { StatusText.Text = "Enter text first."; return; }
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = UsesLegacyBackend
                ? "Text copied. Android 4.4 Unicode requires the optional ADBKeyBoard IME; use Send via ADB IME."
                : "Unicode text copied. Focus an Android text field inside scrcpy and press Ctrl+V (clipboard sync must be enabled).";
        }
        catch (Exception ex) { StatusText.Text = $"Clipboard unavailable: {ex.Message}"; }
    }

    private async void SendUnicode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _keyboard.SendAsync(_device.Serial, AndroidTextBox.Text, _lifetime.Token);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void RefreshKeyboardRecovery()
    {
        ImeRecoveryText.Visibility = _keyboard.HasRecovery(_device.Serial) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UnicodeSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_unicodeWindow is not null) { _unicodeWindow.Activate(); return; }
        _unicodeWindow = new UnicodeSetupWindow(_keyboard, _device.Serial, AndroidTextBox.Text,
            !UsesLegacyBackend && _scrcpySession is { HasExited: false }) { Owner = Window.GetWindow(this) };
        _unicodeWindow.Closed += (_, _) => { _unicodeWindow = null; RefreshKeyboardRecovery(); };
        _unicodeWindow.Show();
    }

    private async void AudioRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _audioBusy) return;
        _audioBusy = true; AudioRoutesButton.IsEnabled = false; AudioRoutesButton.Content = "Reading audio…";
        StatusText.Text = "Reading audio configuration and Android diagnostics…";
        try
        {
            var report = await _advanced.GetAudioRouteReportAsync(_device, _settings.EnableAudio, _settings.KeepDeviceAudio, _lifetime.Token);
            if (_disposed) return;
            var window = new TextReportWindow("Audio routing diagnostics", report) { Owner = Window.GetWindow(this) };
            window.Show();
            StatusText.Text = "Audio report opened. Exit codes describe the query; playback destination remains unverified.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _audioBusy = false; if (!_disposed) { AudioRoutesButton.IsEnabled = true; AudioRoutesButton.Content = "Audio routes"; } }
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
        RotateButton.Content = automatic ? "Switch orientation" : "Rotating…";
        if (automatic) AutoRotateButton.Content = "Enabling…";
        StatusText.Text = automatic ? "Enabling Android sensor rotation…" : "Requesting Android orientation change…";
        try
        {
            // Orientation changes always return to Fit, including from a cropped 1:1 view.
            ApplyZoom(1);
            if (automatic)
            {
                await _bridge.EnableAutoRotationAsync(_device.Serial, _lifetime.Token);
                StatusText.Text = "Auto-rotate enabled. Tilt the physical device to rotate; the view is fitted. Some ROMs suspend sensors while the screen is off.";
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
                    ? $"Android display orientation verified at {degrees}° · view fitted."
                    : $"Android requested {degrees}°, but actual display did not rotate. Foreground app or OEM rotation policy may be locking it.";
            }
            if (_minicapSession is not null)
                await RestartAsync(); // minicap's capture projection is set when its stream starts.
            _refreshRequested = true;
            await RefreshRotationStateAsync();
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
                RotateButton.Content = "Switch orientation";
                AutoRotateButton.IsEnabled = true;
                if (AutoRotateButton.Content?.ToString() == "Enabling…") AutoRotateButton.Content = "Auto rotate · Unknown";
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
        if (!UsesLegacyBackend || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or PasswordBox)
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
        if (!UsesLegacyBackend || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or PasswordBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            return;

        e.Handled = true;
        try
        {
            await _inputGate.WaitAsync(_lifetime.Token);
            try { await _bridge.InputTextAsync(_device.Serial, e.Text, _lifetime.Token); }
            finally { _inputGate.Release(); }
            _refreshRequested = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    public void Dispose()
    {
        ++_focusRequestVersion;
        if (_focusWindow is not null) _focusWindow.Activated -= FocusWindow_Activated;
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        StopModernSession();
        StopLegacySession();
        GC.SuppressFinalize(this);
    }
}
