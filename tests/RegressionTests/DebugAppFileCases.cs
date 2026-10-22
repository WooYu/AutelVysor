using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutelVysor.Models;

internal static partial class Program
{
    // Dispatch this after FakeAdb's call recording, before its generic command cases.
    private static int? TryDebugAppFileAdb(string[] args)
    {
        if (!args.Contains("debug-fixture")) return null;
        if (args.Contains("dumpsys") && args.Contains("package"))
        {
            if (args[^1] == "com.test.slow")
            {
                File.WriteAllText(State("debug-slow-started"), "1");
                Thread.Sleep(900);
            }
            if (args[^1] == "com.test.absent") { Console.WriteLine("Unable to find package: com.test.absent"); return 0; }
            Console.WriteLine($"Package [{args[^1]}]\n  versionName={args[^1]}\n  versionCode=12\n  android.permission.CAMERA: granted=true");
            return 0;
        }
        if (args.Contains("pidof")) { Console.WriteLine(args[^1] == "com.test.slow" ? "111" : "222"); return 0; }
        if (args.Contains("ls"))
        {
            if (args[^1] == "/denied") { Console.Error.WriteLine("Permission denied"); return 1; }
            if (args[^1] == "/slow") { File.WriteAllText(State("debug-files-started"), "1"); Thread.Sleep(900); }
            Console.WriteLine("drwxr-xr-x 2 shell shell 4096 2026-10-22 10:00 child\n-rw-r--r-- 1 shell shell 12 2026-10-22 10:00 file.txt");
            return 0;
        }
        return null;
    }

    private static AutelVysor.DeviceDebugWindow DebugFixtureWindow() => new(Bridge(),
        new AndroidDevice("debug-fixture", "device", "Test", "", "", "1", ""));

    private static async Task DebugWaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new InvalidOperationException("Timed out waiting for debug fixture.");
            await Task.Delay(25);
        }
    }

    private static async Task DebugAppLookup()
    {
        var window = DebugFixtureWindow();
        try
        {
            var package = (ComboBox)window.FindName("PackageBox");
            var status = (TextBlock)window.FindName("AppStatusText");
            package.Text = "com.test.first";
            await DebugWaitUntil(() => status.Text.StartsWith("Loaded app details"));
            Check(((TextBlock)window.FindName("VersionText")).Text == "com.test.first", "Typed package did not automatically refresh its summary");
            Check(((TextBox)window.FindName("PermissionsTextBox")).Text.Contains("CAMERA"), "Typed package did not refresh permissions");
            package.Text = "invalid";
            Check(((TextBlock)window.FindName("VersionText")).Text == "—", "Invalid input retained stale summary");
            Check(((TextBox)window.FindName("PermissionsTextBox")).Text.Length == 0, "Invalid input retained stale permissions");
            Check((string)typeof(AutelVysor.DeviceDebugWindow).GetField("_selectedLogPid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == "", "Invalid input retained selected-app PID");
            File.Delete(State("debug-slow-started"));
            package.Text = "com.test.slow";
            var oldLookup = (Task)DebugCall(window, "LoadSelectedAppAsync", false)!;
            await DebugWaitUntil(() => File.Exists(State("debug-slow-started")));
            package.Text = "com.test.latest";
            await (Task)DebugCall(window, "LoadSelectedAppAsync", false)!;
            await oldLookup;
            Check(((TextBlock)window.FindName("VersionText")).Text == "com.test.latest", "Old lookup overwrote the newest app");
            Check(((TextBlock)window.FindName("PidText")).Text == "222", "Old lookup overwrote selected PID");
            package.Text = "com.test.absent";
            await (Task)DebugCall(window, "LoadSelectedAppAsync", false)!;
            Check(status.Text.StartsWith("Unable to read"), "An absent package was reported as loaded");
            Check(((TextBlock)window.FindName("VersionText")).Text == "—", "Failed lookup retained summary");
        }
        finally { window.Close(); }
    }

    private static async Task DebugFileNavigation()
    {
        var window = DebugFixtureWindow();
        try
        {
            var path = (TextBox)window.FindName("RemoteFilePathBox");
            var list = (ListView)window.FindName("RemoteFileList");
            Button Button(string name) => (Button)window.FindName(name);
            path.Text = "/parent/./nested/..";
            await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            Check(path.Text == "/parent", "Remote path was not normalized");
            list.SelectedItem = list.Items.Cast<RemoteFileEntry>().Single(item => item.IsDirectory);
            Check(Button("RemoteOpenButton").IsEnabled && !Button("RemoteDownloadButton").IsEnabled && Button("RemoteDeleteButton").IsEnabled, "Directory action applicability is incorrect");
            await (Task)DebugCall(window, "OpenRemoteFolderAsync")!;
            Check(path.Text == "/parent/child", "Explicit folder navigation failed");
            DebugCall(window, "RemoteUp_Click", window, new RoutedEventArgs());
            await DebugWaitUntil(() => path.Text == "/parent" && Button("RemoteRefreshButton").IsEnabled);
            DebugCall(window, "RemoteRoot_Click", window, new RoutedEventArgs());
            await DebugWaitUntil(() => path.Text == "/" && Button("RemoteRefreshButton").IsEnabled);
            Check(!Button("RemoteUpButton").IsEnabled, "Root still permits parent navigation");
            list.SelectedItem = list.Items.Cast<RemoteFileEntry>().Single(item => !item.IsDirectory);
            Check(Button("RemoteDownloadButton").IsEnabled && !Button("RemoteOpenButton").IsEnabled, "File action applicability is incorrect");
            list.SelectedItems.Add(list.Items.Cast<RemoteFileEntry>().Single(item => item.IsDirectory));
            Check(!Button("RemoteDownloadButton").IsEnabled && Button("RemoteDeleteButton").IsEnabled, "Multiple selections incorrectly permit single-file download");
            path.Text = "/denied";
            Check(!Button("RemoteDeleteButton").IsEnabled, "Edited path retained stale selection actions");
            await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            Check(((TextBlock)window.FindName("FileStatusText")).Text.Contains("Unable to read"), "Directory failure was not visible");
            Check(list.Items.Count == 0 && !Button("RemoteUploadButton").IsEnabled, "Failed directory retained applicable actions");
        }
        finally { window.Close(); }
    }

    private static async Task DebugFileStaleNavigation()
    {
        var window = DebugFixtureWindow();
        try
        {
            var path = (TextBox)window.FindName("RemoteFilePathBox");
            File.Delete(State("debug-files-started"));
            path.Text = "/slow";
            var oldLookup = (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            await DebugWaitUntil(() => File.Exists(State("debug-files-started")));
            Check(!((Button)window.FindName("RemoteRefreshButton")).IsEnabled, "Directory loading did not disable duplicate refresh");
            path.Text = "/new";
            await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            await oldLookup;
            var list = (ListView)window.FindName("RemoteFileList");
            Check(list.Items.Cast<RemoteFileEntry>().All(item => item.FullPath.StartsWith("/new/")), "A stale directory response overwrote the current listing");
            Check(path.Text == "/new", "A stale response rewrote the current path");
        }
        finally { window.Close(); }
    }
    private static Task DebugFileSelectionContrast()
    {
        var window = DebugFixtureWindow();
        try
        {
            var source = (ListView)window.FindName("RemoteFileList");
            var list = new ListView { ItemContainerStyle = source.ItemContainerStyle, View = new GridView() };
            ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            list.Items.Add(new RemoteFileEntry("file.txt", "/file.txt", false, 12, "today"));
            list.Measure(new Size(600, 300));
            list.Arrange(new System.Windows.Rect(0, 0, 600, 300));
            list.UpdateLayout();
            list.SelectedIndex = 0;
            list.UpdateLayout();
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Check(row is not null, "Remote file row was not generated");
            Check(!row!.IsKeyboardFocusWithin, "Fixture unexpectedly focused the row");
            Check(((SolidColorBrush)row.Background).Color == (Color)ColorConverter.ConvertFromString("#1D4ED8"), "Inactive file selection lost its dark-theme blue background");
            Check(((SolidColorBrush)row.Foreground).Color == Colors.White, "Selected filename has insufficient contrast");
            Check(row.Template is not null, "Selection template was not applied");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

}
