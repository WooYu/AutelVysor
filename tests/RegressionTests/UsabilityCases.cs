using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AutelVysor.Models;

internal static partial class Program
{
    private static async Task DefaultDeviceSelection()
    {
        var window = new AutelVysor.MainWindow();
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var tools = State("main-tools"); Directory.CreateDirectory(tools); File.WriteAllText(Path.Combine(tools, "adb.exe"), "fixture");
            typeof(AutelVysor.MainWindow).GetField("_tools", flags)!.SetValue(window, tools);
            typeof(AutelVysor.MainWindow).GetField("_bridge", flags)!.SetValue(window, Bridge());
            ((HashSet<string>)typeof(AutelVysor.MainWindow).GetField("_knownWireless", flags)!.GetValue(window)!).Clear();
            File.WriteAllText(State("offline-first"), "1");
            await (Task)DebugCallMain(window, "RefreshAsync", false)!;
            var selected = (AndroidDevice)DebugCallMain(window, "SelectedOne")!;
            Check(selected.Serial == "test-device", "Startup did not select the first online device");
            await (Task)DebugCallMain(window, "RefreshAsync", true)!;
            Check(((AndroidDevice)DebugCallMain(window, "SelectedOne")!).Serial == selected.Serial, "Polling lost the selection");
            File.WriteAllText(State("offline"), "1");
            await (Task)DebugCallMain(window, "RefreshAsync", true)!;
            Check(((ListBox)window.FindName("DeviceList")).SelectedItems.Count == 0, "Offline device was auto-selected");
            File.Delete(State("offline"));
            await (Task)DebugCallMain(window, "RefreshAsync", true)!;
            Check(((AndroidDevice)DebugCallMain(window, "SelectedOne")!).Serial == "test-device", "Reconnect did not restore a default selection");
        }
        finally { File.Delete(State("offline-first")); File.Delete(State("offline")); window.Close(); }
    }

    private static string? ReadApkLabel(string path) => (string?)typeof(AutelVysor.MainWindow).Assembly
        .GetType("AutelVysor.Services.ApkLabelReader")!.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [path]);

    private static async Task AppLabelSearch()
    {
        // Minimal compiled Android XML with a UTF-8 string pool and literal application label.
        using var xml = new MemoryStream();
        using var writer = new BinaryWriter(xml, Encoding.UTF8, true);
        writer.Write((ushort)3); writer.Write((ushort)8); writer.Write(0);
        var strings = new[] { "application", "label", "测试工具" };
        using var bytes = new MemoryStream();
        var offsets = new List<int>();
        foreach (var text in strings)
        {
            offsets.Add((int)bytes.Position);
            var encoded = Encoding.UTF8.GetBytes(text);
            bytes.WriteByte((byte)text.Length); bytes.WriteByte((byte)encoded.Length); bytes.Write(encoded); bytes.WriteByte(0);
        }
        while (bytes.Length % 4 != 0) bytes.WriteByte(0);
        writer.Write((ushort)1); writer.Write((ushort)28); writer.Write(28 + 12 + (int)bytes.Length);
        writer.Write(3); writer.Write(0); writer.Write(0x100); writer.Write(40); writer.Write(0);
        foreach (var offset in offsets) writer.Write(offset);
        writer.Write(bytes.ToArray());
        writer.Write((ushort)0x102); writer.Write((ushort)16); writer.Write(56);
        writer.Write(1); writer.Write(uint.MaxValue); // XML node header.
        writer.Write(uint.MaxValue); writer.Write(0); writer.Write((ushort)20); writer.Write((ushort)20);
        writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(uint.MaxValue); writer.Write(1); writer.Write(2);
        writer.Write((ushort)8); writer.Write((byte)0); writer.Write((byte)3); writer.Write(2);
        writer.Seek(4, SeekOrigin.Begin); writer.Write((int)xml.Length); writer.Flush();
        var apk = State("label.apk");
        using (var archive = ZipFile.Open(apk, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("AndroidManifest.xml").Open()) output.Write(xml.ToArray());
        Check(ReadApkLabel(apk) == "测试工具", "APK label decoding lost Chinese characters");

        var window = DebugFixtureWindow();
        try
        {
            PrepareRemoteFileFixture();
            window.Show();
            await DebugWaitUntil(() => ((TextBlock)window.FindName("StatusText")).Text.StartsWith("Device details refreshed"));
            typeof(AutelVysor.DeviceDebugWindow).GetField("_allPackages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new[] { "com.example.alpha", "com.example.beta" });
            var names = (Dictionary<string, string>)typeof(AutelVysor.DeviceDebugWindow).GetField("_appNames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            names["com.example.beta"] = "测试工具";
            ((TextBox)window.FindName("PackageSearchBox")).Text = "测试";
            DebugCall(window, "ApplyPackageFilter", (object?)null!);
            var results = (ListBox)window.FindName("AppSearchResults");
            Check(results.Items.Count == 1 && results.Visibility == Visibility.Visible, "App name search did not show a visible matching result");
            results.SelectedIndex = 0;
            Check(((ComboBox)window.FindName("PackageBox")).Text == "com.example.beta", "App result did not select the package");
            SaveUsabilityRender(window, "app-name-search.png");
            await Task.Delay(20);
            ((TextBox)window.FindName("PackageSearchBox")).Text = "does-not-exist";
            DebugCall(window, "ApplyPackageFilter", (object?)null!);
            Check(results.Items.Count == 0 && ((TextBlock)window.FindName("SearchMatchText")).Text.StartsWith("0 matching"), "Empty app search has no feedback");
        }
        finally { window.Close(); }
    }

    private static async Task RemoteHistoryAndFilter()
    {
        PrepareRemoteFileFixture();
        var window = DebugFixtureWindow();
        try
        {
            window.Show();
            await DebugWaitUntil(() => ((TextBlock)window.FindName("StatusText")).Text.StartsWith("Device details refreshed"));
            ((TabControl)window.FindName("DebugTabs")).SelectedIndex = 3;
            var path = (TextBox)window.FindName("RemoteFilePathBox");
            path.Text = "/one"; await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            path.Text = "/two"; await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            await (Task)DebugCall(window, "NavigateHistoryAsync", -1)!;
            Check(path.Text == "/one", "Back did not restore previous directory");
            await (Task)DebugCall(window, "NavigateHistoryAsync", 1)!;
            Check(path.Text == "/two", "Forward did not restore next directory");
            ((TextBox)window.FindName("RemoteSearchBox")).Text = "file";
            var list = (ListView)window.FindName("RemoteFileList");
            Check(list.Items.Count == 1 && ((RemoteFileEntry)list.Items[0]).Name == "file.txt", "File filter did not limit rows");
            path.Text = "/three"; await (Task)DebugCall(window, "RefreshRemoteFilesAsync")!;
            Check(list.Items.Cast<RemoteFileEntry>().All(x => x.FullPath.StartsWith("/three/")), "Filter retained stale folder entries");
            ((TextBox)window.FindName("RemoteSearchBox")).Clear();
            list.SelectedIndex = 0;
            window.Width = 980;
            window.UpdateLayout();
            var name = (FrameworkElement)window.FindName("NewFolderNameBox");
            var create = (FrameworkElement)window.FindName("RemoteNewFolderButton");
            var rename = (FrameworkElement)window.FindName("RemoteRenameButton");
            var nameBounds = name.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(new Point(), name.RenderSize));
            var createBounds = create.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(new Point(), create.RenderSize));
            var renameBounds = rename.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(new Point(), rename.RenderSize));
            Check(Math.Abs(nameBounds.Bottom - createBounds.Bottom) < 2 && Math.Abs(createBounds.Bottom - renameBounds.Bottom) < 2,
                "File name field and action buttons do not share a baseline");
            Check(nameBounds.Right <= createBounds.Left && createBounds.Right <= renameBounds.Left,
                "File name field overlaps the action buttons");
            SaveUsabilityRender(window, "file-manager.png");
        }
        finally { window.Close(); }
    }

    private static void SaveUsabilityRender(Window window, string name)
    {
        window.UpdateLayout();
        var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        render.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
        var output = Path.GetFullPath("test-results"); Directory.CreateDirectory(output);
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }

    private static async Task RemoteFileShellBoundary()
    {
        // Use the actual POSIX shell and filesystem rather than a mock parsing the desired command.
        var bridge = Bridge();
        var root = State("remote-files").Replace('\\', '/');
        string ShellPath(string value) => OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(value[0]) + value[2..] : value;
        Directory.CreateDirectory(root);
        var source = root + "/a ' 中文;$(touch unexpected).txt";
        var destination = root + "/renamed ' 文件.txt";
        File.WriteAllText(source, "payload");
        await bridge.RenameRemotePathAsync("test-device", ShellPath(source), ShellPath(destination));
        Check(File.Exists(destination) && !File.Exists(source) && !File.Exists(State("unexpected")), "Rename lost shell path quoting");
        File.WriteAllText(source, "keep");
        var rejected = false;
        try { await bridge.RenameRemotePathAsync("test-device", ShellPath(source), ShellPath(destination)); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllText(destination) == "payload", "Rename overwrote an existing file");
        await bridge.DeleteRemotePathAsync("test-device", ShellPath(destination));
        Check(!File.Exists(destination), "Delete lost shell quoting");
        await bridge.CreateRemoteDirectoryAsync("test-device", ShellPath(root + "/new folder ' 中文"));
        Check(Directory.Exists(root + "/new folder ' 中文"), "Create folder lost spaces or punctuation");
    }
}
