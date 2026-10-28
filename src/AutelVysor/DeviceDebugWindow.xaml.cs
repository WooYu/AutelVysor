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
    private readonly AndroidBridge _bridge;
    private readonly AdvancedAdbService _advanced;
    private readonly ShellFavoritesService _favoritesService = new();
    private readonly List<string> _favorites = [];
    private IReadOnlyList<string> _allPackages = Array.Empty<string>();
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _debugLifetime = new();
    private CancellationTokenSource? _packageLoadCancellation;
    private long _packageLoadVersion;
    private bool _filteringPackages;
    private bool _shellBusy;
    private bool _debugActionBusy;
    private readonly AppNameService _appNamesService;
    private readonly AndroidDevice _device;
    private readonly ConcurrentQueue<QueuedLogLine> _logQueue = new();
    private readonly DispatcherTimer _logFlushTimer;
    private readonly List<string> _shellHistory = [];
    private Process? _logcatProcess;
    private int _shellHistoryIndex;
    private string _selectedLogPid = string.Empty;

    private CancellationTokenSource? _appLookupCancellation;
    private long _appLookupVersion;
    private long _fileLookupVersion;
    private bool _windowClosed;
    private bool _filesBusy;
    private bool _filesLoaded;
    private string _loadedRemotePath = string.Empty;

    public DeviceDebugWindow(AndroidBridge bridge, AndroidDevice device)
    {
        InitializeComponent();
        AutelVysor.Services.DarkWindowChrome.Attach(this);
        _bridge = bridge;
        _appNamesService = new AppNameService(bridge);
        _advanced = new AdvancedAdbService(bridge.ExecutablePath);
        _device = device;
        PackageBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler(PackageInputChanged));
        PackageBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => PackageInputChanged(PackageBox, null!)));
        ClearSelectedAppDetails();
        UpdateRemoteFileActions();
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
        InitializeLogUi();

        Loaded += async (_, _) =>
        {
            await Guard(LoadPackagesAsync, showDialog: false);
            await Guard(RefreshRemoteFilesAsync, showDialog: false);
            await Guard(RefreshDeviceDetailsAsync, showDialog: false);
        };
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _debugLifetime.Cancel();
            _packageLoadCancellation?.Cancel();
            _logLoadCancellation?.Cancel();
            _appLookupVersion++;
            _fileLookupVersion++;
            _appLookupCancellation?.Cancel();
            StopLiveLog();
            _logFlushTimer.Stop();
        };
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;

    private async Task Guard(Func<Task> action, bool showDialog = true)
    {
        if (_windowClosed || _debugActionBusy) return;
        _debugActionBusy = true;
        DebugBusyText.Visibility = Visibility.Visible;
        var actionButton = Keyboard.FocusedElement as System.Windows.Controls.Button;
        if (actionButton is not null && Window.GetWindow(actionButton) != this) actionButton = null;
        if (actionButton is not null) actionButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (showDialog && !_windowClosed)
                MessageBox.Show(this, ex.Message, "Device debug", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _debugActionBusy = false;
            DebugBusyText.Visibility = Visibility.Collapsed;
            if (actionButton is not null) actionButton.IsEnabled = true;
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
        _packageLoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_debugLifetime.Token);
        _packageLoadCancellation = cancellation;
        var version = ++_packageLoadVersion;
        var previous = PackageBox.Text.Trim();
        StatusText.Text = "Loading installed packages…";
        var category = (AppCategoryBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
        var indexingStarted = false;
        try
        {
            var packages = await _advanced.ListPackagesAsync(_device.Serial, category, cancellation.Token);
            if (_windowClosed || version != _packageLoadVersion) return;
            _allPackages = packages;
            ApplyPackageFilter(previous);
            StatusText.Text = $"Loaded {_allPackages.Count} {category.ToLowerInvariant()} package(s).";
            indexingStarted = true;
            _ = LoadAppNamesAsync(packages, version, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (!indexingStarted)
            {
                if (ReferenceEquals(_packageLoadCancellation, cancellation)) _packageLoadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task LoadAppNamesAsync(IReadOnlyList<string> packages, long version, CancellationTokenSource cancellation)
    {
        var found = 0;
        try
        {
            for (var i = 0; i < packages.Count; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var name = await _appNamesService.ReadAsync(_device.Serial, packages[i], cancellation.Token);
                    if (_windowClosed || version != _packageLoadVersion) return;
                    if (!string.IsNullOrWhiteSpace(name)) { _appNames[packages[i]] = name; found++; }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { /* Skip timed out name lookups; continue indexing other apps. */ }
                catch (Exception) { /* Package may disappear, be unreadable or use split resources. Keep package search available. */ }
                if (_windowClosed || version != _packageLoadVersion) return;
                PackageResultsText.Text = $"Reading app names {i + 1}/{packages.Count} · {found} names available";
                if (i % 5 == 0 || i == packages.Count - 1) ApplyPackageFilter();
            }
            PackageResultsText.Text = $"{packages.Count} apps · {found} names loaded. Apps without readable labels remain searchable by package.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_packageLoadCancellation, cancellation)) _packageLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private sealed record AppChoice(string Package, string Name)
    {
        public string Title => string.IsNullOrWhiteSpace(Name) ? Package : $"{Name}  ·  {Package}";
    }

    private void ApplyPackageFilter(string? previouslySelected = null)
    {
        var selection = previouslySelected ?? PackageBox.Text.Trim();
        var search = PackageSearchBox.Text.Trim();
        var filtered = _allPackages
            .Where(name => search.Length == 0 || name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (_appNames.TryGetValue(name, out var label) && label.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        _filteringPackages = true;
        try
        {
        PackageBox.ItemsSource = filtered;
        if (!string.IsNullOrWhiteSpace(selection))
            PackageBox.Text = selection;
        AppSearchResults.ItemsSource = filtered.Select(p => new AppChoice(p, _appNames.GetValueOrDefault(p, ""))).ToArray();
        AppSearchResults.Visibility = search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchMatchText.Text = search.Length > 0 ? $"{filtered.Length} matching app(s). Select a result below." : "Search by app name or package";
        }
        finally { _filteringPackages = false; }
    }

    private void AppSearchResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_filteringPackages && AppSearchResults.SelectedItem is AppChoice app) PackageBox.Text = app.Package;
    }

    private async void AppFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ReferenceEquals(sender, AppCategoryBox))
        {
            try { await LoadPackagesAsync(); }
            catch (Exception ex) { if (!_windowClosed) PackageResultsText.Text = ex.Message; }
        }
        else
            ApplyPackageFilter();
    }

    private static bool IsPackageName(string value)
        => Regex.IsMatch(value, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$");

    private void ClearSelectedAppDetails()
    {
        VersionText.Text = VersionCodeText.Text = PidText.Text = MinSdkText.Text = TargetSdkText.Text = "—";
        FirstInstallText.Text = LastUpdateText.Text = DataDirText.Text = "—";
        PermissionsTextBox.Clear();
        PackageDumpTextBox.Clear();
        AppOpsTextBox.Clear();
        _selectedLogPid = string.Empty;
        _logFiltersDirty = true;
    }

    private async void PackageInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_filteringPackages && !_windowClosed) await LoadSelectedAppAsync(debounce: true);
    }

    private Task RefreshSelectedAppAsync() => LoadSelectedAppAsync(debounce: false);

    private async Task LoadSelectedAppAsync(bool debounce)
    {
        var packageName = PackageBox.Text.Trim();
        var version = ++_appLookupVersion;
        _appLookupCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _appLookupCancellation = cancellation;
        ClearSelectedAppDetails();
        if (!IsPackageName(packageName))
        {
            AppStatusText.Text = "Enter or select a valid Android package name.";
            cancellation.Dispose();
            if (ReferenceEquals(_appLookupCancellation, cancellation)) _appLookupCancellation = null;
            return;
        }
        bool Current() => !_windowClosed && version == _appLookupVersion && PackageBox.Text.Trim() == packageName;
        AppStatusText.Text = debounce ? $"Waiting to read {packageName}…" : $"Reading {packageName}…";
        try
        {
            if (debounce) await Task.Delay(350, cancellation.Token);
            if (!Current()) return;
            AppStatusText.Text = $"Reading {packageName}…";
            var details = await _bridge.GetAppDetailsAsync(_device.Serial, packageName, cancellation.Token);
            if (!Current()) return;
            // dumpsys may exit successfully for an absent package; do not present an empty dump as loaded.
            if (string.IsNullOrWhiteSpace(details.PackageDump) ||
                (string.IsNullOrWhiteSpace(details.Info.VersionName) && string.IsNullOrWhiteSpace(details.Info.VersionCode) &&
                 !details.PackageDump.Contains("Package [" + packageName + "]", StringComparison.Ordinal)))
                throw new InvalidOperationException($"No installed app details were returned for {packageName}.");
            var info = details.Info;
            VersionText.Text = Empty(info.VersionName);
            VersionCodeText.Text = Empty(info.VersionCode);
            PidText.Text = string.IsNullOrWhiteSpace(info.Pid) ? "not running" : info.Pid;
            _selectedLogPid = info.Pid;
            _logFiltersDirty = true;
            MinSdkText.Text = Empty(info.MinSdk);
            TargetSdkText.Text = Empty(info.TargetSdk);
            FirstInstallText.Text = Empty(info.FirstInstallTime);
            LastUpdateText.Text = Empty(info.LastUpdateTime);
            DataDirText.Text = Empty(info.DataDir);
            PermissionsTextBox.Text = details.Permissions;
            PackageDumpTextBox.Text = details.PackageDump;
            AppStatusText.Text = $"Reading AppOps for {packageName}…";
            try
            {
                var appOps = await _advanced.GetAppOpsAsync(_device.Serial, packageName, cancellation.Token);
                if (!Current()) return;
                AppOpsTextBox.Text = appOps;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (!Current()) return;
                AppOpsTextBox.Text = $"AppOps unavailable: {ex.Message}";
                AppStatusText.Text = $"Loaded app details for {packageName}; AppOps unavailable.";
                return;
            }
            AppStatusText.Text = $"Loaded app details for {packageName}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) AppStatusText.Text = $"Unable to read {packageName}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_appLookupCancellation, cancellation)) _appLookupCancellation = null;
            cancellation.Dispose();
        }
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) => await Guard(LoadPackagesAsync);

    private async void RefreshAppInfo_Click(object sender, RoutedEventArgs e) => await Guard(RefreshSelectedAppAsync);

    private async void ForceStopApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        StatusText.Text = $"Stopping {packageName}…";
        await _bridge.ForceStopAppAsync(_device.Serial, packageName);
        StatusText.Text = $"Stopped {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void StartApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        StatusText.Text = $"Starting {packageName}…";
        await _bridge.StartAppAsync(_device.Serial, packageName);
        StatusText.Text = $"Started {packageName}.";
        await RefreshSelectedAppAsync();
    });

    private async void RestartApp_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        StatusText.Text = $"Restarting {packageName}…";
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

        StatusText.Text = $"Clearing data for {packageName}…";
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

        StatusText.Text = $"Uninstalling {packageName}…";
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
        var packageName = SelectedPackageName();
        StatusText.Text = $"Granting permission for {packageName}…";
        StatusText.Text = await _advanced.SetRuntimePermissionAsync(
            _device.Serial, packageName, PermissionNameBox.Text, grant: true);
        await RefreshSelectedAppAsync();
    });

    private async void RevokePermission_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        if (MessageBox.Show(this,
            $"Revoke {PermissionNameBox.Text.Trim()} from {packageName}?\nThe app may lose access to a required feature.",
            "Confirm permission revoke", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        StatusText.Text = $"Revoking permission for {packageName}…";
        StatusText.Text = await _advanced.SetRuntimePermissionAsync(
            _device.Serial, packageName, PermissionNameBox.Text, grant: false);
        await RefreshSelectedAppAsync();
    });

    private async void RefreshAppOps_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var packageName = SelectedPackageName();
        var version = _appLookupVersion;
        AppStatusText.Text = $"Reading AppOps for {packageName}…";
        var result = await _advanced.GetAppOpsAsync(_device.Serial, packageName);
        if (_windowClosed || version != _appLookupVersion || PackageBox.Text.Trim() != packageName) return;
        AppOpsTextBox.Text = result;
        AppStatusText.Text = "AppOps refreshed.";
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
        var version = _appLookupVersion;
        StatusText.Text = $"Setting AppOp for {name}…";
        var result = await _advanced.SetAppOpAsync(_device.Serial, name, op, mode);
        var appOps = await _advanced.GetAppOpsAsync(_device.Serial, name);
        if (_windowClosed || version != _appLookupVersion || PackageBox.Text.Trim() != name) return;
        StatusText.Text = result;
        AppOpsTextBox.Text = appOps;
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

    private static string NormalizeBrowserPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return "/" + string.Join("/", segments);
    }

    private void UpdateRemoteFileActions()
    {
        if (RemoteOpenButton is null) return;
        var selected = RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().ToArray();
        var ready = !_filesBusy && !_fileOperationBusy && _filesLoaded && NormalizeBrowserPath(RemoteFilePathBox.Text) == _loadedRemotePath;
        RemoteOpenButton.IsEnabled = ready && selected.Length == 1 && selected[0].IsDirectory;
        RemoteDownloadButton.IsEnabled = ready && selected.Length > 0;
        RemoteDeleteButton.IsEnabled = ready && selected.Length > 0;
        RemoteRenameButton.IsEnabled = ready && selected.Length == 1;
        RemoteUploadButton.IsEnabled = RemoteNewFolderButton.IsEnabled = ready;
        RemoteUpButton.IsEnabled = !_filesBusy && NormalizeBrowserPath(RemoteFilePathBox.Text) != "/";
        RemoteRootButton.IsEnabled = RemoteRefreshButton.IsEnabled = !_filesBusy;
        RemoteFileList.IsEnabled = !_filesBusy && !_fileOperationBusy;
        RemoteFilePathBox.IsReadOnly = _fileOperationBusy;
        RemoteBackButton.IsEnabled = !_filesBusy && !_fileOperationBusy && _remoteHistoryIndex > 0;
        RemoteForwardButton.IsEnabled = !_filesBusy && !_fileOperationBusy && _remoteHistoryIndex + 1 < _remoteHistory.Count;
        RemoteUpButton.IsEnabled &= !_fileOperationBusy;
        RemoteRootButton.IsEnabled = RemoteRefreshButton.IsEnabled = !_filesBusy && !_fileOperationBusy;
    }

    private void RemoteSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateRemoteFileActions();
        if (_filesLoaded && !_filesBusy && FileStatusText is not null)
            FileStatusText.Text = $"{RemoteFileList.Items.Count} item(s) · {RemoteFileList.SelectedItems.Count} selected · {_loadedRemotePath}";
    }
    private void RemotePathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateRemoteFileActions();

    private async Task RefreshRemoteFilesAsync()
    {
        var path = NormalizeBrowserPath(RemoteFilePathBox.Text);
        var selectedPaths = _filesLoaded && path == _loadedRemotePath
            ? RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().Select(x => x.FullPath).ToHashSet()
            : [];
        var version = ++_fileLookupVersion;
        _filesBusy = true;
        _filesLoaded = false;
        RemoteFileList.ItemsSource = null;
        RemoteFilePathBox.Text = path;
        UpdateRemoteFileActions();
        FileStatusText.Text = $"Reading {path}…";
        try
        {
            var entries = await _bridge.ListRemoteFilesAsync(_device.Serial, path, _debugLifetime.Token);
            if (_windowClosed || version != _fileLookupVersion) return;
            if (NormalizeBrowserPath(RemoteFilePathBox.Text) != path)
            {
                FileStatusText.Text = "Path changed. Press Enter or Refresh to load it.";
                return;
            }
            _remoteEntries = entries;
            _loadedRemotePath = path;
            _filesLoaded = true;
            RememberRemotePath(path);
            ApplyRemoteFilter();
            foreach (var entry in RemoteFileList.Items.Cast<RemoteFileEntry>().Where(x => selectedPaths.Contains(x.FullPath)))
                RemoteFileList.SelectedItems.Add(entry);
        }
        catch (Exception ex)
        {
            if (!_windowClosed && version == _fileLookupVersion) FileStatusText.Text = $"Unable to read {path}: {ex.Message}";
        }
        finally
        {
            if (version == _fileLookupVersion) { _filesBusy = false; UpdateRemoteFileActions(); }
        }
    }

    private async void RefreshRemoteFiles_Click(object sender, RoutedEventArgs e) => await RefreshRemoteFilesAsync();

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        RemoteFilePathBox.Text = NormalizeBrowserPath(RemoteFilePathBox.Text + "/..");
        await RefreshRemoteFilesAsync();
    }

    private async void RemoteRoot_Click(object sender, RoutedEventArgs e)
    {
        RemoteFilePathBox.Text = "/";
        await RefreshRemoteFilesAsync();
    }

    private async void RemotePath_KeyDown(object sender, KeyEventArgs e)
    {
        if (_fileOperationBusy || _filesBusy) return;
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RefreshRemoteFilesAsync();
    }

    private async void RemoteOpen_Click(object sender, RoutedEventArgs e) => await OpenRemoteFolderAsync();

    private async Task OpenRemoteFolderAsync()
    {
        if (_filesBusy || !RemoteOpenButton.IsEnabled || RemoteFileList.SelectedItem is not RemoteFileEntry entry || !entry.IsDirectory) return;
        RemoteFilePathBox.Text = entry.FullPath;
        await RefreshRemoteFilesAsync();
    }

    private async void RemoteFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Empty list space must not open the previously selected directory.
        var row = System.Windows.Controls.ItemsControl.ContainerFromElement(RemoteFileList, e.OriginalSource as DependencyObject);
        if (row is System.Windows.Controls.ListViewItem) await OpenRemoteFolderAsync();
    }

    private async Task GuardRemoteFileOperation(Func<Task> action)
    {
        if (_filesBusy || _fileOperationBusy || !_filesLoaded || NormalizeBrowserPath(RemoteFilePathBox.Text) != _loadedRemotePath) return;
        _fileOperationBusy = true;
        UpdateRemoteFileActions();
        FileStatusText.Text = "Running file operation…";
        try
        {
            await action();
            if (FileStatusText.Text == "Running file operation…") FileStatusText.Text = StatusText.Text;
        }
        catch (Exception ex)
        {
            FileStatusText.Text = $"File operation failed: {ex.Message}";
            StatusText.Text = ex.Message;
        }
        finally { _fileOperationBusy = false; UpdateRemoteFileActions(); }
    }

    private async void UploadRemoteFile_Click(object sender, RoutedEventArgs e) => await GuardRemoteFileOperation(async () =>
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Upload files to {RemoteFilePathBox.Text.Trim()}",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            FileStatusText.Text = "Upload cancelled.";
            return;
        }

        var remoteDir = RemoteFilePathBox.Text.Trim().TrimEnd('/');
        var uploaded = 0;
        foreach (var file in dialog.FileNames)
        {
            var remote = (remoteDir.Length == 0 ? "/" : remoteDir + "/") + System.IO.Path.GetFileName(file);
            StatusText.Text = $"Uploading {System.IO.Path.GetFileName(file)}…";
            if (_remoteEntries.Any(x => x.Name == System.IO.Path.GetFileName(file)) &&
                MessageBox.Show(this, $"Replace {remote}?", "Confirm overwrite", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                continue;
            FileStatusText.Text = StatusText.Text;
            await _bridge.PushAsync(_device.Serial, file, remote, _debugLifetime.Token);
            uploaded++;
        }
        await RefreshRemoteFilesAsync();
        ReportFileResult($"Uploaded {uploaded} of {dialog.FileNames.Length} file(s).");
    });

    private async void DownloadRemoteFile_Click(object sender, RoutedEventArgs e) => await GuardRemoteFileOperation(async () =>
    {
        var entries = RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("Select a file to download.");
        if (entries.Length == 1 && !entries[0].IsDirectory)
        {
            var entry = entries[0];
            var dialog = new SaveFileDialog
            {
                Title = $"Download {entry.Name}",
                FileName = entry.Name,
                Filter = "All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                FileStatusText.Text = "Download cancelled.";
                return;
            }

            FileStatusText.Text = StatusText.Text = $"Downloading {entry.Name}…";
            await _bridge.PullAsync(_device.Serial, entry.FullPath, dialog.FileName, _debugLifetime.Token);
            StatusText.Text = $"Saved to {dialog.FileName}";
            FileStatusText.Text = StatusText.Text;
            return;
        }

        var folder = new OpenFolderDialog { Title = "Download selected files / folders to…" };
        if (folder.ShowDialog(this) != true) { FileStatusText.Text = "Download cancelled."; return; }
        var saved = 0;
        foreach (var entry in entries)
        {
            if (entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name is "." or "..")
                throw new IOException($"Cannot save this Android filename on Windows: {entry.Name}");
            var destination = Path.Combine(folder.FolderName, entry.Name);
            if ((File.Exists(destination) || Directory.Exists(destination)) &&
                MessageBox.Show(this, $"Replace or merge {destination}?", "Confirm download overwrite", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) continue;
            FileStatusText.Text = $"Downloading {entry.Name} ({saved + 1}/{entries.Length})…";
            await _bridge.PullAsync(_device.Serial, entry.FullPath, destination, _debugLifetime.Token);
            saved++;
        }
        FileStatusText.Text = StatusText.Text = $"Downloaded {saved} of {entries.Length} selected item(s) to {folder.FolderName}.";
    });

    private async void CreateRemoteFolder_Click(object sender, RoutedEventArgs e) => await GuardRemoteFileOperation(async () =>
    {
        var name = NewFolderNameBox.Text.Trim();
        ValidateRemoteName(name);
        if (_remoteEntries.Any(x => x.Name == name)) throw new IOException("An item with that name already exists.");

        var parent = RemoteFilePathBox.Text.Trim().TrimEnd('/');
        var path = (parent.Length == 0 ? "/" : parent + "/") + name;
        await _bridge.CreateRemoteDirectoryAsync(_device.Serial, path, _debugLifetime.Token);
        NewFolderNameBox.Clear();
        await RefreshRemoteFilesAsync();
        ReportFileResult($"Created folder: {name}");
    });

    private async void DeleteRemoteFile_Click(object sender, RoutedEventArgs e) => await GuardRemoteFileOperation(async () =>
    {
        var entries = RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("Select at least one file or folder to delete.");

        var answer = MessageBox.Show(
            this,
            $"Delete {entries.Length} selected item(s) from Android?\n\n{string.Join("\n", entries.Take(12).Select(x => x.FullPath))}\n\nThis action cannot be undone.",
            "Confirm remote delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            FileStatusText.Text = "Delete cancelled.";
            return;
        }

        var deleted = 0;
        try
        {
            foreach (var entry in entries)
            {
                FileStatusText.Text = $"Deleting {entry.Name} ({deleted + 1}/{entries.Length})…";
                await _bridge.DeleteRemotePathAsync(_device.Serial, entry.FullPath, _debugLifetime.Token);
                deleted++;
            }
        }
        catch (Exception ex) { throw new IOException($"Deleted {deleted} of {entries.Length} item(s). {ex.Message}", ex); }
        finally { await RefreshRemoteFilesAsync(); }
        ReportFileResult($"Deleted {deleted} item(s).");
    });

    private async Task ExecuteShellCommandAsync()
    {
        if (_shellBusy || _windowClosed) return;
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
        _shellBusy = true;
        RunShellButton.IsEnabled = false;
        try
        {
            var result = await _bridge.RunShellCommandResultAsync(_device.Serial, command, _debugLifetime.Token);
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
        finally { _shellBusy = false; RunShellButton.IsEnabled = true; }
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
