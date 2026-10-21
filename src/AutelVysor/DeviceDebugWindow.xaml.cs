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
    private readonly AdvancedAdbService _advanced;
    private readonly ShellFavoritesService _favoritesService = new();
    private readonly List<string> _favorites = [];
    private IReadOnlyList<string> _allPackages = Array.Empty<string>();
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
        _advanced = new AdvancedAdbService(bridge.ExecutablePath);
        _device = device;
        _favorites.AddRange(_favoritesService.Load());
        SavedCommandsBox.ItemsSource = _favorites.ToArray();

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
        var category = (AppCategoryBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
        _allPackages = await _advanced.ListPackagesAsync(_device.Serial, category);
        ApplyPackageFilter(previous);
        StatusText.Text = $"Loaded {_allPackages.Count} {category.ToLowerInvariant()} package(s).";
    }

    private void ApplyPackageFilter(string? previouslySelected = null)
    {
        var selection = previouslySelected ?? PackageBox.Text.Trim();
        var search = PackageSearchBox.Text.Trim();
        var filtered = _allPackages
            .Where(name => search.Length == 0 || name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        PackageBox.ItemsSource = filtered;
        if (!string.IsNullOrWhiteSpace(selection))
            PackageBox.Text = selection;
    }

    private async void AppFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ReferenceEquals(sender, AppCategoryBox))
            await Guard(LoadPackagesAsync, showDialog: false);
        else
            ApplyPackageFilter();
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

    private async void GrantPermission_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        StatusText.Text = await _advanced.SetRuntimePermissionAsync(
            _device.Serial, SelectedPackageName(), PermissionNameBox.Text, grant: true);
        await RefreshSelectedAppAsync();
    });

    private async void RevokePermission_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        if (MessageBox.Show(this,
            $"Revoke {PermissionNameBox.Text.Trim()} from {packageName}?\nThe app may lose access to a required feature.",
            "Confirm permission revoke", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        StatusText.Text = await _advanced.SetRuntimePermissionAsync(
            _device.Serial, packageName, PermissionNameBox.Text, grant: false);
        await RefreshSelectedAppAsync();
    });

    private async void RefreshAppOps_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        AppOpsTextBox.Text = await _advanced.GetAppOpsAsync(_device.Serial, SelectedPackageName());
        StatusText.Text = "AppOps refreshed.";
    });

    private async void SetAppOp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var name = SelectedPackageName();
        var op = AppOpNameBox.Text.Trim();
        var mode = (AppOpModeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "default";
        if (MessageBox.Show(this,
            $"Set {name} appop {op} = {mode}?\nThis changes Android app access policy.",
            "Confirm AppOps change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        StatusText.Text = await _advanced.SetAppOpAsync(_device.Serial, name, op, mode);
        AppOpsTextBox.Text = await _advanced.GetAppOpsAsync(_device.Serial, name);
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

        var pidFilter = LogPidBox.Text.Trim();
        if (pidFilter.Length > 0 && (!match.Success || match.Groups["pid"].Value != pidFilter))
            return false;

        var tagFilter = LogTagBox.Text.Trim();
        if (tagFilter.Length > 0)
        {
            if (!match.Success)
                return false;
            var levelEnd = match.Index + match.Length;
            var remaining = line[levelEnd..];
            var colon = remaining.IndexOf(':');
            var tag = colon < 0 ? remaining.Trim() : remaining[..colon].Trim();
            if (!tag.Contains(tagFilter, StringComparison.OrdinalIgnoreCase))
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

    private async Task RefreshRemoteFilesAsync()
    {
        var path = RemoteFilePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = "/sdcard/Download";

        StatusText.Text = $"Reading {path}…";
        var entries = await _bridge.ListRemoteFilesAsync(_device.Serial, path);
        RemoteFileList.ItemsSource = entries;
        RemoteFilePathBox.Text = path;
        StatusText.Text = $"{entries.Count} item(s) in {path}.";
    }

    private async void RefreshRemoteFiles_Click(object sender, RoutedEventArgs e)
        => await Guard(RefreshRemoteFilesAsync);

    private async void RemoteUp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var path = RemoteFilePathBox.Text.Trim().TrimEnd('/');
        if (path.Length <= 1)
        {
            RemoteFilePathBox.Text = "/";
        }
        else
        {
            var slash = path.LastIndexOf('/');
            RemoteFilePathBox.Text = slash <= 0 ? "/" : path[..slash];
        }
        await RefreshRemoteFilesAsync();
    });

    private async void RemoteFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteFileList.SelectedItem is not RemoteFileEntry entry || !entry.IsDirectory)
            return;

        RemoteFilePathBox.Text = entry.FullPath;
        await Guard(RefreshRemoteFilesAsync);
    }

    private async void UploadRemoteFile_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Upload files to {RemoteFilePathBox.Text.Trim()}",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var remoteDir = RemoteFilePathBox.Text.Trim().TrimEnd('/');
        foreach (var file in dialog.FileNames)
        {
            var remote = (remoteDir.Length == 0 ? "/" : remoteDir + "/") + System.IO.Path.GetFileName(file);
            StatusText.Text = $"Uploading {System.IO.Path.GetFileName(file)}…";
            await _bridge.PushAsync(_device.Serial, file, remote);
        }
        await RefreshRemoteFilesAsync();
    });

    private async void DownloadRemoteFile_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (RemoteFileList.SelectedItem is not RemoteFileEntry entry)
            throw new InvalidOperationException("Select a file to download.");
        if (entry.IsDirectory)
            throw new InvalidOperationException("Folder download is not enabled yet. Open the folder and download files individually.");

        var dialog = new SaveFileDialog
        {
            Title = $"Download {entry.Name}",
            FileName = entry.Name,
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        StatusText.Text = $"Downloading {entry.Name}…";
        await _bridge.PullAsync(_device.Serial, entry.FullPath, dialog.FileName);
        StatusText.Text = $"Saved to {dialog.FileName}";
    });

    private async void CreateRemoteFolder_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var name = NewFolderNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new InvalidOperationException("Enter a valid folder name.");

        var parent = RemoteFilePathBox.Text.Trim().TrimEnd('/');
        var path = (parent.Length == 0 ? "/" : parent + "/") + name;
        await _bridge.CreateRemoteDirectoryAsync(_device.Serial, path);
        NewFolderNameBox.Clear();
        await RefreshRemoteFilesAsync();
    });

    private async void DeleteRemoteFile_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var entries = RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("Select at least one file or folder to delete.");

        var answer = MessageBox.Show(
            this,
            $"Delete {entries.Length} selected item(s) from Android?\n\nThis action cannot be undone.",
            "Confirm remote delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        foreach (var entry in entries)
            await _bridge.DeleteRemotePathAsync(_device.Serial, entry.FullPath);
        await RefreshRemoteFilesAsync();
    });

    private async Task ExecuteShellCommandAsync()
    {
        var command = ShellCommandBox.Text.Trim();
        if (command.Length == 0)
            return;

        _shellHistory.RemoveAll(item => string.Equals(item, command, StringComparison.Ordinal));
        _shellHistory.Add(command);
        if (_shellHistory.Count > 100)
            _shellHistory.RemoveAt(0);
        _shellHistoryIndex = _shellHistory.Count;

        ShellOutputBox.AppendText($"$ {command}{Environment.NewLine}");
        ShellCommandBox.Clear();
        StatusText.Text = "Running shell command…";
        try
        {
            var result = await _bridge.RunShellCommandResultAsync(_device.Serial, command);
            ShellOutputBox.AppendText(result.FormatForDisplay() + Environment.NewLine);
            ShellOutputBox.AppendText(Environment.NewLine);
            ShellOutputBox.ScrollToEnd();
            StatusText.Text = result.Succeeded ? "Shell command completed (exit 0)."
                : result.EffectiveExitCode is int exitCode ? $"Shell command failed (exit {exitCode})."
                : "Remote shell exit status could not be verified.";
        }
        catch (Exception ex)
        {
            ShellOutputBox.AppendText($"[error] {ex.Message}{Environment.NewLine}{Environment.NewLine}");
            ShellOutputBox.ScrollToEnd();
            StatusText.Text = ex.Message;
        }
    }

    private async void RunShell_Click(object sender, RoutedEventArgs e)
        => await ExecuteShellCommandAsync();

    private void ClearShell_Click(object sender, RoutedEventArgs e)
    {
        ShellOutputBox.Clear();
        StatusText.Text = "Shell output cleared.";
    }

    private async void ShellCommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ExecuteShellCommandAsync();
            return;
        }

        if (e.Key == Key.Up && _shellHistory.Count > 0)
        {
            e.Handled = true;
            _shellHistoryIndex = Math.Max(0, _shellHistoryIndex - 1);
            ShellCommandBox.Text = _shellHistory[_shellHistoryIndex];
            ShellCommandBox.CaretIndex = ShellCommandBox.Text.Length;
        }
        else if (e.Key == Key.Down && _shellHistory.Count > 0)
        {
            e.Handled = true;
            _shellHistoryIndex = Math.Min(_shellHistory.Count, _shellHistoryIndex + 1);
            ShellCommandBox.Text = _shellHistoryIndex >= _shellHistory.Count ? string.Empty : _shellHistory[_shellHistoryIndex];
            ShellCommandBox.CaretIndex = ShellCommandBox.Text.Length;
        }
    }

    private async Task RefreshDeviceDetailsAsync()
    {
        StatusText.Text = "Reading device details…";
        DeviceDetailsTextBox.Text = await _bridge.GetDeviceOverviewAsync(_device.Serial);
        StatusText.Text = "Device details refreshed.";
    }

    private async void RefreshDeviceDetails_Click(object sender, RoutedEventArgs e)
        => await Guard(RefreshDeviceDetailsAsync);

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        StatusText.Text = "Collecting network interfaces, routes, DNS and ports…";
        NetworkTextBox.Text = await _advanced.GetNetworkReportAsync(_device.Serial);
        StatusText.Text = "Network diagnostics ready.";
    });

    private async void PingHost_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var host = PingHostBox.Text.Trim();
        StatusText.Text = $"Pinging {host}…";
        NetworkTextBox.AppendText(Environment.NewLine + "=== PING " + host + " ===" + Environment.NewLine);
        NetworkTextBox.AppendText(await _advanced.PingAsync(_device.Serial, host));
        NetworkTextBox.ScrollToEnd();
        StatusText.Text = "Ping finished.";
    });

    private async void ExportBugreport_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new SaveFileDialog
        {
            Title = "Select bugreport output folder (file name will be generated)",
            FileName = "autel-bugreport.zip",
            Filter = "ZIP archive (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        StatusText.Text = "Collecting bugreport; this can take several minutes…";
        var file = await _advanced.ExportBugreportAsync(_device.Serial,
            Path.GetDirectoryName(dialog.FileName) ?? AppContext.BaseDirectory);
        StatusText.Text = $"Bugreport saved: {file}";
        MessageBox.Show(this, $"Bugreport exported:\n{file}\n\nCheck for sensitive content before sharing.",
            "Autel Vysor", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    private void RefreshSavedCommands()
    {
        var selected = SavedCommandsBox.SelectedItem as string;
        SavedCommandsBox.ItemsSource = _favorites.ToArray();
        if (selected is not null)
            SavedCommandsBox.SelectedItem = selected;
    }

    private void SavedCommandSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SavedCommandsBox.SelectedItem is not string cmd) return;
        ShellCommandBox.Text = cmd;
        ShellCommandBox.CaretIndex = cmd.Length;
    }

    private void SaveShellFavorite_Click(object sender, RoutedEventArgs e)
    {
        var cmd = ShellCommandBox.Text.Trim();
        if (cmd.Length == 0) { StatusText.Text = "Enter a command to save."; return; }
        if (!_favorites.Contains(cmd, StringComparer.Ordinal)) _favorites.Add(cmd);
        _favoritesService.Save(_favorites);
        RefreshSavedCommands();
        StatusText.Text = "Shell command saved locally.";
    }

    private void RemoveShellFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (SavedCommandsBox.SelectedItem is not string cmd) return;
        _favorites.Remove(cmd);
        _favoritesService.Save(_favorites);
        RefreshSavedCommands();
        StatusText.Text = "Saved command removed.";
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
