using AutelVysor.Services;
internal static class Program
{
    private static string StateDirectory => Environment.GetEnvironmentVariable("AUTEL_BRIDGE_TEST_STATE")!;
    private static string State(string file) => Path.Combine(StateDirectory, file);
    private static AndroidBridge Bridge() => new(Environment.ProcessPath!);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0) return FakeAdb(args);
        var directory = Path.Combine(Path.GetTempPath(), "Autel-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AUTEL_BRIDGE_TEST_STATE", directory);
        var failures = 0;
        try
        {
            foreach (var (name, test) in new (string, Func<Task>)[] { ("ShellBoundary", ShellBoundary), ("ActualDisplayRotation", ActualDisplayRotation), ("CleanupOwnership", CleanupOwnership) })
            {
                try { await test(); Console.WriteLine("PASS: " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + "\n" + ex); }
            }
        }
        finally { Directory.Delete(directory, true); }
        return failures == 0 ? 0 : 1;
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
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(commands))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    private static async Task CleanupOwnership()
    {
        var service = new ScrcpyService("unused", new AutelVysor.Models.AppSettings());
        var field = typeof(ScrcpyService).GetField("_recordings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sessions = (System.Collections.IDictionary)field!.GetValue(service)!;
        using var inaccessible = new System.Diagnostics.Process();
        sessions["test-device"] = new RecordingSession(inaccessible, "unknown.mp4");
        try { await service.StopRecordingsAsync(); }
        catch (InvalidOperationException) { }
        Check(service.HasRecordings, "Unknown process liveness was discarded after cleanup failure");
    }

    private static int FakeAdb(string[] args)
    {
        var index = Array.IndexOf(args, "shell");
        if (index < 0) return 1;
        var command = string.Join(" ", args.Skip(index + 1));
        if (args.Contains("dumpsys") && (args.Contains("window") || args.Contains("input")))
        {
            var rotation = File.ReadAllText(State("modern-display"));
            if (args.Contains("window") && rotation != "unknown")
                Console.WriteLine($"Display: mDisplayId=0\n DisplayFrames w=1080 h=2400 r={rotation}\n mCurrentRotation=ROTATION_{int.Parse(rotation)*90}\nDisplay: mDisplayId=4\n DisplayFrames w=800 h=600 r=1");
            else Console.WriteLine("No legacy orientation field");
            return 0;
        }
        if (args.Contains("settings") && args.Contains("get"))
        {
            Console.WriteLine(File.ReadAllText(State(args.Contains("accelerometer_rotation") ? "sensor" : "rotation")));
            return 0;
        }
        if (args.Contains("getprop")) { Console.WriteLine("test-property"); return 0; }
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/sh";
        var result = ToolRunner.RunAsync(shell, ["-c", "export PATH=\"$PWD/commands:$PATH\"; " + command], workingDirectory: StateDirectory).GetAwaiter().GetResult();
        Console.Write(result.Output); Console.Error.Write(result.Error);
        return result.ExitCode;
    }
}
