using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutelVysor.Controls;
using AutelVysor.Models;
using AutelVysor.Services;

internal static partial class Program
{
    private static string StateDirectory => Environment.GetEnvironmentVariable("AUTEL_TEST_ADB_STATE")!;
    private static string State(string file) => Path.Combine(StateDirectory, file);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-apk")
        {
            Console.WriteLine(ReadApkLabel(args[1]));
            return 0;
        }
        if (args.Length > 0)
            return args[0] == "--native-child" ? NativeChildProcess() : FakeAdb(args);

        var directory = Path.Combine(Path.GetTempPath(), "AutelVysor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AUTEL_TEST_ADB_STATE", directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AutelVysor;component/Theme.xaml")
        });
        var failures = 0;
        app.Startup += async (_, _) =>
        {
            foreach (var (name, test) in new (string, Func<Task>)[]
            {
                ("Device metadata is reused until disconnect", CachedMetadata),
                ("Startup selects the first online device and reconnect selects again", DefaultDeviceSelection),
                ("Logcat combines PID tag text level and selected app filters", LogcatCombinedFilters),
                ("Logcat rejects lower severity and unparsed lines under a level filter", LogcatMinimumSeverity),
                ("Live log rendering has a bounded per-tick text budget", LogcatFlushBudget),
                ("One oversized log line cannot monopolize a UI tick", LogcatOversizedLine),
                ("App details reuse a single package snapshot", SharedPackageSnapshot),
                ("Modern auto keyboard selects hardware with SDK compatibility available", ModernKeyboardMode),
                ("Native mirror zoom pans and restores fit after renderer resize", NativeZoomPan),
                ("Long input errors do not shrink the mirror viewport", CompactMirrorStatus),
                ("Modern toolbar pastes Chinese through native scrcpy shortcut without ADB IME", ModernToolbarPaste),
                ("Clipboard paste refuses an unattached host", PasteRequiresAttachedHost),
                ("Rotation alternates between two orientations", ToggleOrientation),
                ("Auto-rotation releases WM lock and re-enables sensor", AutoRotation),
                ("Shell arguments survive the real remote shell boundary", ShellBoundary),
                ("Rotation resets zoom and supersedes a pending 1:1 view", RotationResetsZoom),
                ("Rotation reads default display instead of stale user setting", ActualDisplayRotation),
                ("Embedded keyboard avoids SDL text-event dependency", EmbeddedKeyboard),
                ("Recording rejects duplicates and finalizes on stop", RecordingLifecycle),
                ("Failed recorder cleanup retains ownership for retry", RecordingCleanupFailure),
                ("ASCII text is injected via the actual ADB process boundary", AsciiText),
                ("Embedded child returns to fit after repeated self-resizing", NativeResize),
                ("Keyboard focus reaches an external native-window process", CrossProcessFocus),
                ("Typed app summaries ignore stale replies", DebugAppLookup),
                ("Application labels decode and search results are directly selectable", AppLabelSearch),
                ("Remote history and filter preserve directory context", RemoteHistoryAndFilter),
                ("File shell paths preserve spaces and punctuation", RemoteFileShellBoundary),
                ("File navigation opens folders and parent/root paths", DebugFileNavigation),
                ("File browser rejects stale navigation replies", DebugFileStaleNavigation),
                ("File selection remains visible in dark UI", DebugFileSelectionContrast),
                ("Imported logs reapply UI filters and show severity colors", ImportedLogFilters),
                ("Live log controls reject duplicate starts and recover on EOF", LiveLogControlState),
                ("Audio reports stay bounded and copyable", AudioReportLayout),
                ("Mirror actions show focus failure and sensor policy", MirrorActionFeedback),
                ("Capture images preview on the right and release files", CapturePreviewLifecycle),
                ("Capture video controls handle selection and codec failure", CaptureVideoLifecycle),
                ("Left tools preserve full-height Fit and fullscreen recovery", LeftToolsLayout),
                ("Dark selections remain readable without keyboard focus", DarkSelection),
                ("Test directory cleanup retries transient Windows locks", TestDirectoryCleanupRetries)
            })
            {
                var filter = Environment.GetEnvironmentVariable("AUTEL_TEST_FILTER");
                if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|').Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                try { await test(); Console.WriteLine("PASS: " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + "\n" + ex); }
            }
            app.Shutdown(failures == 0 ? 0 : 1);
        };
        try { return app.Run(); }
        finally
        {
            DeleteTestDirectoryWithRetry(
                directory,
                Directory.Delete,
                static duration => Thread.Sleep(duration),
                maxAttempts: 20);
        }
    }

    private static Task TestDirectoryCleanupRetries()
    {
        var attempts = 0;
        var delays = 0;
        var deleted = DeleteTestDirectoryWithRetry(
            "fixture",
            (_, _) =>
            {
                attempts++;
                if (attempts < 3)
                    throw new IOException("fixture lock");
            },
            _ => delays++,
            maxAttempts: 3);

        Check(deleted, "Transient directory locks should be retried successfully");
        Check(attempts == 3, $"Expected 3 cleanup attempts, got {attempts}");
        Check(delays == 2, $"Expected 2 retry delays, got {delays}");
        return Task.CompletedTask;
    }

    private static bool DeleteTestDirectoryWithRetry(
        string path,
        Action<string, bool> delete,
        Action<TimeSpan> delay,
        int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                delete(path, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine($"WARNING: Could not remove test directory after {maxAttempts} attempts: {ex.Message}");
                    return false;
                }

                delay(TimeSpan.FromMilliseconds(Math.Min(250, attempt * 25)));
            }
        }

        return false;
    }

    private static AndroidBridge Bridge() => new(Environment.ProcessPath!);

    private static AutelVysor.DeviceDebugWindow DebugWindow() => new(Bridge(), new AndroidDevice("test-device", "device", "Test", "", "", "1", ""));
    private static object? DebugCall(AutelVysor.DeviceDebugWindow window, string method, params object[] args)
        => typeof(AutelVysor.DeviceDebugWindow).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, args);

    private static Task LogcatCombinedFilters()
    {
        var window = DebugWindow();
        try
        {
            ((TextBox)window.FindName("LogPidBox")).Text = "1234";
            ((TextBox)window.FindName("LogTagBox")).Text = "MyTag";
            ((TextBox)window.FindName("LogFilterBox")).Text = "中文";
            ((ComboBox)window.FindName("LogLevelBox")).SelectedIndex = 4;
            ((CheckBox)window.FindName("OnlySelectedAppLogBox")).IsChecked = true;
            typeof(AutelVysor.DeviceDebugWindow).GetField("_selectedLogPid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, "1234");
            bool Match(string line) => (bool)DebugCall(window, "ShouldShowLogLine", line)!;
            const string line = "10-22 00:15:30.123  1234  5678 W MyTag   : 中文 message";
            Check(Match(line), "A valid threadtime line failed combined filters");
            Check(!Match(line.Replace("1234", "4321")), "Other app PID leaked through");
            Check(!Match(line.Replace("MyTag", "Other")), "Other tag leaked through");
            Check(!Match(line.Replace(" W ", " I ")), "Lower priority leaked through");
            Check(!Match(line.Replace("中文", "English")), "Text filter was ignored");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static Task LogcatMinimumSeverity()
    {
        var window = DebugWindow();
        try
        {
            ((ComboBox)window.FindName("LogLevelBox")).SelectedIndex = 5;
            Check(!(bool)DebugCall(window, "ShouldShowLogLine", "10-22 00:15:30.123  1234  5678 D MyTag: debug")!, "Debug passed an Error filter");
            Check(!(bool)DebugCall(window, "ShouldShowLogLine", "--------- beginning of main")!, "Unparsed text passed a structured level filter");
            Check((bool)DebugCall(window, "ShouldShowLogLine", "10-22 00:15:30.123  1234  5678 F MyTag: fatal")!, "Fatal was excluded by Error");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static Task LogcatFlushBudget()
    {
        var window = DebugWindow();
        try
        {
            for (var i = 0; i < 100; i++) DebugCall(window, "EnqueueLogLine", $"{i:D3}:" + new string('x', 4000));
            DebugCall(window, "FlushLiveLog");
            var output = (ListBox)window.FindName("LogLines");
            string Rendered() => string.Join("\n", output.Items.Cast<object>().Select(row => row.GetType().GetProperty("Text")!.GetValue(row)));
            var firstBatch = Rendered();
            Console.WriteLine($"Logcat first flush: {firstBatch.Length} characters (100 queued 4K lines)");
            Check(firstBatch.Length > 0 && firstBatch.Length <= 32_768, "A single UI tick rendered more than 32K characters");
            for (var i = 0; i < 100; i++) DebugCall(window, "FlushLiveLog");
            Check(Rendered().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 100, "Batched log lines were lost or duplicated");
            Check(Rendered().StartsWith("000:") && Rendered().Contains("099:"), "Log order changed");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static Task LogcatOversizedLine()
    {
        var window = DebugWindow();
        try
        {
            DebugCall(window, "EnqueueLogLine", new string('x', 1_000_000));
            DebugCall(window, "FlushLiveLog");
            var row = ((ListBox)window.FindName("LogLines")).Items[0];
            var text = (string)row.GetType().GetProperty("Text")!.GetValue(row)!;
            Check(text.Length <= 32_768 && text.Contains("[truncated]"), "Oversized line was not explicitly bounded");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static async Task SharedPackageSnapshot()
    {
        File.WriteAllText(State("calls"), "");
        var window = DebugWindow();
        try
        {
            ((ComboBox)window.FindName("PackageBox")).Text = "com.test.app";
            await (Task)DebugCall(window, "RefreshSelectedAppAsync")!;
            var calls = File.ReadAllLines(State("calls"));
            Console.WriteLine($"App details: {calls.Length} ADB subprocesses; {calls.Count(line => line.Contains("dumpsys package com.test.app"))} package dump(s)");
            Check(calls.Count(line => line.Contains("dumpsys package com.test.app")) == 1, "App refresh repeated dumpsys package instead of sharing its snapshot");
            Check(((TextBlock)window.FindName("VersionText")).Text == "1.2.3", "App info did not use the snapshot");
            Check(((TextBox)window.FindName("PermissionsTextBox")).Text.Contains("android.permission.CAMERA"), "Permission view lost shared data");
            Check(((TextBox)window.FindName("PackageDumpTextBox")).Text.Contains("versionName=1.2.3"), "Raw package view differs from shared data");
        }
        finally { window.Close(); }
    }

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

    private static async Task ShellBoundary()
    {
        // adb joins shell argv with spaces; run that exact string through Git's
        // real POSIX shell, rather than teaching a fake ADB the desired result.
        var commands = State("commands");
        Directory.CreateDirectory(commands);
        File.WriteAllText(Path.Combine(commands, "dumpsys"), "#!/bin/sh\ncase \"$1\" in battery) echo BatteryStatus;; wifi) echo WifiStatus;; dnsresolver) echo ResolverStatus;; connectivity) echo ConnectivityStatus;; *) echo ALL_DUMPSYS;; esac\n");
        File.WriteAllText(Path.Combine(commands, "wm"), "#!/bin/sh\ncase \"$1\" in size) echo 'Physical size: 1080x2400';; density) echo 'Physical density: 420';; *) echo WM_HELP;; esac\n");
        File.WriteAllText(Path.Combine(commands, "settings"), "#!/bin/sh\ncase \"$3\" in accelerometer_rotation) cat \"$PWD/sensor\";; *) cat \"$PWD/rotation\";; esac\n");
        var bridge = Bridge();
        var output = await bridge.RunShellCommandAsync("test-device", "printf '%s|%s' 'hello world' \"a'b\"");
        Check(output == "hello world|a'b", "Shell lost command arguments: " + output);
        output = await bridge.RunShellCommandAsync("test-device", "printf one; printf ' two'");
        Check(output == "one two", "Compound shell command changed: " + output);
        var failed = false;
        try { await bridge.RunShellCommandAsync("test-device", "false"); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Nonzero shell status was reported as success");
        // Exercises AdvancedAdbService's separate shell caller as well.
        var report = await new AdvancedAdbService(Environment.ProcessPath!).GetNetworkReportAsync("test-device");
        Check(report.Contains("WifiStatus") && !report.Contains("ALL_DUMPSYS"), "Network query dropped dumpsys wifi argument");
        var overview = await bridge.GetDeviceOverviewAsync("test-device");
        Check(overview.Contains("BatteryStatus") && overview.Contains("Physical size: 1080x2400") && !overview.Contains("ALL_DUMPSYS"),
            "Device overview dropped display/battery arguments");
    }

    private static async Task RotationResetsZoom()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(MirrorSessionControl view, string method, params object[] args)
            => typeof(MirrorSessionControl).GetMethod(method, flags)!.Invoke(view, args);
        void AssertFit(MirrorSessionControl view)
        {
            Check(((ComboBox)view.FindName("ZoomBox")).SelectedIndex == 0, "Rotation left the zoom selector out of Fit");
            Check(((System.Windows.Controls.Primitives.ScrollBar)view.FindName("PanHorizontal")).Visibility == Visibility.Collapsed,
                "Rotation left cropped-view pan controls visible");
            Check(((Image)view.FindName("LegacyImage")).RenderTransform.Value.M11 == 1,
                "Rotation left the legacy image magnified");
        }
        try
        {
            foreach (var device in new[] { TestDevice(), TestDevice() with { ApiLevel = 19 } })
            {
                using var view = new MirrorSessionControl(Bridge(), null, null, new AppSettings(), device);
                Call(view, "ApplyZoom", 2d);
                await (Task)Call(view, "ChangeOrientationAsync", true)!;
                AssertFit(view);
            }
            // Hold the first display-size query so rotation can return to Fit while 1:1 is pending.
            File.Delete(State("release-display-size"));
            File.WriteAllText(State("delay-display-size"), "1");
            using var modern = new MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice());
            var actualSize = (Task)Call(modern, "SetActualSizeAsync")!;
            var until = DateTime.UtcNow.AddSeconds(4);
            while (!File.Exists(State("display-size-started")) && DateTime.UtcNow < until) await Task.Delay(20);
            Check(File.Exists(State("display-size-started")) && !actualSize.IsCompleted,
                "Display-size query did not reach the pending gate");
            var rotation = (Task)Call(modern, "ChangeOrientationAsync", true)!;
            File.WriteAllText(State("release-display-size"), "1");
            await Task.WhenAll(actualSize, rotation);
            AssertFit(modern);
            Check(!((TextBlock)modern.FindName("StatusText")).Text.Contains("did not report a display size"),
                "Pending 1:1 was not exercised through a successful display-size query");
        }
        finally
        {
            File.WriteAllText(State("release-display-size"), "1");
            File.Delete(State("delay-display-size"));
            File.Delete(State("display-size-started"));
        }
    }

    private static async Task ActualDisplayRotation()
    {
        File.WriteAllText(State("sensor"), "1");
        File.WriteAllText(State("rotation"), "1"); // last lock, NOT actual orientation
        File.WriteAllText(State("modern-display"), "0");
        try
        {
            var state = await new AdvancedAdbService(Environment.ProcessPath!).GetRotationStateAsync("test-device");
            Check(state.AutoRotate && state.Degrees == 0, "Auto mode used the stale 90-degree user setting");
            Check(await Bridge().GetCurrentRotationDegreesAsync("test-device") == 0, "Orientation toggle uses a different angle source");
            File.WriteAllText(State("modern-display"), "3");
            Check(await Bridge().GetCurrentRotationDegreesAsync("test-device") == 270, "Actual 270-degree display was not detected");
            File.WriteAllText(State("modern-display"), "unknown");
            var failed = false;
            try { await Bridge().GetCurrentRotationDegreesAsync("test-device"); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "Unavailable display diagnostics became a fabricated angle");
        }
        finally { File.Delete(State("modern-display")); }
    }

    private static AndroidDevice TestDevice() => new("test-device", "device", "Test", "", "", "", "", 34);

    private static async Task ModernKeyboardMode()
    {
        File.Delete(State("native-hwnd"));
        using var session = await new ScrcpyService(Environment.ProcessPath!, new AppSettings()).StartEmbeddedAsync(TestDevice());
        var args = File.ReadAllLines(State("scrcpy-args"));
        Check(args.Contains("--keyboard=uhid") && !args.Contains("--raw-key-events"), "Modern default keyboard still uses the limited SDK injection channel");
    }

    private static async Task NativeZoomPan()
    {
        var host = new NativeWindowHost();
        var zoom = typeof(NativeWindowHost).GetMethod("SetZoom");
        var pan = typeof(NativeWindowHost).GetMethod("SetPan");
        Check(zoom is not null && pan is not null, "Mirror has no adjustable zoom/pan");
        var window = new Window { Width = 700, Height = 500, Content = host, ShowInTaskbar = false };
        var child = IntPtr.Zero;
        try
        {
            window.Show();
            await Task.Delay(100);
            child = CreateWindowEx(0, "STATIC", "Zoom test", 0x90000000U, 0, 0, 200, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            host.AttachExternalWindow(child);
            zoom!.Invoke(host, [2d]);
            pan!.Invoke(host, [0d, 0d]);
            await Task.Delay(100);
            GetClientRect(host.Handle, out var available);
            GetWindowRect(child, out var actual); MapWindowPoints(IntPtr.Zero, host.Handle, ref actual, 2);
            Check(actual.Left == 0 && actual.Top == 0 && actual.Right == available.Right * 2 && actual.Bottom == available.Bottom * 2, "200% zoom/pan start geometry is wrong");
            pan.Invoke(host, [1d, 1d]);
            MoveWindow(child, 13, 17, 300, 400, true);
            await Task.Delay(250);
            GetWindowRect(child, out actual); MapWindowPoints(IntPtr.Zero, host.Handle, ref actual, 2);
            Check(actual.Left == -available.Right && actual.Top == -available.Bottom, "Zoom/pan was lost after SDL resized itself");
            host.SetFitMode(); await Task.Delay(100);
            GetWindowRect(child, out actual); MapWindowPoints(IntPtr.Zero, host.Handle, ref actual, 2);
            Check(actual.Left == 0 && actual.Top == 0 && actual.Right == available.Right && actual.Bottom == available.Bottom, "Fit did not reset zoom and pan");
        }
        finally { host.DetachExternalWindow(); if (child != IntPtr.Zero) DestroyWindow(child); window.Close(); }
    }

    private static async Task CompactMirrorStatus()
    {
        var view = new MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice());
        var window = new Window { Width = 1180, Height = 780, Content = view, ShowInTaskbar = false };
        try
        {
            window.Show(); await Task.Delay(100);
            var status = (TextBlock)view.FindName("StatusText");
            status.Text = string.Join('\n', Enumerable.Repeat("ADB command failed / stdout / stderr diagnostic detail", 10));
            window.UpdateLayout();
            Check(status.ActualHeight <= 24, "Multiline command diagnostics consumed the mirror viewport");
            Check(status.ToolTip?.ToString() == status.Text, "Compact status lost the full diagnostic tooltip");
            var folder = Path.GetFullPath("test-results"); Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(folder, "input-zoom.png")); encoder.Save(image);
        }
        finally { view.Dispose(); window.Close(); }
    }

    private static async Task ModernToolbarPaste()
    {
        File.Delete(State("native-hwnd")); File.Delete(State("native-paste"));
        var settings = new AppSettings();
        var view = new MirrorSessionControl(Bridge(), new ScrcpyService(Environment.ProcessPath!, settings), null, settings, TestDevice());
        var window = new Window { Width = 1180, Height = 780, Content = view, ShowInTaskbar = false };
        try
        {
            window.Show();
            var host = (NativeWindowHost)view.FindName("ScrcpyHost");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!host.HasChild && DateTime.UtcNow < until) await Task.Delay(50);
            Check(host.HasChild, "Fake scrcpy child did not attach");
            window.Activate();
            until = DateTime.UtcNow.AddSeconds(2);
            while (!host.HasKeyboardFocus && DateTime.UtcNow < until) await Task.Delay(25);
            Check(host.HasKeyboardFocus, "Opening/activating the mirror did not focus the keyboard automatically");
            var editor = (TextBox)view.FindName("AndroidTextBox");
            editor.Focus();
            view.FocusKeyboardWhenReady();
            await Task.Delay(150);
            Check(editor.IsKeyboardFocusWithin, "Automatic mirror focus stole PC text entry");
            typeof(MirrorSessionControl).GetMethod("FocusMirror_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, [view, new RoutedEventArgs()]);
            until = DateTime.UtcNow.AddSeconds(2);
            while (!host.HasKeyboardFocus && DateTime.UtcNow < until) await Task.Delay(25);
            Check(host.HasKeyboardFocus, "Explicit focus recovery did not leave the PC editor");
            const string expected = "这个是多么 café 😀 123";
            ((TextBox)view.FindName("AndroidTextBox")).Text = expected;
            typeof(MirrorSessionControl).GetMethod("SendText_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, [view, new RoutedEventArgs()]);
            until = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(State("native-paste")) && DateTime.UtcNow < until) await Task.Delay(25);
            Check(File.Exists(State("native-paste")) && File.ReadAllText(State("native-paste")) == expected,
                "Chinese toolbar text did not reach native clipboard paste without ADBKeyBoard");
        }
        finally { view.Dispose(); window.Close(); }
    }

    private static Task PasteRequiresAttachedHost()
    {
        var method = typeof(NativeWindowHost).GetMethod("PasteClipboard");
        Check(method is not null, "Native clipboard paste is unavailable");
        using var host = new NativeWindowHost();
        Check(!(bool)method!.Invoke(host, null)!, "Paste sent keys without an attached mirror");
        return Task.CompletedTask;
    }

    private static async Task EmbeddedKeyboard()
    {
        File.Delete(State("native-hwnd"));
        var service = new ScrcpyService(Environment.ProcessPath!, new AppSettings());
        using var session = await service.StartEmbeddedAsync(TestDevice(), keyboardMode: "sdk");
        var arguments = File.ReadAllLines(State("scrcpy-args"));
        Check(arguments.Contains("--keyboard=sdk") && arguments.Contains("--raw-key-events") && !arguments.Contains("--prefer-text"),
            "Embedded numeric/letter keys still require SDL_TEXTINPUT instead of key events");
    }

    private static async Task RecordingLifecycle()
    {
        var service = new ScrcpyService(Environment.ProcessPath!, new AppSettings());
        File.Delete(State("native-hwnd"));
        File.Delete(State("record-finalized"));
        using var first = service.StartRecording(TestDevice(), State("first.mp4"));
        var recorderId = first.Id;
        Process? duplicate = null;
        var rejected = false;
        try
        {
            try { duplicate = service.StartRecording(TestDevice(), State("second.mp4")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Repeated Record launched a second recorder for the same device");
            var stop = typeof(ScrcpyService).GetMethod("StopRecordingsAsync");
            Check(stop is not null, "Recording has no owned stop/shutdown operation");
            await (Task)stop!.Invoke(service, null)!;
            var stillRunning = Process.GetProcesses().Any(p => { using (p) return p.Id == recorderId; });
            Check(!stillRunning, "Recorder remained after stop");
            Check(File.Exists(State("record-finalized")), "Recorder was killed before native close/finalization");
        }
        finally
        {
            foreach (var process in new[] { first, duplicate })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(true); } catch { }
                process.Dispose();
            }
        }
    }

    private static async Task RecordingCleanupFailure()
    {
        var service = new ScrcpyService(Environment.ProcessPath!, new AppSettings());
        File.Delete(State("native-hwnd"));
        service.StartRecording(TestDevice(), State("cleanup.mp4"));
        // A real Process with no associated OS process makes HasExited and Kill
        // fail, exercising the same unknown-liveness boundary as OS access errors.
        var field = typeof(ScrcpyService).GetField("_recordings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sessions = (System.Collections.IDictionary)field!.GetValue(service)!;
        var real = sessions["test-device"]!;
        using var inaccessible = new Process();
        var unknown = Activator.CreateInstance(real.GetType(), inaccessible, State("unknown.mp4"));
        sessions["test-device"] = unknown;
        try
        {
            try { await service.StopRecordingsAsync(); }
            catch (InvalidOperationException) { }
            Check(service.HasRecordings, "Failed cleanup discarded an owned recorder whose exit could not be confirmed");
            var rejected = false;
            try { service.StartRecording(TestDevice(), State("duplicate.mp4")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unknown recorder liveness allowed a duplicate recording");
        }
        finally
        {
            sessions["test-device"] = real;
            await service.StopRecordingsAsync();
        }
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
            foreach (var height in new[] { 350d, 700d, 450d })
            {
                window.Height = height;
                await Task.Delay(200);
                GetClientRect(host.Handle, out var available);
                GetWindowRect(child, out var actual); MapWindowPoints(IntPtr.Zero, host.Handle, ref actual, 2);
                Check(actual.Left == 0 && actual.Top == 0 && actual.Right == available.Right && actual.Bottom == available.Bottom,
                    "A WPF viewport resize left the Android window cropped at the old size");
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
        // Recorder/embedded-window tests use the same helper marker. Never read
        // an HWND left by an already-exited subprocess from an earlier case.
        File.Delete(State("native-hwnd"));
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
            Check(host.HasKeyboardFocus, "Native focus confirmation did not see the child");
            var focusWatch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                Check(host.FocusEmbeddedWindow(), "Repeated focus lost the child window");
            Check(focusWatch.Elapsed < TimeSpan.FromSeconds(1), "Already-focused requests are slow");
            File.Delete(State("native-typed"));
            var keys = new[] { KeyInput(0x36, false), KeyInput(0x36, true), KeyInput(0x37, false), KeyInput(0x37, true) };
            Check(SendInput((uint)keys.Length, keys, Marshal.SizeOf<TestInput>()) == keys.Length, "Native digit injection failed");
            var until = DateTime.UtcNow.AddSeconds(3);
            while ((!File.Exists(State("native-typed")) || File.ReadAllText(State("native-typed")) != "67") && DateTime.UtcNow < until) await Task.Delay(25);
            Check(File.Exists(State("native-typed")) && File.ReadAllText(State("native-typed")) == "67", "Focus did not deliver real keyboard characters to the foreign HWND");
        }
        finally
        {
            host.DetachExternalWindow();
            window.Close();
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            try { child.WaitForExit(3000); } catch { }
        }
    }

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static WindowProcedure? _childProcedure;
    private static string _nativeTyped = "";
    private static void PublishNativeText(string name, string text)
    {
        File.WriteAllText(State(name + ".tmp"), text);
        File.Move(State(name + ".tmp"), State(name), overwrite: true);
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetProcedure(IntPtr hwnd, int index, WindowProcedure callback);
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);

    private static int NativeChildProcess()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "Autel Vysor native input focus test",
            0x90C00000U, 120, 120, 320, 480,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return 2;
        var previous = IntPtr.Zero;
        _childProcedure = (window, message, wParam, lParam) =>
        {
            if (message == 0x0102 && !char.IsControl((char)wParam.ToInt32()))
            {
                _nativeTyped += (char)wParam.ToInt32();
                PublishNativeText("native-typed", _nativeTyped);
            }
            if ((message == 0x0100 || message == 0x0104) && wParam.ToInt32() == 0x56 && (GetKeyState(0x12) & 0x8000) != 0)
                PublishNativeText("native-paste", Clipboard.GetText());
            if (message == 0x0010) // WM_CLOSE: simulate scrcpy flushing MP4
            {
                File.WriteAllText(State("record-finalized"), "closed normally");
                var arguments = Environment.GetCommandLineArgs();
                var recordIndex = Array.IndexOf(arguments, "--record");
                if (recordIndex >= 0) File.WriteAllText(arguments[recordIndex + 1], "simulated finalized recording");
                DestroyWindow(window);
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return CallWindowProc(previous, window, message, wParam, lParam);
        };
        previous = SetProcedure(hwnd, -4, _childProcedure);
        // Publish the child HWND atomically to prevent the parent process
        // from opening a partially written / exclusively locked marker file.
        var marker = State("native-hwnd");
        var temporary = marker + ".tmp";
        File.WriteAllText(temporary, hwnd.ToInt64().ToString());
        File.Move(temporary, marker, overwrite: true);
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
        if (args.Any(a => a.StartsWith("--window-title=")))
        {
            File.WriteAllLines(State("scrcpy-args"), args);
            return NativeChildProcess();
        }
        var shellIndex = Array.IndexOf(args, "shell");
        if (shellIndex >= 0 && args.Length > shellIndex + 1 &&
            (args[shellIndex + 1] == "sh" || args.Length == shellIndex + 2))
        {
            var bash = (Environment.GetEnvironmentVariable("AUTEL_TEST_SHELL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"));
            var result = ToolRunner.RunAsync(bash, ["-c", "export PATH=\"$PWD/commands:$PATH\"; " + string.Join(" ", args.Skip(shellIndex + 1))], workingDirectory: StateDirectory).GetAwaiter().GetResult();
            Console.Write(result.Output);
            Console.Error.Write(result.Error);
            return result.ExitCode;
        }
        // Real subprocess boundary: exercises ToolRunner and AndroidBridge without a connected phone.
        using (var mutex = new Mutex(false, "AutelVysorRegressionAdb"))
        {
            mutex.WaitOne();
            try { File.AppendAllText(State("calls"), string.Join(" ", args) + "\n"); }
            finally { mutex.ReleaseMutex(); }
        }
        if (TryDebugAppFileAdb(args) is int debugExitCode) return debugExitCode;
        if (TryLogUiAdb(args) is int logExitCode) return logExitCode;
        if (args[0] == "devices")
        {
            Console.WriteLine("List of devices attached");
            if (File.Exists(State("offline-first"))) Console.WriteLine("not-authorized unauthorized model:Offline");
            if (!File.Exists(State("offline"))) Console.WriteLine("test-device device product:test model:Test device:test transport_id:1");
        }
        else if (args.Contains("getprop"))
        {
            Console.WriteLine(args[^1] switch { "ro.build.version.sdk" => "34", "ro.build.version.release" => "14", _ => "arm64-v8a" });
        }
        else if (args.Contains("dumpsys"))
        {
            if (args.Contains("package"))
                Console.WriteLine("  versionName=1.2.3\n  versionCode=12\n  android.permission.CAMERA: granted=true");
            else if (File.Exists(State("modern-display")))
            {
                var rotation = File.ReadAllText(State("modern-display"));
                if (args.Contains("window") && rotation != "unknown")
                    Console.WriteLine($"Display: mDisplayId=0\n  DisplayFrames w=1080 h=2400 r={rotation}\n  mRotation=ROTATION_{int.Parse(rotation) * 90}\nDisplay: mDisplayId=2\n  DisplayFrames w=800 h=600 r=1");
                else Console.WriteLine("No SurfaceOrientation field on this Android");
            }
            else Console.WriteLine("SurfaceOrientation: " + ReadRotation());
        }
        else if (args.Contains("pidof")) Console.WriteLine("1234");
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
        else if (args.Contains("wm") && args.Contains("size"))
        {
            if (File.Exists(State("delay-display-size")))
            {
                File.WriteAllText(State("display-size-started"), "1");
                var wait = Stopwatch.StartNew();
                while (!File.Exists(State("release-display-size")) && wait.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(10);
                if (!File.Exists(State("release-display-size"))) { Console.Error.WriteLine("Display-size fixture gate timed out"); return 1; }
            }
            Console.WriteLine("Physical size: 1080x2400");
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

    private static async Task LeftToolsLayout()
    {
        var window = new AutelVysor.MainWindow { Width = 1180, Height = 780, ShowInTaskbar = false };
        using var first = new MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice());
        using var second = new MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice() with { Serial = "other-device" });
        try
        {
            var tabs = (TabControl)window.FindName("SessionTabs");
            var tab1 = new TabItem { Header = "Phone one", Content = first };
            var tab2 = new TabItem { Header = "Phone two", Content = second };
            tabs.Items.Add(tab1); tabs.Items.Add(tab2); tabs.SelectedItem = tab1;
            ((FrameworkElement)window.FindName("EmptyWorkspace")).Visibility = Visibility.Collapsed;
            window.Show(); await Task.Delay(100); window.UpdateLayout();
            var tools = window.FindName("SessionToolsHost") as ContentControl;
            Check(tools is not null && ReferenceEquals(tools.Content, first.FindName("SessionTools")),
                "Active device controls are not hosted in the left sidebar");
            var viewport = (FrameworkElement)first.FindName("MirrorViewport");
            Check(viewport.ActualHeight >= first.ActualHeight - 2, "A top/bottom toolbar still consumes mirror height");
            var left = ((FrameworkElement)tools!).TransformToAncestor(window).Transform(new Point());
            var picture = viewport.TransformToAncestor(window).Transform(new Point());
            Check(left.X < picture.X, "Device controls are not left of the mirror");
            ((TextBox)first.FindName("AndroidTextBox")).Text = "first phone";
            tabs.SelectedItem = tab2; window.UpdateLayout();
            Check(ReferenceEquals(tools!.Content, second.FindName("SessionTools")), "Switching tabs retained the previous device's controls");
            tabs.SelectedItem = tab1; window.UpdateLayout();
            Check(((TextBox)first.FindName("AndroidTextBox")).Text == "first phone", "Switching tabs lost device input state");
            typeof(MirrorSessionControl).GetMethod("ApplyZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(first, [2d]);
            DebugCallMain(window, "ToggleFullscreen"); await Task.Delay(100); window.UpdateLayout();
            Check(((ComboBox)first.FindName("ZoomBox")).SelectedIndex == 0, "Fullscreen retained a cropped zoom instead of Fit");
            Check(viewport.ActualHeight >= window.ActualHeight - 4, "Fullscreen does not give the mirror the available height");
            Check(((FrameworkElement)window.FindName("SidebarRecoveryRail")).IsVisible, "Hidden tools have no mouse-accessible recovery control");
            DebugCallMain(window, "SidebarToggle_Click", window, new RoutedEventArgs()); window.UpdateLayout();
            Check(((FrameworkElement)window.FindName("SidebarPanel")).IsVisible, "Tools cannot be opened while the native mirror has focus");
            DebugCallMain(window, "ToggleFullscreen"); window.UpdateLayout();
            Check(window.WindowStyle == WindowStyle.SingleBorderWindow && ((FrameworkElement)window.FindName("SidebarPanel")).IsVisible,
                "Exiting fullscreen did not restore the window/sidebar");
            var folder = Path.GetFullPath("test-results"); Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(folder, "left-tools.png")); encoder.Save(image);
        }
        finally { window.Close(); }
    }

    private static object? DebugCallMain(AutelVysor.MainWindow window, string method, params object[] args)
        => typeof(AutelVysor.MainWindow).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, args);

    private static async Task DarkSelection()
    {
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
            var sessionList = (ListBox)window.FindName("SessionList");
            var sessionItem = (ListBoxItem)sessionList.ItemContainerGenerator.ContainerFromIndex(0);
            var tabSurface = (Border)sessionItem.Template.FindName("Surface", sessionItem);
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
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct TestInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    private static TestInput KeyInput(ushort key, bool up) => new() { Type = 1, Key = key, Flags = up ? 2u : 0u };
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] TestInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
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

