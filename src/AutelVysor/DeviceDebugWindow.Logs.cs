using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AutelVysor.Services;
using Microsoft.Win32;

namespace AutelVysor;

public partial class DeviceDebugWindow
{
    private const int MaxLiveLogBatchChars = 32_768;
    private const int MaxLiveLogLineChars = 16_000;
    private LogcatDocument _logDocument = new();
    private bool _logUiReady, _logFiltersDirty = true;
    private int _logSourceVersion;
    private long _queuedLogCharacters, _droppedQueuedLines, _truncatedQueuedLines;
    private sealed record QueuedLogLine(int Version, string Text);
    private string _logSource = "No source";
    private CancellationTokenSource? _logLoadCancellation;
    private IReadOnlyList<LogRow> _visibleLogRows = Array.Empty<LogRow>();
    private sealed record LogRow(string Text, string Color);

    private void InitializeLogUi()
    {
        _logUiReady = true;
        LogFilterBox.TextChanged += LogFilterChanged;
        LogTagBox.TextChanged += LogFilterChanged;
        LogPidBox.TextChanged += LogFilterChanged;
        LogLevelBox.SelectionChanged += LogFilterChanged;
        OnlySelectedAppLogBox.Checked += LogFilterChanged;
        OnlySelectedAppLogBox.Unchecked += LogFilterChanged;
        PauseLogBox.Checked += LogFilterChanged;
        PauseLogBox.Unchecked += LogFilterChanged;
        RenderLogView();
    }

    private void LogFilterChanged(object sender, RoutedEventArgs e) { if (_logUiReady) _logFiltersDirty = true; }

    private LogcatFilter CurrentLogFilter()
    {
        var tag = (LogLevelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        LogcatLevel? level = tag switch { "V" => LogcatLevel.Verbose, "D" => LogcatLevel.Debug, "I" => LogcatLevel.Info,
            "W" => LogcatLevel.Warn, "E" => LogcatLevel.Error, "F" => LogcatLevel.Fatal, _ => null };
        int? pid = string.IsNullOrWhiteSpace(LogPidBox.Text) ? null : int.TryParse(LogPidBox.Text.Trim(), out var p) && p > 0 ? p : -1;
        var selectedPid = int.TryParse(_selectedLogPid.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var selected) ? selected : (int?)null;
        return new LogcatFilter { Text = LogFilterBox.Text, Tag = LogTagBox.Text, Pid = pid, MinimumLevel = level,
            OnlySelectedApp = OnlySelectedAppLogBox.IsChecked == true, SelectedAppPid = selectedPid };
    }

    private bool ShouldShowLogLine(string line)
    {
        var single = new LogcatDocument(); single.AppendLine(line);
        return single.GetVisible(CurrentLogFilter()).Count > 0;
    }

    private void RenderLogView()
    {
        if (!_logUiReady || _windowClosed) return;
        _logFiltersDirty = false;
        var filter = CurrentLogFilter();
        _visibleLogRows = _logDocument.GetVisible(filter).Select(record => new LogRow(record.Text, record.Level switch
        {
            LogcatLevel.Verbose => "#94A3B8", LogcatLevel.Debug => "#93C5FD", LogcatLevel.Info => "#86EFAC",
            LogcatLevel.Warn => "#FCD34D", LogcatLevel.Error => "#FCA5A5", LogcatLevel.Fatal => "#F0ABFC", _ => "#CBD5E1"
        })).ToArray();
        LogLines.ItemsSource = _visibleLogRows;
        if (_logcatProcess is not null && PauseLogBox.IsChecked != true && _visibleLogRows.Count > 0)
            LogLines.ScrollIntoView(_visibleLogRows[^1]);
        LogCountText.Text = $"{_logSource} · {_visibleLogRows.Count}/{_logDocument.RetainedRecordCount} records · {_logDocument.RetainedLineCount} retained lines"
            + (filter.OnlySelectedApp && filter.SelectedAppPid is null ? " · select/refresh a running app to resolve its PID" : "")
            + (PauseLogBox.IsChecked == true ? " · view paused; capture continues" : "")
            + (_logDocument.HasRetentionLoss ? $" · retention: {_logDocument.DroppedLineCount} lines dropped, {_logDocument.TruncatedLineCount} lines truncated" : "")
            + (_truncatedQueuedLines > 0 ? $" · {_truncatedQueuedLines} incoming lines truncated" : "")
            + (_droppedQueuedLines > 0 ? $" · {_droppedQueuedLines} queue lines dropped" : "");
    }

    private void SetLogSource(string text, string source)
    {
        _logDocument = new LogcatDocument(); _logDocument.SetSource(text);
        _logSource = source; PauseLogBox.IsChecked = false; RenderLogView();
    }

    private void ClearPendingLogs()
    {
        while (_logQueue.TryDequeue(out var line)) Interlocked.Add(ref _queuedLogCharacters, -line.Text.Length);
    }

    private void EnqueueLogLine(string line) => EnqueueTaggedLine(_logSourceVersion, line);

    private void EnqueueTaggedLine(int version, string line)
    {
        // Bound the cross-thread backlog independently of the parsed document.
        if (line.Length > MaxLiveLogLineChars) { line = line[..(MaxLiveLogLineChars - 12)] + " [truncated]"; Interlocked.Increment(ref _truncatedQueuedLines); }
        Interlocked.Add(ref _queuedLogCharacters, line.Length); _logQueue.Enqueue(new QueuedLogLine(version, line));
        while ((_logQueue.Count > 2_000 || Interlocked.Read(ref _queuedLogCharacters) > 2_000_000) && _logQueue.TryDequeue(out var removed))
        { Interlocked.Add(ref _queuedLogCharacters, -removed.Text.Length); Interlocked.Increment(ref _droppedQueuedLines); }
    }

    private void FlushLiveLog()
    {
        if (_windowClosed) return;
        var count = 0; var characters = 0;
        while (count < 500 && characters <= MaxLiveLogBatchChars - MaxLiveLogLineChars - 1 && _logQueue.TryDequeue(out var line))
        {
            Interlocked.Add(ref _queuedLogCharacters, -line.Text.Length);
            characters += line.Text.Length + 1; count++;
            if (line.Version == _logSourceVersion) _logDocument.AppendLine(line.Text);
        }
        if (_logFiltersDirty || count > 0 && PauseLogBox.IsChecked != true) RenderLogView();
    }

    private void UpdateLiveLogButtons(bool running)
    {
        StartLiveLogButton.IsEnabled = !running;
        StartLiveLogButton.Content = running ? "Live running…" : "Start live";
        StopLiveLogButton.IsEnabled = running;
        SnapshotLogButton.IsEnabled = !running;
    }

    private void StartLiveLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logcatProcess is not null) { StatusText.Text = "Live logcat is already running."; return; }
        try
        {
            _logLoadCancellation?.Cancel(); var version = ++_logSourceVersion; ClearPendingLogs();
            _droppedQueuedLines = _truncatedQueuedLines = 0; SetLogSource("", "Live logcat");
            var process = _bridge.StartLogcat(_device.Serial);
            var errors = new StringBuilder();
            _logcatProcess = process; UpdateLiveLogButtons(true);
            process.OutputDataReceived += (_, args) => { if (args.Data is not null && ReferenceEquals(_logcatProcess, process)) EnqueueTaggedLine(version, args.Data); };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null || !ReferenceEquals(_logcatProcess, process)) return;
                lock (errors) { if (errors.Length < 4_000) errors.AppendLine(args.Data); }
                EnqueueTaggedLine(version, "[adb] " + args.Data);
            };
            process.Exited += async (_, _) => await FinishLiveLogAsync(process, errors);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            StatusText.Text = "Starting live logcat · waiting for records…";
            if (process.HasExited) _ = FinishLiveLogAsync(process, errors);
        }
        catch (Exception ex) { StopLiveLog(); StatusText.Text = "Live logcat failed: " + ex.Message; }
    }

    private async Task FinishLiveLogAsync(Process process, StringBuilder errors)
    {
        try
        {
            await process.WaitForExitAsync();
            var exit = process.ExitCode;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_logcatProcess, process)) return;
                _logcatProcess = null; UpdateLiveLogButtons(false); FlushLiveLog();
                _logSource = $"Live ended (exit {exit})"; RenderLogView();
                string error; lock (errors) error = errors.ToString().Trim();
                StatusText.Text = $"Live logcat ended · exit code {exit} · {_logDocument.ReceivedLineCount} received lines."
                    + (error.Length > 0 ? " stderr: " + error : "") + (_logDocument.ReceivedLineCount == 0 ? " No records received." : "");
                process.Dispose();
            });
        }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void StopLiveLog_Click(object sender, RoutedEventArgs e)
    {
        if (!StopLiveLog()) return;
        FlushLiveLog(); RenderLogView(); StatusText.Text = "Live capture stopped; retained records remain filterable/exportable.";
    }
    private bool StopLiveLog()
    {
        var process = _logcatProcess;
        if (process is null) { UpdateLiveLogButtons(false); return true; }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            StatusText.Text = "Could not stop logcat: " + ex.Message;
            UpdateLiveLogButtons(true); return false;
        }
        _logcatProcess = null; UpdateLiveLogButtons(false); process.Dispose();
        _logSource = "Stopped live logcat"; return true;
    }

    private async void SnapshotLog_Click(object sender, RoutedEventArgs e)
    {
        if (!StopLiveLog()) return;
        _logLoadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _logLoadCancellation = cancellation;
        var version = ++_logSourceVersion; ClearPendingLogs(); SnapshotLogButton.IsEnabled = false;
        StatusText.Text = "Reading logcat snapshot…";
        try
        {
            var text = await _bridge.GetLogcatSnapshotAsync(_device.Serial, ct: cancellation.Token);
            if (_windowClosed || version != _logSourceVersion) return;
            SetLogSource(text, "Device snapshot"); StatusText.Text = "Snapshot loaded; change filters to reapply to retained records.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_windowClosed && version == _logSourceVersion) StatusText.Text = "Snapshot failed: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_logLoadCancellation, cancellation)) { _logLoadCancellation = null; if (!_windowClosed) SnapshotLogButton.IsEnabled = _logcatProcess is null; }
        }
    }

    private async void ImportLog_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new OpenFileDialog { Title = "Import ADB / logcat log", Filter = "Log files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) await LoadLogFileAsync(dialog.FileName);
    }, showDialog: false);

    private async Task LoadLogFileAsync(string path)
    {
        if (!StopLiveLog()) throw new InvalidOperationException("Stop the live stream before importing a log.");
        ClearPendingLogs(); _logLoadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _logLoadCancellation = cancellation;
        var version = ++_logSourceVersion; StatusText.Text = "Importing log; retaining up to 4,000,000 characters / 10,000 records…";
        try
        {
            var document = await Task.Run(() =>
            {
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return LogcatDocument.ReadFrom(reader, cancellation.Token);
            }, cancellation.Token);
            if (_windowClosed || version != _logSourceVersion) return;
            _logDocument = document; _logSource = "Imported: " + Path.GetFileName(path); _droppedQueuedLines = _truncatedQueuedLines = 0;
            PauseLogBox.IsChecked = false; RenderLogView(); StatusText.Text = "Imported log loaded; all filter conditions combine.";
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_logLoadCancellation, cancellation)) _logLoadCancellation = null; }
    }

    private string ExportLogText(bool filtered) => _logDocument.ExportText(filtered ? CurrentLogFilter() : null);

    private async Task ExportRetainedLogAsync(bool filtered)
    {
        FlushLiveLog();
        var dialog = new SaveFileDialog { Title = filtered ? "Export filtered retained log" : "Export all retained log", FileName = $"autel-logcat-{(filtered ? "filtered" : "all")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var text = ExportLogText(filtered);
        await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false));
        StatusText.Text = $"Exported {(filtered ? "filtered" : "all retained")} log to {dialog.FileName}.";
    }
    private async void ExportLog_Click(object sender, RoutedEventArgs e) => await Guard(() => ExportRetainedLogAsync(false));
    private async void ExportFilteredLog_Click(object sender, RoutedEventArgs e) => await Guard(() => ExportRetainedLogAsync(true));
    private void ClearLogView_Click(object sender, RoutedEventArgs e)
    {
        _logLoadCancellation?.Cancel(); if (_logcatProcess is null) ++_logSourceVersion; ClearPendingLogs(); _logDocument.Clear(); _droppedQueuedLines = _truncatedQueuedLines = 0;
        RenderLogView(); StatusText.Text = "Retained log cleared; live capture continues if running.";
    }
}
