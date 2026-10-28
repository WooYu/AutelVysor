using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutelVysor.Models;

internal static partial class Program
{
    private static int? TryLogUiAdb(string[] args)
    {
        if (!args.Contains("log-fixture") || !args.Contains("logcat")) return null;
        Console.WriteLine("10-22 00:15:30.123 1234 1234 D Demo: first");
        Console.WriteLine("10-22 00:15:30.124 1234 1234 E Demo: failure");
        Console.Out.Flush();
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!File.Exists(State("release-logcat")) && DateTime.UtcNow < until) Thread.Sleep(20);
        Console.Error.WriteLine("fixture stream ended");
        return 7;
    }

    private static async Task ImportedLogFilters()
    {
        var path = State("imported.log");
        File.WriteAllText(path, "10-22 00:15:30.123 1234 5678 D Demo: verbose detail\n10-22 00:15:30.124 1234 5678 E Demo: 中文 failure\n    at Sample.run(Sample.java:12)\nW/Other(4321): other warning\n");
        var window = DebugWindow();
        try
        {
            await (Task)DebugCall(window, "LoadLogFileAsync", path)!;
            var rows = (ListBox)window.FindName("LogLines");
            Check(rows.Items.Count == 3, "Import did not retain records and associate continuation");
            ((ComboBox)window.FindName("LogLevelBox")).SelectedIndex = 5;
            DebugCall(window, "FlushLiveLog");
            Check(rows.Items.Count == 1, "Changing minimum level did not re-filter imported data");
            var errorRow = rows.Items[0];
            Check(errorRow.GetType().GetProperty("Color")!.GetValue(errorRow)?.ToString() == "#D9A6A2", "Error row has no severity color");
            ((TextBox)window.FindName("LogFilterBox")).Text = "Sample.java";
            ((TextBox)window.FindName("LogTagBox")).Text = "Demo";
            DebugCall(window, "FlushLiveLog");
            Check(rows.Items.Count == 1, "Text/Tag/level filters failed on continuation");
            var all = (string)DebugCall(window, "ExportLogText", false)!;
            var filtered = (string)DebugCall(window, "ExportLogText", true)!;
            Check(all.Contains("verbose detail") && all.Contains("other warning"), "All export lost filtered-out records");
            Check(filtered.Contains("中文 failure") && filtered.Contains("Sample.java") && !filtered.Contains("verbose detail"), "Filtered export disagrees with the view");
            ((TextBox)window.FindName("LogFilterBox")).Text = "does-not-match"; DebugCall(window, "FlushLiveLog");
            Check(rows.Items.Count == 0 && ((TextBlock)window.FindName("LogCountText")).Text.Contains("0/3"), "Empty filters have no visible result count");
            ((TextBox)window.FindName("LogFilterBox")).Clear(); DebugCall(window, "FlushLiveLog");
            DependencyObject? current = rows;
            while (current is not null && current is not TabItem) current = LogicalTreeHelper.GetParent(current);
            if (current is TabItem tab) tab.IsSelected = true;
            window.Show(); await Task.Delay(100); window.UpdateLayout();
            var folder = Path.GetFullPath("test-results"); Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(folder, "log-import.png")); encoder.Save(image);
        }
        finally { window.Close(); }
    }

    private static async Task LiveLogControlState()
    {
        File.Delete(State("release-logcat")); File.WriteAllText(State("calls"), "");
        var window = new AutelVysor.DeviceDebugWindow(Bridge(), new AndroidDevice("log-fixture", "device", "Test", "", "", "", "", 34));
        try
        {
            DebugCall(window, "StartLiveLog_Click", window, new RoutedEventArgs());
            var start = (Button)window.FindName("StartLiveLogButton"); var stop = (Button)window.FindName("StopLiveLogButton");
            Check(!start.IsEnabled && stop.IsEnabled, "Starting live did not disable duplicate starts");
            DebugCall(window, "StartLiveLog_Click", window, new RoutedEventArgs());
            await DebugWaitUntil(() => ((ListBox)window.FindName("LogLines")).Items.Count >= 2);
            Check(File.ReadAllLines(State("calls")).Count(line => line.Contains("log-fixture logcat")) == 1, "Duplicate click started another process");
            File.WriteAllText(State("release-logcat"), "1");
            await DebugWaitUntil(() => start.IsEnabled);
            Check(!stop.IsEnabled && ((TextBlock)window.FindName("StatusText")).Text.Contains("exit code 7"), "EOF did not restore buttons / expose actual exit status");
            DebugCall(window, "StartLiveLog_Click", window, new RoutedEventArgs());
            DebugCall(window, "StopLiveLog_Click", window, new RoutedEventArgs());
            Check(start.IsEnabled && !stop.IsEnabled, "Manual stop did not restore controls");
        }
        finally { File.WriteAllText(State("release-logcat"), "1"); window.Close(); }
    }

    private static async Task AudioReportLayout()
    {
        var report = string.Join('\n', Enumerable.Repeat("Audio policy (configured, not a playback test) / Exit code: 0 / stdout / stderr", 500));
        var window = new AutelVysor.TextReportWindow("Audio report", report) { Width = 500, ShowInTaskbar = false };
        try
        {
            window.Show(); await Task.Delay(50); window.UpdateLayout();
            var root = (DockPanel)window.Content; var text = root.Children.OfType<TextBox>().Single();
            Check(text.IsReadOnly && text.Text == report && text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Audio report lost copyable scrollable evidence");
            var actions = root.Children.OfType<Grid>().Single();
            var label = actions.Children.OfType<TextBlock>().Single();
            var bounds = label.TransformToAncestor(actions).TransformBounds(new System.Windows.Rect(new Point(), label.RenderSize));
            Check(bounds.Right <= actions.ActualWidth + 1 && bounds.Left >= 0 && label.ActualHeight > 20,
                "Audio report explanation does not wrap within the minimum window width");
            Check(window.ActualHeight <= SystemParameters.WorkArea.Height + 2, "Diagnostic report exceeds the screen height");
        }
        finally { window.Close(); }
    }

    private static async Task MirrorActionFeedback()
    {
        using var view = new AutelVysor.Controls.MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice());
        object? Call(string method, params object[] args) => view.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, args);
        Check(!((Button)view.FindName("FocusKeyboardButton")).Focusable, "Focus recovery button takes keyboard focus");
        Call("FocusMirror_Click", view, new RoutedEventArgs()); await Task.Delay(100);
        Check(((Button)view.FindName("FocusKeyboardButton")).Content.ToString() == "Focus failed", "Unattached keyboard focus was reported as successful/no feedback");
        File.WriteAllText(State("sensor"), "1"); File.WriteAllText(State("rotation"), "0");
        await (Task)Call("RefreshRotationStateAsync")!;
        Check(((Button)view.FindName("AutoRotateButton")).Content.ToString()!.Contains("On"), "Sensor enabled policy is not visible on its button");
        File.WriteAllText(State("sensor"), "0"); await (Task)Call("RefreshRotationStateAsync")!;
        Check(((Button)view.FindName("AutoRotateButton")).Content.ToString()!.Contains("Off"), "Sensor locked policy is not visible on its button");
    }
}
