using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AutelVysor.Models;
using AutelVysor.Services;

namespace AutelVysor;

public partial class DeviceDebugWindow : Window
{
    private const int MaxLiveLogChars = 500_000;

    private readonly AndroidBridge _bridge;
    private readonly AndroidDevice _device;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _logFlushTimer;
    private readonly List<string> _shellHistory = [];
    private Process? _logcatProcess;
    private int _shellHistoryIndex;
    private string _selectedLogPid = string.Empty;

    public DeviceDebugWindow(AndroidBridge bridge, AndroidDevice device)
    {
        InitializeComponent();
        _bridge = bridge;
        _device = device;

        DeviceNameText.Text = device.DisplayName;
        DeviceMetaText.Text = $"{device.Serial} · Android {Fallback(device.AndroidVersion)} · API {(device.ApiLevel > 0 ? device.ApiLevel : 0)} · {Fallback(device.Abi)} · {device.Connection}";
        DeviceDetailText.Text =
            $"Serial: {device.Serial}{Environment.NewLine}" +
            $"State: {device.State}{Environment.NewLine}" +
            $"Android: {Fallback(device.AndroidVersion)}{Environment.NewLine}" +
            $"API: {(device.ApiLevel > 0 ? device.ApiLevel : 0)}{Environment.NewLine}" +
            $"ABI: {Fallback(device.Abi)}{Environment.NewLine}" +
            $"Model: {Fallback(device.Model)}{Environment.NewLine}" +
            $"Connection: {device.Connection}";

        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _logFlushTimer.Tick += (_, _) => FlushLiveLog();
        _logFlushTimer.Start();

        Loaded += async (_, _) =>
        {
            await Guard(LoadPackagesAsync, showDialog: false);
            await Guard(RefreshRemoteFilesAsync, showDialog: false);
            await Guard(RefreshDeviceDetailsAsync, showDialog: false);
        };
        Closed += (_, _) =>
        {
            StopLiveLog();
            _logFlushTimer.Stop();
        };
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;

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
                MessageBox.Show(this, ex.Message, "Device debug", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string SelectedPackageName()
    {
        var packageName = PackageBox.Text.Trim();
        if (!Regex.IsMatch(packageName, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$"))
            throw new InvalidOperationException("Enter or select a valid Android package name, for example com.autel.example.");
        return packageName;
    }

    private async Task LoadPackagesAsync()
    {
        var previous = PackageBox.Text.Trim();
        StatusText.Text = "Loading installed packages…";
        var packages = await _bridge.ListPackagesAsync(_device.Serial);
        PackageBox.ItemsSource = packages;
        if (!string.IsNullOrWhiteSpace(previous))
            PackageBox.Text = previous;
        StatusText.Text = $"Loaded {packages.Count} package(s).";
    }

    private async Task RefreshSelectedAppAsync()
    {
        var packageName = SelectedPackageName();
        StatusText.Text = $"Reading {packageName}…";

        var infoTask = _bridge.GetAppInfoAsync(_device.Serial, packageName);
        var permissionsTask = _bridge.GetAppPermissionsAsync(_device.Serial, packageName);
        var dumpTask = _bridge.GetPackageDumpAsync(_device.Serial, packageName);
        await Task.WhenAll(infoTask, permissionsTask, dumpTask);

        var info = infoTask.Result;
        VersionText.Text = Empty(info.VersionName);
        VersionCodeText.Text = Empty(info.VersionCode);
        PidText.Text = string.IsNullOrWhiteSpace(info.Pid) ? "not running" : info.Pid;
        _selectedLogPid = info.Pid;
        MinSdkText.Text = Empty(info.MinSdk);
        TargetSdkText.Text = Empty(info.TargetSdk);
        FirstInstallText.Text = Empty(info.FirstInstallTime);
        LastUpdateText.Text = Empty(info.LastUpdateTime);
        DataDirText.Text = Empty(info.DataDir);
        PermissionsTextBox.Text = permissionsTask.Result;
        PackageDumpTextBox.Text = dumpTask.Result;
        StatusText.Text = $"Loaded app details for {packageName}.";
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) => await Guard(LoadPackagesAsync);

    private async void RefreshAppInfo_Click(object sender, RoutedEventArgs e) => await Guard(RefreshSelectedAppAsync);

    private async void ForceStopApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        await _bridge.ForceStopAppAsync(_device.Serial, packageName);
        StatusText.Text = $"Stopped {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void StartApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        await _bridge.StartAppAsync(_device.Serial, packageName);
        StatusText.Text = $"Started {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void RestartApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        await _bridge.RestartAppAsync(_device.Serial, packageName);
        StatusText.Text = $"Restarted {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void ClearAppData_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        var answer = MessageBox.Show(
            this,
            $"Clear ALL application data for:\n\n{packageName}\n\non {_device.DisplayName}?\n\nAccounts, settings, databases, files and cache will be removed. The APK remains installed.",
            "Confirm clear app data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        await _bridge.ClearAppDataAsync(_device.Serial, packageName);
        StatusText.Text = $"Cleared data for {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void InstallApk_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Install APK on {_device.DisplayName}",
            Filter = "Android packages (*.apk)|*.apk"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        StatusText.Text = $"Installing {System.IO.Path.GetFileName(dialog.FileName)}…";
        await _bridge.InstallAsync(_device.Serial, dialog.FileName);
        await LoadPackagesAsync();
        StatusText.Text = "APK installed successfully.";
    });

    private async void UninstallApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        var keepData = KeepDataBox.IsChecked == true;
        var answer = MessageBox.Show(
            this,
            $"Uninstall:\n\n{packageName}\n\nfrom {_device.DisplayName}?\n\n" +
            (keepData ? "Application data will be kept where Android supports -k." : "Application data will also be removed."),
            "Confirm uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        await _bridge.UninstallAppAsync(_device.Serial, packageName, keepData);
        PackageBox.Text = string.Empty;
        VersionText.Text = VersionCodeText.Text = PidText.Text = MinSdkText.Text = TargetSdkText.Text = "—";
        FirstInstallText.Text = LastUpdateText.Text = DataDirText.Text = "—";
        PermissionsTextBox.Clear();
        PackageDumpTextBox.Clear();
        await LoadPackagesAsync();
        StatusText.Text = $"Uninstalled {packageName}.";
    });

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        StatusText.Text = "Reading process list…";
        var processes = await _bridge.GetProcessListAsync(_device.Serial);
        ProcessTextBox.Text = processes;

        var packageName = PackageBox.Text.Trim();
        if (Regex.IsMatch(packageName, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$"))
        {
            var pid = await _bridge.GetPidAsync(_device.Serial, packageName);
            ProcessHintText.Text = string.IsNullOrWhiteSpace(pid)
                ? $"{packageName} · not running"
                : $"{packageName} · PID {pid}";
        }
        else
        {
            ProcessHintText.Text = "All device processes";
        }
        StatusText.Text = "Process list refreshed.";
    });

    private async void SnapshotLog_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        StatusText.Text = "Reading logcat snapshot…";
        var snapshot = await _bridge.GetLogcatSnapshotAsync(_device.Serial);
        LogTextBox.Text = ApplyLogFilters(snapshot);
        LogTextBox.ScrollToEnd();
        StatusText.Text = "Logcat snapshot loaded.";
    });

    private void StartLiveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StopLiveLog();
            while (_logQueue.TryDequeue(out _)) { }

            var process = _bridge.StartLogcat(_device.Serial);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    EnqueueLogLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    EnqueueLogLine("[adb] " + args.Data);
            };
            process.Exited += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_logcatProcess, process))
                    StatusText.Text = "Live logcat stopped.";
            });

            _logcatProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            StatusText.Text = "Live logcat started.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Device debug", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopLiveLog_Click(object sender, RoutedEventArgs e)
    {
        StopLiveLog();
        StatusText.Text = "Live logcat stopped.";
    }

    private void StopLiveLog()
    {
        var process = _logcatProcess;
        _logcatProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.Dispose(); } catch { }
    }

    private void EnqueueLogLine(string line)
    {
        _logQueue.Enqueue(line);
        while (_logQueue.Count > 20_000)
            _logQueue.TryDequeue(out _);
    }

    private void FlushLiveLog()
    {
        if (_logQueue.IsEmpty || PauseLogBox.IsChecked == true)
            return;

        var chunk = new StringBuilder();
        var count = 0;
        while (count < 500 && _logQueue.TryDequeue(out var line))
        {
            if (ShouldShowLogLine(line))
                chunk.AppendLine(line);
            count++;
        }

        if (chunk.Length == 0)
            return;

        LogTextBox.AppendText(chunk.ToString());
        if (LogTextBox.Text.Length > MaxLiveLogChars)
        {
            var keep = LogTextBox.Text[^250_000..];
            LogTextBox.Text = keep;
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
        }
        LogTextBox.ScrollToEnd();
    }

    private bool ShouldShowLogLine(string line)
    {
        var filter = LogFilterBox.Text.Trim();
        if (filter.Length > 0 && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return false;

        var match = Regex.Match(
            line,
            @"^d{2}-d{2}s+d{2}:d{2}:d{2}.d+s+(?<pid>d+)s+(?<tid>d+)s+(?<level>[VDIWEFAS])s+");

        if (OnlySelectedAppLogBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(_selectedLogPid))
                return false;
            if (!match.Success || !string.Equals(match.Groups["pid"].Value, _selectedLogPid, StringComparison.Ordinal))
                return false;
        }

        var selectedLevel = (LogLevelBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "*";
        if (selectedLevel != "*" && match.Success)
        {
            static int Rank(char level) => level switch
            {
                'V' => 0,
                'D' => 1,
                'I' => 2,
                'W' => 3,
                'E' => 4,
                'F' or 'A' => 5,
                _ => 0
            };
            if (Rank(match.Groups["level"].Value[0]) < Rank(selectedLevel[0]))
                return false;
        }

        return true;
    }

    private string ApplyLogFilters(string text)
        => string.Join(
            Environment.NewLine,
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(ShouldShowLogLine));

    private async void ExportLog_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var safeSerial = Regex.Replace(_device.Serial, @"[^A-Za-z0-9_.-]", "_");
        var dialog = new SaveFileDialog
        {
            Title = "Export logcat",
            FileName = $"autel-logcat-{safeSerial}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        StatusText.Text = "Exporting logcat…";
        await _bridge.ExportLogcatAsync(_device.Serial, dialog.FileName);
        StatusText.Text = $"Logcat exported to {dialog.FileName}";
    });

    private void ClearLogView_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        while (_logQueue.TryDequeue(out _)) { }
        StatusText.Text = "Log view cleared.";
    }

    private async void RebootDevice_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var answer = MessageBox.Show(
            this,
            $"Reboot {_device.DisplayName} ({_device.Serial}) now?\n\nThe mirror and ADB connection will disconnect until Android starts again.",
            "Confirm device reboot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        StopLiveLog();
        await _bridge.RebootDeviceAsync(_device.Serial);
        StatusText.Text = "Reboot command sent. This debug window can be closed while the device restarts.";
    });

    private async void RestartAdb_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var answer = MessageBox.Show(
            this,
            "Restart the Windows ADB server?\n\nThis affects ALL connected Android devices and will temporarily interrupt every active Autel Vysor mirror session.",
            "Confirm ADB restart",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        StopLiveLog();
        StatusText.Text = "Restarting ADB server…";
        await _bridge.RestartAdbServerAsync();
        StatusText.Text = "ADB server restarted. The main window will rediscover devices automatically.";
    });
}
