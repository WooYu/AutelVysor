using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AutelVysor.Controls;
using AutelVysor.Models;
using AutelVysor.Services;

namespace AutelVysor;

public partial class MainWindow : Window
{
    private sealed record DeviceItem(AndroidDevice Device)
    {
        public string Title => Device.DisplayName;
        public string Meta
        {
            get
            {
                var api = Device.ApiLevel > 0 ? $"API {Device.ApiLevel}" : "API ?";
                var android = string.IsNullOrWhiteSpace(Device.AndroidVersion) ? "Android ?" : $"Android {Device.AndroidVersion}";
                return $"{android} · {api} · {Device.Connection}";
            }
        }
        public string Serial => Device.Serial;
        public string Status => Device.IsOnline ? "Online" : Device.State;
    }

    private readonly string _tools = Path.Combine(AppContext.BaseDirectory, "tools");
    private readonly AndroidBridge _bridge;
    private readonly ScrcpyService _scrcpy;
    private readonly LegacyMinicapService _minicap;
    private readonly DispatcherTimer _monitorTimer;
    private readonly AppSettingsService _settingsService = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly AppSettings _settings;
    private readonly Dictionary<string, (TabItem Tab, MirrorSessionControl View)> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownWireless = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AndroidDevice> _devices = Array.Empty<AndroidDevice>();
    private bool _refreshing;
    private bool _recordingStopBusy;
    private int _recordingGeneration;
    private bool _closeAfterRecordings;
    private bool _sidebarHidden;
    private bool _isFullscreen;
    private bool _fitWindow;
    private bool _autoOpenPending = true;
    private bool _syncingSessionList;
    private double _sidebarWidth = 340;
    private sealed record SessionChoice(TabItem Tab) { public string Title => Tab.Header?.ToString() ?? "Device"; }
    private bool _sidebarHiddenBeforeFullscreen;
    private WindowState _restoreWindowState = WindowState.Normal;

    private string Adb => Path.Combine(_tools, "adb.exe");
    private string Scrcpy => Path.Combine(_tools, "scrcpy.exe");

    public MainWindow()
    {
        InitializeComponent();
        DarkWindowChrome.Attach(this);
        _bridge = new AndroidBridge(Adb);
        _settings = _settingsService.Load();
        _scrcpy = new ScrcpyService(Scrcpy, _settings);
        _minicap = new LegacyMinicapService(Adb, _tools);
        foreach (var endpoint in _settings.WirelessEndpoints.Where(x => !string.IsNullOrWhiteSpace(x)))
            _knownWireless.Add(endpoint);
        AutoReconnectBox.IsChecked = _settings.AutoReconnectWireless;
        AudioBox.IsChecked = _settings.EnableAudio;
        KeepDeviceAudioBox.IsChecked = _settings.KeepDeviceAudio;
        ClipboardBox.IsChecked = _settings.ClipboardSync;
        ScreenOffBox.IsChecked = _settings.TurnScreenOffOnMirror;
        LegacyAccelerationBox.IsChecked = _settings.PreferLegacyAcceleration;
        AutoUpdateCheckBox.IsChecked = _settings.CheckUpdatesOnStart;
        MirrorQualityBox.SelectedIndex = (_settings.MirrorQuality ?? "Standard").ToLowerInvariant() switch
        {
            "smooth" => 0,
            "high" => 2,
            _ => 1
        };
        AutoReconnectBox.Checked += (_, _) => PersistSettings();
        AutoReconnectBox.Unchecked += (_, _) => PersistSettings();
        AudioBox.Checked += (_, _) => PersistSettings();
        AudioBox.Unchecked += (_, _) => PersistSettings();
        KeepDeviceAudioBox.Checked += (_, _) => PersistSettings();
        KeepDeviceAudioBox.Unchecked += (_, _) => PersistSettings();
        ClipboardBox.Checked += (_, _) => PersistSettings();
        ClipboardBox.Unchecked += (_, _) => PersistSettings();
        ScreenOffBox.Checked += (_, _) => PersistSettings();
        ScreenOffBox.Unchecked += (_, _) => PersistSettings();
        LegacyAccelerationBox.Checked += (_, _) => PersistSettings();
        LegacyAccelerationBox.Unchecked += (_, _) => PersistSettings();
        AutoUpdateCheckBox.Checked += (_, _) => PersistSettings();
        AutoUpdateCheckBox.Unchecked += (_, _) => PersistSettings();
        MirrorQualityBox.SelectionChanged += (_, _) => PersistSettings();
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _monitorTimer.Tick += async (_, _) => await RefreshAsync(quiet: true);

        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _monitorTimer.Start();
            if (_settings.CheckUpdatesOnStart)
                _ = CheckUpdatesAsync(interactive: false);
        };
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _monitorTimer.Stop();
            foreach (var (_, view) in _sessions.Values)
                view.Dispose();
            _sessions.Clear();
        };
    }

    private static void Require(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"Missing tool: {file}. Run scripts/prepare-tools.ps1 or see README.md.");
    }

    private AndroidDevice SelectedOne()
    {
        if (DeviceList.SelectedItem is not DeviceItem item)
            throw new InvalidOperationException("Select a device first.");
        ValidateDevice(item.Device);
        return item.Device;
    }

    private IReadOnlyList<AndroidDevice> SelectedMany()
    {
        var devices = DeviceList.SelectedItems.Cast<DeviceItem>().Select(x => x.Device).ToArray();
        if (devices.Length == 0)
            throw new InvalidOperationException("Select at least one device first.");
        foreach (var device in devices)
            ValidateDevice(device);
        return devices;
    }

    private static void ValidateDevice(AndroidDevice device)
    {
        if (!device.IsOnline)
            throw new InvalidOperationException($"{device.Serial} is not authorized/online. Confirm USB debugging on Android.");
        if (device.ApiLevel > 0 && device.ApiLevel < 19)
            throw new InvalidOperationException($"Android API {device.ApiLevel} is unsupported. Autel Vysor requires Android 4.4 / API 19 or newer.");
    }

    private async Task Guard(Func<Task> action, bool showDialog = true)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (showDialog)
                MessageBox.Show(ex.Message, "Autel Vysor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string ValidAddress(string value)
    {
        value = value.Trim();
        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9.\-]+:[0-9]{1,5}$"))
            throw new InvalidOperationException("Enter a valid host:port.");
        var port = int.Parse(value[(value.LastIndexOf(':') + 1)..]);
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Port must be 1-65535.");
        return value;
    }

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            Require(Adb);
            if (!quiet)
                StatusText.Text = "Scanning Android devices and reading OS versions…";

            var devices = await _bridge.ListDevicesAsync();

            if (AutoReconnectBox.IsChecked == true)
            {
                var onlineSerials = devices.Where(x => x.IsOnline).Select(x => x.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var reconnectAttempted = false;
                foreach (var address in _knownWireless.Where(x => !onlineSerials.Contains(x)).ToArray())
                {
                    if (_lastReconnectAttempt.TryGetValue(address, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(7))
                        continue;
                    _lastReconnectAttempt[address] = DateTime.UtcNow;
                    reconnectAttempted = true;
                    try { await _bridge.ConnectAsync(address); } catch { }
                }
                if (reconnectAttempted)
                    devices = await _bridge.ListDevicesAsync();
            }

            if (!_devices.SequenceEqual(devices))
            {
                // Capture selection after awaiting ADB, so a click during the scan is preserved.
                var selectedSerials = DeviceList.SelectedItems.Cast<DeviceItem>().Select(x => x.Device.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var items = devices.Select(d => new DeviceItem(d)).ToArray();
                DeviceList.ItemsSource = items;
                foreach (var item in items.Where(x => selectedSerials.Contains(x.Device.Serial)))
                    DeviceList.SelectedItems.Add(item);
                if (DeviceList.SelectedItems.Count == 0)
                    DeviceList.SelectedItem = items.FirstOrDefault(x => x.Device.IsOnline && (x.Device.ApiLevel == 0 || x.Device.ApiLevel >= 19));
            }
            _devices = devices;

            var online = devices.Where(d => d.IsOnline && (d.ApiLevel == 0 || d.ApiLevel >= 19)).ToArray();
            if (IsLoaded && _autoOpenPending && _sessions.Count == 0 && online.Length == 1)
            {
                _autoOpenPending = false;
                try
                {
                    OpenSession(online[0]);
                    _fitWindow = true;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (_fitWindow && SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl view }) ResizeFitWindow(view);
                    }));
                }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            }

            var map = devices.ToDictionary(x => x.Serial, StringComparer.OrdinalIgnoreCase);
            foreach (var (serial, session) in _sessions.ToArray())
            {
                map.TryGetValue(serial, out var current);
                await session.View.UpdateConnectionAsync(current);
                session.Tab.Header = current is null ? $"{session.View.Device.DisplayName} · offline" : current.DisplayName;
            }

            EmptyWorkspace.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MonitorText.Text = $"Monitor: {DateTime.Now:HH:mm:ss} · {devices.Count} device(s) · {_sessions.Count} session(s)";
            if (!quiet)
            {
                StatusText.Text = devices.Count == 0
                    ? "No devices detected. Enable USB debugging and connect an Android 4.4+ device."
                    : $"{devices.Count} device(s) detected · Android 4.4+ supported";
            }
        }
        catch (Exception ex)
        {
            MonitorText.Text = "Monitor: error";
            if (!quiet)
                StatusText.Text = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Task OpenSessionsAsync(IEnumerable<AndroidDevice> devices)
    {
        foreach (var device in devices)
            OpenSession(device);
        return Task.CompletedTask;
    }

    private void OpenSession(AndroidDevice device)
    {
        ValidateDevice(device);
        _autoOpenPending = false;
        if (device.ApiLevel >= 21 || device.ApiLevel == 0)
            Require(Scrcpy);

        if (_sessions.TryGetValue(device.Serial, out var existing))
        {
            SessionTabs.SelectedItem = existing.Tab;
            existing.View.FocusKeyboardWhenReady();
            return;
        }

        var scrcpy = File.Exists(Scrcpy) ? _scrcpy : null;
        var view = new MirrorSessionControl(_bridge, scrcpy, _minicap, _settings, device);
        view.OrientationChanged += (_, _) =>
        {
            if (_fitWindow && !_isFullscreen && ReferenceEquals(SessionTabs.SelectedItem, _sessions.GetValueOrDefault(view.Serial).Tab))
                ResizeFitWindow(view);
        };
        var tab = new TabItem
        {
            Header = device.DisplayName,
            Content = view
        };
        _sessions[device.Serial] = (tab, view);
        SessionTabs.Items.Add(tab);
        SessionTabs.SelectedItem = tab;
        EmptyWorkspace.Visibility = Visibility.Collapsed;
        DeviceSection.IsExpanded = false;
        StatusText.Text = $"Opened embedded mirror session for {device.DisplayName}.";
    }

    private void CloseCurrentSession()
    {
        if (SessionTabs.SelectedItem is not TabItem selected)
            return;

        var match = _sessions.FirstOrDefault(x => ReferenceEquals(x.Value.Tab, selected));
        if (string.IsNullOrEmpty(match.Key))
            return;

        match.Value.View.Dispose();
        SessionTabs.Items.Remove(match.Value.Tab);
        _sessions.Remove(match.Key);
        EmptyWorkspace.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"Closed mirror session for {match.Key}.";
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SessionTabs)) return;
        SessionToolsHost.Content = null;
        if (SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl view })
        {
            SessionToolsHost.Content = view.DetachToolsForSidebar();
            view.FocusKeyboardWhenReady();
            if (_isFullscreen || _sidebarHidden) view.FitToWindow();
            if (_fitWindow && !_isFullscreen) ResizeFitWindow(view);
        }
        _syncingSessionList = true;
        try
        {
            var choices = SessionTabs.Items.Cast<TabItem>().Select(tab => new SessionChoice(tab)).ToArray();
            SessionList.ItemsSource = choices;
            SessionList.SelectedItem = choices.FirstOrDefault(choice => ReferenceEquals(choice.Tab, SessionTabs.SelectedItem));
        }
        finally { _syncingSessionList = false; }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSessionList && SessionList.SelectedItem is SessionChoice choice)
            SessionTabs.SelectedItem = choice.Tab;
    }

    private void SetSidebarHidden(bool hidden)
    {
        if (hidden && !_sidebarHidden && SidebarColumn.ActualWidth > 44) _sidebarWidth = SidebarColumn.ActualWidth;
        _sidebarHidden = hidden;
        SidebarPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarRecoveryRail.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(hidden ? 44 : _sidebarWidth);
        SidebarSplitterColumn.Width = new GridLength(hidden ? 0 : 5);
        SidebarToggleButton.ToolTip = hidden ? "Show tools" : "Hide tools";
        ExitFullscreenButton.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
        if (hidden && SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl view }) view.FitToWindow();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_fitWindow && !_isFullscreen && SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl active })
                ResizeFitWindow(active);
        }));
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => SetSidebarHidden(!_sidebarHidden);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ResizeFitWindow(MirrorSessionControl view)
    {
        if (WindowState != WindowState.Normal) return;
        var work = SystemParameters.WorkArea;
        var ratio = view.DisplayAspectRatio;
        var chromeHeight = view.ActualHeight > 0 ? Math.Max(40, ActualHeight - view.ActualHeight) : 65;
        var sideWidth = (_sidebarHidden ? 44 : SidebarColumn.ActualWidth) + SidebarSplitterColumn.ActualWidth + 18;
        var availableHeight = Math.Min(800, work.Height);
        var pictureHeight = Math.Min(availableHeight - chromeHeight, (work.Width - sideWidth) / ratio);
        Height = Math.Clamp(pictureHeight + chromeHeight, MinHeight, work.Height);
        Width = Math.Clamp(sideWidth + pictureHeight * ratio, MinWidth, work.Width);
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _sidebarHiddenBeforeFullscreen = _sidebarHidden;
            _isFullscreen = true;
            SetSidebarHidden(true);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _isFullscreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            DarkWindowChrome.Apply(this);
            ResizeMode = ResizeMode.CanResize;
            SetSidebarHidden(_sidebarHiddenBeforeFullscreen);
            WindowState = _restoreWindowState;
            if (_fitWindow && SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl view }) ResizeFitWindow(view);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleFullscreen();
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            e.Handled = true;
            ToggleFullscreen();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Mirror_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await OpenSessionsAsync(SelectedMany());
    });

    private async void OpenAll_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var supported = _devices.Where(x => x.IsOnline && (x.ApiLevel == 0 || x.ApiLevel >= 19)).ToArray();
        if (supported.Length == 0)
            throw new InvalidOperationException("No online Android 4.4+ devices are available.");
        await OpenSessionsAsync(supported);
    });

    private void CloseSession_Click(object sender, RoutedEventArgs e) => CloseCurrentSession();

    private void DeviceDebug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = SelectedOne();
            var window = new DeviceDebugWindow(_bridge, device)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Show();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Autel Vysor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BatchOperations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = SelectedMany();
            var window = new BatchOperationsWindow(_bridge, devices)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Show();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Batch operations", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Captures_Click(object sender, RoutedEventArgs e)
    {
        var window = new CaptureHistoryWindow
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Show();
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        try
        {
            var localVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            var release = await _updateService.CheckAsync(localVersion);
            if (release is null)
            {
                if (interactive)
                    MessageBox.Show(this, $"No newer GitHub release found. Current version: {localVersion}.",
                        "Autel Vysor updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StatusText.Text = $"New version {release.Tag} is available on GitHub Releases.";
            if (!interactive)
                return;

            if (MessageBox.Show(this,
                $"Autel Vysor {release.Tag} is available ({(release.IsPrerelease ? "pre-release" : "release")}).\n\n" +
                "Open the official GitHub Release page?\n" +
                "Automatic installer execution is disabled until code signing is configured.",
                "Autel Vysor updates", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url)
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            if (interactive)
                MessageBox.Show(this, $"Update check failed: {ex.Message}",
                    "Autel Vysor updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckUpdatesAsync(interactive: true);

    private async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await Guard(async () =>
    {
        await OpenSessionsAsync(SelectedMany());
    });

    private async void Connect_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var address = ValidAddress(AddressBox.Text);
        await _bridge.ConnectAsync(address);
        _knownWireless.Add(address);
        _lastReconnectAttempt.Remove(address);
        PersistSettings();
        await RefreshAsync();
    });

    private async void Pair_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var address = ValidAddress(AddressBox.Text);
        var code = PairCodeBox.Text.Trim();
        if (!Regex.IsMatch(code, @"^[0-9]{6}$"))
            throw new InvalidOperationException("Enter the 6-digit Android wireless debugging pairing code.");
        await _bridge.PairAsync(address, code);
        StatusText.Text = "Paired. Enter the Android wireless debugging connection port and click Connect.";
    });

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        if (!device.IsWireless)
            throw new InvalidOperationException("Only TCP/IP devices can be disconnected here.");
        _knownWireless.Remove(device.Serial);
        PersistSettings();
        await _bridge.DisconnectAsync(device.Serial);
        await RefreshAsync();
    });

    private async void TcpIp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        // Read the Wi-Fi address before restarting adbd in TCP/IP mode; some Android 4.4
        // builds immediately drop the USB transport after `adb tcpip`.
        var ip = await _bridge.GetWifiIpAsync(device.Serial);
        await _bridge.TcpIpAsync(device.Serial);
        if (!string.IsNullOrWhiteSpace(ip))
        {
            AddressBox.Text = $"{ip}:5555";
            StatusText.Text = $"TCP/IP ADB enabled. Detected Wi-Fi address {ip}:5555; click Connect after the device switches to TCP/IP mode.";
        }
        else
        {
            StatusText.Text = "TCP/IP ADB enabled on port 5555. Enter the device Wi-Fi IP address and connect.";
        }
    });

    private async void Install_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        var dialog = new OpenFileDialog { Filter = "Android packages (*.apk)|*.apk" };
        if (dialog.ShowDialog(this) != true)
            return;
        StatusText.Text = "Installing APK…";
        await _bridge.InstallAsync(device.Serial, dialog.FileName);
        StatusText.Text = "APK installed successfully.";
    });

    private async void Push_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        var dialog = new OpenFileDialog { Title = "Select a file to send to Android" };
        if (dialog.ShowDialog(this) != true)
            return;
        var remote = "/sdcard/Download/" + Path.GetFileName(dialog.FileName);
        StatusText.Text = "Transferring…";
        await _bridge.PushAsync(device.Serial, dialog.FileName, remote);
        StatusText.Text = $"Sent to {remote}";
    });

    private async void Pull_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        var remote = RemotePathBox.Text.Trim();
        if (!remote.StartsWith("/sdcard/", StringComparison.Ordinal) || remote.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Enter an absolute file path under /sdcard/.");
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(remote) };
        if (dialog.ShowDialog(this) != true)
            return;
        StatusText.Text = "Downloading…";
        await _bridge.PullAsync(device.Serial, remote, dialog.FileName);
        StatusText.Text = $"Saved to {dialog.FileName}";
    });

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = CaptureDevice();
            if (device.ApiLevel is >= 19 and < 21)
                throw new InvalidOperationException("Continuous MP4 recording is not available in the Android 4.4 compatibility backend yet. Screenshot capture is supported.");

            Require(Scrcpy);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"autel-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var process = _scrcpy.StartRecording(device, path);
            var generation = _recordingGeneration;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (_recordingStopBusy || generation != _recordingGeneration) return;
                RecordButton.Content = "Recording ended · check file";
                CaptureStatusText.Text = $"Recorder exited unexpectedly. Close extra mirrors and use Stop recordings to verify: {path}";
            });
            RecordButton.Content = "● Recording";
            RecordButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(118, 81, 74));
            CaptureStatusText.Text = $"Recording {device.DisplayName} → {path}";
            StopRecordingsButton.IsEnabled = true;
            StatusText.Text = $"Recording started: {path}. Use Stop recordings to finalize the MP4.";
            if (process.HasExited)
            {
                RecordButton.Content = "Recording ended · check file";
                CaptureStatusText.Text = $"Recorder exited during startup. Close extra mirrors and use Stop recordings to verify: {path}";
            }
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = ex.Message;
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Autel Vysor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopOwnedRecordingsAsync()
    {
        _recordingStopBusy = true;
        _recordingGeneration++;
        StopRecordingsButton.Content = "Finalizing…";
        CaptureStatusText.Text = "Finalizing MP4 files…";
        RecordButton.IsEnabled = false;
        StopRecordingsButton.IsEnabled = false;
        StatusText.Text = "Stopping recordings and finalizing MP4 files…";
        try
        {
            await _scrcpy.StopRecordingsAsync();
            StatusText.Text = "Recordings stopped and files finalized. Open Captures to view them.";
            CaptureStatusText.Text = StatusText.Text;
        }
        catch (Exception ex) { CaptureStatusText.Text = ex.Message; throw; }
        finally
        {
            _recordingStopBusy = false;
            RecordButton.IsEnabled = true;
            StopRecordingsButton.IsEnabled = _scrcpy.HasRecordings;
            StopRecordingsButton.Content = "Stop recordings";
            RecordButton.Content = _scrcpy.HasRecordings ? "● Recording" : "Record MP4";
            RecordButton.ClearValue(BackgroundProperty);
            if (_scrcpy.HasRecordings)
                RecordButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(118, 81, 74));
        }
    }

    private async void StopRecordings_Click(object sender, RoutedEventArgs e)
        => await Guard(StopOwnedRecordingsAsync);

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterRecordings) return;
        if (_recordingStopBusy) { e.Cancel = true; return; }
        if (!_scrcpy.HasRecordings) return;
        e.Cancel = true;
        _monitorTimer.Stop();
        try { await StopOwnedRecordingsAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Recording finalization", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_scrcpy.HasRecordings)
            {
                _closeAfterRecordings = true;
                Close();
            }
            else
            {
                StatusText.Text = "A recorder has not exited. Retry Stop recordings before closing.";
                _monitorTimer.Start();
            }
        }
    }

    private void PersistSettings()
    {
        _settings.AutoReconnectWireless = AutoReconnectBox.IsChecked == true;
        _settings.EnableAudio = AudioBox.IsChecked == true;
        _settings.KeepDeviceAudio = KeepDeviceAudioBox.IsChecked == true;
        _settings.ClipboardSync = ClipboardBox.IsChecked == true;
        _settings.TurnScreenOffOnMirror = ScreenOffBox.IsChecked == true;
        _settings.PreferLegacyAcceleration = LegacyAccelerationBox.IsChecked == true;
        _settings.CheckUpdatesOnStart = AutoUpdateCheckBox.IsChecked == true;
        _settings.MirrorQuality = MirrorQualityBox.SelectedIndex switch
        {
            0 => "Smooth",
            2 => "High",
            _ => "Standard"
        };
        _settings.WirelessEndpoints = _knownWireless.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        try { _settingsService.Save(_settings); } catch { }
    }

    private async void ApplyMirrorSettings_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        PersistSettings();
        foreach (var (_, view) in _sessions.Values)
            await view.RestartAsync();
        StatusText.Text = $"Mirror settings applied · {_settings.MirrorQuality} · screen {(_settings.TurnScreenOffOnMirror ? "off" : "on")} · audio {(_settings.EnableAudio ? "on" : "off")} · device/Bluetooth {(_settings.KeepDeviceAudio ? "kept" : "PC priority")} · clipboard {(_settings.ClipboardSync ? "on" : "off")}.";
    });

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!ScreenshotButton.IsEnabled) return;
        ScreenshotButton.IsEnabled = false;
        ScreenshotButton.Content = "Capturing…";
        CaptureStatusText.Text = "Taking screenshot…";
        try
        {
            Require(Adb);
            var device = CaptureDevice();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"autel-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await _bridge.CaptureScreenshotAsync(device.Serial, path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Screenshot file is empty or missing.");
            ScreenshotButton.Content = "Screenshot saved ✓";
            CaptureStatusText.Text = StatusText.Text = $"Saved screenshot: {path}. Open Captures to preview.";
        }
        catch (Exception ex)
        {
            ScreenshotButton.Content = "Screenshot failed · retry";
            CaptureStatusText.Text = StatusText.Text = ex.Message;
        }
        finally { ScreenshotButton.IsEnabled = true; }
    }

    private AndroidDevice CaptureDevice()
    {
        var device = SessionTabs.SelectedItem is TabItem { Content: MirrorSessionControl view } ? view.Device : SelectedOne();
        ValidateDevice(device);
        return device;
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var report = new StringBuilder();
        report.AppendLine("Autel Vysor diagnostics");
        report.AppendLine($"App: {typeof(MainWindow).Assembly.GetName().Version}");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        report.AppendLine($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        report.AppendLine($"Base directory: {AppContext.BaseDirectory}");
        report.AppendLine($"Mirror quality: {_settings.MirrorQuality}");
        report.AppendLine($"Audio: {_settings.EnableAudio}");
        report.AppendLine($"Keep device/Bluetooth audio: {_settings.KeepDeviceAudio}");
        report.AppendLine($"Clipboard sync: {_settings.ClipboardSync}");
        report.AppendLine($"Turn device screen off on mirror: {_settings.TurnScreenOffOnMirror}");
        report.AppendLine($"Legacy acceleration preferred: {_settings.PreferLegacyAcceleration}");

        if (File.Exists(Adb))
        {
            try { report.AppendLine("ADB: " + (await _bridge.GetAdbVersionAsync()).Replace(Environment.NewLine, " | ")); }
            catch (Exception ex) { report.AppendLine("ADB: error - " + ex.Message); }
        }
        else
        {
            report.AppendLine("ADB: missing");
        }

        if (File.Exists(Scrcpy))
        {
            try { report.AppendLine("scrcpy: " + (await _scrcpy.GetVersionAsync()).Replace(Environment.NewLine, " | ")); }
            catch (Exception ex) { report.AppendLine("scrcpy: error - " + ex.Message); }
        }
        else
        {
            report.AppendLine("scrcpy: missing");
        }

        report.AppendLine($"Devices: {_devices.Count}");
        foreach (var device in _devices)
        {
            var minicap = _minicap.CanAccelerate(device) ? "minicap-ready" : "minicap-no";
            report.AppendLine($"- {device.Serial} | {device.State} | Android {device.AndroidVersion} | API {device.ApiLevel} | {device.Abi} | {device.Backend} | {minicap}");
        }

        Clipboard.SetText(report.ToString());
        StatusText.Text = "Diagnostics copied to the clipboard.";
    });

}

