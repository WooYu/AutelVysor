using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutelVysor.Models;

namespace AutelVysor;

public partial class DeviceDebugWindow
{
    private readonly List<string> _remoteHistory = [];
    private int _remoteHistoryIndex = -1;
    private IReadOnlyList<RemoteFileEntry> _remoteEntries = [];
    private bool _fileOperationBusy;

    private void RememberRemotePath(string path)
    {
        if (_remoteHistoryIndex >= 0 && _remoteHistory[_remoteHistoryIndex] == path) return;
        if (_remoteHistoryIndex + 1 < _remoteHistory.Count)
            _remoteHistory.RemoveRange(_remoteHistoryIndex + 1, _remoteHistory.Count - _remoteHistoryIndex - 1);
        _remoteHistory.Add(path);
        _remoteHistoryIndex = _remoteHistory.Count - 1;
    }

    private async void RemoteBack_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
    private async void RemoteForward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);

    private async Task NavigateHistoryAsync(int delta)
    {
        var target = _remoteHistoryIndex + delta;
        if (_filesBusy || _fileOperationBusy || target < 0 || target >= _remoteHistory.Count) return;
        var previous = _remoteHistoryIndex;
        _remoteHistoryIndex = target;
        RemoteFilePathBox.Text = _remoteHistory[target];
        await RefreshRemoteFilesAsync();
        if (!_filesLoaded) _remoteHistoryIndex = previous;
        UpdateRemoteFileActions();
    }

    private async void RemoteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_filesBusy || _fileOperationBusy) return;
        RemoteFilePathBox.Text = ((Button)sender).Tag.ToString();
        await RefreshRemoteFilesAsync();
    }

    private void RemoteFilterChanged(object sender, TextChangedEventArgs e) => ApplyRemoteFilter();
    private void RemoteSortChanged(object sender, SelectionChangedEventArgs e) => ApplyRemoteFilter();

    private void ApplyRemoteFilter()
    {
        if (RemoteFileList is null || RemoteSearchBox is null || !_filesLoaded) return;
        var selected = RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().Select(x => x.FullPath).ToHashSet();
        var query = RemoteSearchBox.Text.Trim();
        var entries = _remoteEntries.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        var foldersFirst = entries.OrderByDescending(x => x.IsDirectory);
        var sorted = RemoteSortBox.SelectedIndex switch
        {
            1 => foldersFirst.ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
            2 => foldersFirst.ThenByDescending(x => x.Size).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            3 => foldersFirst.ThenByDescending(x => x.Modified).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => foldersFirst.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
        var result = sorted.ToArray();
        RemoteFileList.ItemsSource = result;
        foreach (var entry in result.Where(x => selected.Contains(x.FullPath))) RemoteFileList.SelectedItems.Add(entry);
        FileStatusText.Text = $"{result.Length} of {_remoteEntries.Count} item(s) · {RemoteFileList.SelectedItems.Count} selected · {_loadedRemotePath}";
    }

    private async void RemoteFiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (_filesBusy || _fileOperationBusy) return;
        if (e.Key == Key.Enter) { e.Handled = true; await OpenRemoteFolderAsync(); }
        else if (e.Key == Key.F5) { e.Handled = true; await RefreshRemoteFilesAsync(); }
        else if (e.Key == Key.Back) { e.Handled = true; RemoteUp_Click(sender, e); }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; await NavigateHistoryAsync(-1); }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; await NavigateHistoryAsync(1); }
        else if (e.Key == Key.Delete && RemoteDeleteButton.IsEnabled) { e.Handled = true; DeleteRemoteFile_Click(sender, e); }
        else if (e.Key == Key.F2 && RemoteRenameButton.IsEnabled)
        {
            e.Handled = true;
            NewFolderNameBox.Text = ((RemoteFileEntry)RemoteFileList.SelectedItem).Name;
            NewFolderNameBox.Focus(); NewFolderNameBox.SelectAll();
        }
    }

    private void RemoteContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = RemoteFileList.ContextMenu;
        menu.Items.Clear();
        void Add(string title, bool enabled, RoutedEventHandler handler)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += handler; menu.Items.Add(item);
        }
        Add("Open folder", RemoteOpenButton.IsEnabled, RemoteOpen_Click);
        Add("Download…", RemoteDownloadButton.IsEnabled, DownloadRemoteFile_Click);
        Add("Upload…", RemoteUploadButton.IsEnabled, UploadRemoteFile_Click);
        Add("Rename using name field", RemoteRenameButton.IsEnabled, RenameRemoteFile_Click);
        Add("Delete…", RemoteDeleteButton.IsEnabled, DeleteRemoteFile_Click);
        Add("Refresh", RemoteRefreshButton.IsEnabled, RefreshRemoteFiles_Click);
    }

    private async void RenameRemoteFile_Click(object sender, RoutedEventArgs e) => await GuardRemoteFileOperation(async () =>
    {
        if (RemoteFileList.SelectedItems.Count != 1 || RemoteFileList.SelectedItem is not RemoteFileEntry entry) return;
        var name = NewFolderNameBox.Text.Trim();
        ValidateRemoteName(name);
        if (entry.Name == name) { FileStatusText.Text = "Name is unchanged."; return; }
        var destination = NormalizeBrowserPath(_loadedRemotePath + "/" + name);
        await _bridge.RenameRemotePathAsync(_device.Serial, entry.FullPath, destination, _debugLifetime.Token);
        await RefreshRemoteFilesAsync();
        NewFolderNameBox.Clear();
        ReportFileResult($"Renamed {entry.Name} to {name}.");
    });

    private void ReportFileResult(string result)
        => FileStatusText.Text = _filesLoaded ? result : result + " Folder refresh failed: " + FileStatusText.Text;

    private static void ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 80 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("Enter a valid file or folder name in the name field.");
    }
}
