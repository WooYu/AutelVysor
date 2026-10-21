using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutelVysor.Controls;
using AutelVysor.Services;

internal static class Program
{
    private static string StateDirectory => Environment.GetEnvironmentVariable("AUTEL_TEST_ADB_STATE")!;
    private static string State(string file) => Path.Combine(StateDirectory, file);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
            return args[0] == "--native-child" ? NativeChildProcess() : FakeAdb(args);

        var directory = Path.Combine(Path.GetTempPath(), "AutelVysor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AUTEL_TEST_ADB_STATE", directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = 0;
        app.Startup += async (_, _) =>
        {
            foreach (var (name, test) in new (string, Func<Task>)[]
            {
                ("Device metadata is reused until disconnect", CachedMetadata),
                ("Rotation alternates between two orientations", ToggleOrientation),
                ("Auto-rotation releases WM lock and re-enables sensor", AutoRotation),
                ("ASCII text is injected via the actual ADB process boundary", AsciiText),
                ("Embedded child returns to fit after repeated self-resizing", NativeResize),
                ("Keyboard focus reaches an external native-window process", CrossProcessFocus),
                ("Dark selections remain readable without keyboard focus", DarkSelection)
            })
            {
                try { await test(); Console.WriteLine("PASS: " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + "\n" + ex); }
            }
            app.Shutdown(failures == 0 ? 0 : 1);
        };
        try { return app.Run(); }
        finally { Directory.Delete(directory, true); }
    }

    private static AndroidBridge Bridge() => new(Environment.ProcessPath!);

    private static async Task CachedMetadata()
    {
        File.WriteAllText(State("calls"), "");
        var bridge = Bridge();
        var first = await bridge.ListDevicesAsync();
        var second = await bridge.ListDevicesAsync();
        Check(first.Count == 1 && first[0].ApiLevel == 34 && first[0].Abi == "arm64-v8a", "Device enrichment failed");
        Check(second.SequenceEqual(first), "Stable device metadata changed");
        var firstReads = File.ReadAllLines(State("calls")).Count(x => x.Contains("getprop"));
        Check(firstReads == 3, $"Expected 3 property reads across two polls, got {firstReads}");
        File.WriteAllText(State("offline"), "1");
        Check((await bridge.ListDevicesAsync()).Count == 0, "Disconnect was not detected");
        File.Delete(State("offline"));
        await bridge.ListDevicesAsync();
        Check(File.ReadAllLines(State("calls")).Count(x => x.Contains("getprop")) == 6, "Reconnect did not refresh cached metadata");
    }

    private static async Task ToggleOrientation()
    {
        var bridge = Bridge();
        File.WriteAllText(State("rotation"), "0");
        for (var i = 0; i < 6; i++)
        {
            var degrees = await bridge.ToggleDeviceOrientationAsync("test-device");
            Check(degrees == (i % 2 == 0 ? 90 : 0), $"Click {i + 1}: expected 0/90 toggle, got {degrees}");
        }
        File.WriteAllText(State("rotation"), "3");
        Check(await bridge.ToggleDeviceOrientationAsync("test-device") == 0, "Reverse landscape should return to natural orientation");
    }

    private static async Task AutoRotation()
    {
        var bridge = Bridge();
        File.WriteAllText(State("rotation"), "0");
        File.WriteAllText(State("sensor"), "1");
        await bridge.ToggleDeviceOrientationAsync("test-device");
        Check(File.ReadAllText(State("sensor")) == "0", "Manual rotation did not disable the accelerometer before locking orientation");
        Check(File.ReadAllText(State("rotation")) == "1", "Manual orientation did not reach 90 degrees");

        await bridge.EnableAutoRotationAsync("test-device");
        Check(File.ReadAllText(State("sensor")) == "1", "Auto rotate did not enable accelerometer");
        Check(File.ReadAllText(State("wm-mode")) == "free", "WindowManager remained in a fixed rotation mode");
    }

    private static async Task AsciiText()
    {
        var bridge = Bridge();
        File.WriteAllText(State("calls"), "");
        await bridge.InputTextAsync("test-device", "VIN123 ABC");
        Check(File.ReadAllLines(State("calls")).Any(x => x.Contains("shell input text VIN123%sABC", StringComparison.Ordinal)),
            "ADB did not receive the intended ASCII input text");
    }

    private static async Task NativeResize()
    {
        var host = new NativeWindowHost();
        var window = new Window { Width = 700, Height = 500, Content = host, ShowInTaskbar = false };
        var child = IntPtr.Zero;
        try
        {
            window.Show();
            await Task.Delay(150);
            child = CreateWindowEx(0, "STATIC", "Test mirror", 0x90000000U, 0, 0, 200, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check(child != IntPtr.Zero, "Unable to create test native window");
            host.AttachExternalWindow(child);
            window.Activate();
            Check(host.FocusEmbeddedWindow(), "Keyboard focus was not delivered to the embedded native window");
            for (var i = 0; i < 12; i++)
            {
                // Simulates SDL resizing/repositioning itself after a display orientation change.
                MoveWindow(child, 17, 23, i % 2 == 0 ? 960 : 320, i % 2 == 0 ? 320 : 960, true);
                await Task.Delay(250);
                GetClientRect(host.Handle, out var available);
                GetWindowRect(child, out var actual);
                MapWindowPoints(IntPtr.Zero, host.Handle, ref actual, 2);
                Check(actual.Left == 0 && actual.Top == 0 && actual.Right == available.Right && actual.Bottom == available.Bottom,
                    $"Rotation {i + 1}: child remained at {actual.Left},{actual.Top} {actual.Right - actual.Left}x{actual.Bottom - actual.Top}; expected {available.Right}x{available.Bottom}");
            }
        }
        finally
        {
            host.DetachExternalWindow();
            if (child != IntPtr.Zero) DestroyWindow(child);
            window.Close();
        }
    }

    private static async Task CrossProcessFocus()
    {
        var window = new Window { Width = 480, Height = 480, ShowInTaskbar = false };
        var host = new NativeWindowHost();
        window.Content = host;
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = "--native-child",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        try
        {
            child.Start();
            var started = Stopwatch.StartNew();
            while (!File.Exists(State("native-hwnd")) && started.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(50);
            Check(File.Exists(State("native-hwnd")), "Native helper did not create its own Win32 window");
            var hwnd = new IntPtr(long.Parse(File.ReadAllText(State("native-hwnd"))));
            window.Show();
            await Task.Delay(150);
            host.AttachExternalWindow(hwnd);
            window.Activate();
            await Task.Delay(100);
            Check(host.FocusEmbeddedWindow(), "Unable to give keyboard focus to the foreign SDL-style HWND");
        }
        finally
        {
            host.DetachExternalWindow();
            window.Close();
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            try { child.WaitForExit(3000); } catch { }
        }
    }

    private static int NativeChildProcess()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "Autel Vysor native input focus test",
            0x90C00000U, 120, 120, 320, 480,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return 2;
        File.WriteAllText(State("native-hwnd"), hwnd.ToInt64().ToString());
        try
        {
            // The separate process has a real native message loop like SDL.
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { DestroyWindow(hwnd); }
        return 0;
    }

    private static int FakeAdb(string[] args)
    {
        // Real subprocess boundary: exercises ToolRunner and AndroidBridge without a connected phone.
        using (var mutex = new Mutex(false, "AutelVysorRegressionAdb"))
        {
            mutex.WaitOne();
            try { File.AppendAllText(State("calls"), string.Join(" ", args) + "\n"); }
            finally { mutex.ReleaseMutex(); }
        }
        if (args[0] == "devices")
        {
            Console.WriteLine("List of devices attached");
            if (!File.Exists(State("offline"))) Console.WriteLine("test-device device product:test model:Test device:test transport_id:1");
        }
        else if (args.Contains("getprop"))
        {
            Console.WriteLine(args[^1] switch { "ro.build.version.sdk" => "34", "ro.build.version.release" => "14", _ => "arm64-v8a" });
        }
        else if (args.Contains("dumpsys"))
        {
            Console.WriteLine("SurfaceOrientation: " + ReadRotation());
        }
        else if (args.Contains("settings") && args.Contains("get"))
        {
            Console.WriteLine(args.Contains("accelerometer_rotation")
                ? (File.Exists(State("sensor")) ? File.ReadAllText(State("sensor")) : "1")
                : ReadRotation());
        }
        else if (args.Contains("settings") && args.Contains("put"))
        {
            if (args.Contains("accelerometer_rotation"))
                File.WriteAllText(State("sensor"), args[^1]);
            if (args.Contains("user_rotation"))
                File.WriteAllText(State("rotation"), args[^1]);
        }
        else if (args.Contains("wm") && (args.Contains("user-rotation") || args.Contains("set-user-rotation")))
        {
            if (args.Contains("lock"))
            {
                File.WriteAllText(State("rotation"), args[^1]);
                File.WriteAllText(State("wm-mode"), "lock " + args[^1]);
            }
            else if (args.Contains("free"))
            {
                File.WriteAllText(State("wm-mode"), "free");
            }
            else
            {
                Console.WriteLine(File.Exists(State("wm-mode")) ? File.ReadAllText(State("wm-mode")) : "free");
            }
        }
        return 0;
    }

    private static async Task DarkSelection()
    {
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AutelVysor;component/Theme.xaml")
        });
        var window = new AutelVysor.MainWindow { Width = 1180, Height = 780, ShowInTaskbar = false };
        try
        {
            var list = (ListBox)window.FindName("DeviceList");
            list.ItemsSource = new[] { new { Title = "SM S9180", Meta = "Android 16 · API 36 · USB", Serial = "TEST-DEVICE", Status = "Online" } };
            list.SelectedIndex = 0;
            var tabs = (TabControl)window.FindName("SessionTabs");
            var selectedTab = new TabItem { Header = "SM S9180", Content = new TextBlock { Text = "Mirror viewport", Foreground = Brushes.White, Margin = new Thickness(30) } };
            tabs.Items.Add(selectedTab);
            tabs.SelectedItem = selectedTab;
            ((FrameworkElement)window.FindName("EmptyWorkspace")).Visibility = Visibility.Collapsed;
            window.Show();
            tabs.Focus();
            await Task.Delay(150);
            window.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var rowSurface = (Border)item.Template.FindName("Surface", item);
            var tabSurface = (Border)selectedTab.Template.FindName("Surface", selectedTab);
            Check(((SolidColorBrush)rowSurface.Background).Color == (Color)ColorConverter.ConvertFromString("#173454"), "Inactive device selection lost dark background");
            Check(((SolidColorBrush)tabSurface.Background).Color == (Color)ColorConverter.ConvertFromString("#173454"), "Selected tab uses a system light background");
            var output = Path.GetFullPath("test-results");
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "dark-selection.png"));
            png.Save(stream);

            var editable = new ComboBox { IsEditable = true, ItemsSource = new[] { "com.autel.test" }, SelectedIndex = 0 };
            var editorWindow = new Window { Content = editable, Width = 300, Height = 100, ShowInTaskbar = false };
            try
            {
                editorWindow.Show();
                editorWindow.UpdateLayout();
                var editor = editable.Template.FindName("PART_EditableTextBox", editable) as TextBox;
                Check(editor is not null && editor.Visibility == Visibility.Visible && editor.Text == "com.autel.test", "Dark editable dropdown lost its text editor");
                editable.Text = "com.autel.changed";
                Check(editor!.Text == "com.autel.changed", "Editable dropdown text binding stopped working");
            }
            finally { editorWindow.Close(); }
        }
        finally { window.Close(); }
    }

    private static string ReadRotation() => File.Exists(State("rotation")) ? File.ReadAllText(State("rotation")) : "0";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Rect rect, uint points);
    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public WinPoint Point;
        public uint Private;
    }

    [DllImport("user32.dll")] private static extern int GetMessage(out WinMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref WinMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref WinMessage message);

}
