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
        public string Display => $"{Device.DisplayName}\n{Device.Serial} · {Device.State} · {Device.Description}";
    }

    private readonly string _tools = Path.Combine(AppContext.BaseDirectory, "tools");
    private readonly AndroidBridge _bridge;
    private readonly ScrcpyService _scrcpy;
    private readonly LegacyMinicapService _minicap;
    private readonly LegacyMinicapService _minicap;
    private readonly DispatcherTimer _monitorTimer;
    private readonly AppSettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly Dictionary<string, (TabItem Tab, MirrorSessionControl View)> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownWireless = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AndroidDevice> _devices = Array.Empty<AndroidDevice>();
    private bool _refreshing;

    private string Adb => Path.Combine(_tools, "adb.exe");
    private string Scrcpy => Path.Combine(_tools, "scrcpy.exe");

    public MainWindow()
    {
        InitializeComponent();
        _bridge = new AndroidBridge(Adb);
        _settings = _settingsService.Load();
        _scrcpy = new ScrcpyService(Scrcpy, _settings);
        _minicap = new LegacyMinicapService(Adb, _tools);
        foreach (var endpoint in _settings.WirelessEndpoints.Where(x => !string.IsNullOrWhiteSpace(x)))
            _knownWireless.Add(endpoint);
        AutoReconnectBox.IsChecked = _settings.AutoReconnectWireless;
        AudioBox.IsChecked = _settings.EnableAudio;
        ClipboardBox.IsChecked = _settings.ClipboardSync;
        LegacyAccelerationBox.IsChecked = _settings.PreferLegacyAcceleration;
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
        ClipboardBox.Checked += (_, _) => PersistSettings();
        ClipboardBox.Unchecked += (_, _) => PersistSettings();
        LegacyAccelerationBox.Checked += (_, _) => PersistSettings();
        LegacyAccelerationBox.Unchecked += (_, _) => PersistSettings();
        MirrorQualityBox.SelectionChanged += (_, _) => PersistSettings();
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _monitorTimer.Tick += async (_, _) => await RefreshAsync(quiet: true);

        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _monitorTimer.Start();
        };
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

            var selectedSerials = DeviceList.SelectedItems.Cast<DeviceItem>().Select(x => x.Device.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var devices = await _bridge.ListDevicesAsync();

            if (AutoReconnectBox.IsChecked == true)
            {
                var onlineSerials = devices.Where(x => x.IsOnline).Select(x => x.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var address in _knownWireless.Where(x => !onlineSerials.Contains(x)).ToArray())
                {
                    if (_lastReconnectAttempt.TryGetValue(address, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(7))
                        continue;
                    _lastReconnectAttempt[address] = DateTime.UtcNow;
                    try { await _bridge.ConnectAsync(address); } catch { }
                }
                devices = await _bridge.ListDevicesAsync();
            }

            _devices = devices;
            var items = devices.Select(d => new DeviceItem(d)).ToArray();
            DeviceList.ItemsSource = items;
            foreach (var item in items.Where(x => selectedSerials.Contains(x.Device.Serial)))
                DeviceList.SelectedItems.Add(item);

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
        if (device.ApiLevel >= 21 || device.ApiLevel == 0)
            Require(Scrcpy);

        if (_sessions.TryGetValue(device.Serial, out var existing))
        {
            SessionTabs.SelectedItem = existing.Tab;
            return;
        }

        var scrcpy = File.Exists(Scrcpy) ? _scrcpy : null;
        var view = new MirrorSessionControl(_bridge, scrcpy, _minicap, _settings, device);
        var tab = new TabItem
        {
            Header = device.DisplayName,
            Content = view
        };
        _sessions[device.Serial] = (tab, view);
        SessionTabs.Items.Add(tab);
        SessionTabs.SelectedItem = tab;
        EmptyWorkspace.Visibility = Visibility.Collapsed;
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
            var device = SelectedOne();
            if (device.ApiLevel is >= 19 and < 21)
                throw new InvalidOperationException("Continuous MP4 recording is not available in the Android 4.4 compatibility backend yet. Screenshot capture is supported.");

            Require(Scrcpy);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"autel-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            _scrcpy.StartRecording(device, path);
            StatusText.Text = $"Background recording started: {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Autel Vysor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PersistSettings()
    {
        _settings.AutoReconnectWireless = AutoReconnectBox.IsChecked == true;
        _settings.EnableAudio = AudioBox.IsChecked == true;
        _settings.ClipboardSync = ClipboardBox.IsChecked == true;
        _settings.PreferLegacyAcceleration = LegacyAccelerationBox.IsChecked == true;
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
        StatusText.Text = $"Mirror settings applied · {_settings.MirrorQuality} · audio {(_settings.EnableAudio ? "on" : "off")} · clipboard {(_settings.ClipboardSync ? "on" : "off")}.";
    });

    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Require(Adb);
        var device = SelectedOne();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"autel-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await _bridge.CaptureScreenshotAsync(device.Serial, path);
        StatusText.Text = $"Saved: {path}";
    });
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
        report.AppendLine($"Clipboard sync: {_settings.ClipboardSync}");
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
