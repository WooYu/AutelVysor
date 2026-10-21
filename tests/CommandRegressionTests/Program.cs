using System.Text;
using AutelVysor.Services;

internal static class Program
{
    private const string Device = "test-device";
    // Fixed UTF-8 bytes, independent of Console.OutputEncoding and the reader's encoding.
    private static readonly byte[] Utf8Payload = [0xE4, 0xB8, 0xAD, 0xE6, 0x96, 0x87, 0x20, 0xC3, 0xA9, 0x20, 0xF0, 0x9F, 0x9A, 0x80, 0x20, 0x26, 0x20, 0x22, 0x27, 0x25, 0x24, 0x5C, 0x0A];
    private const string Expected = "中文 é 🚀 & \"'%$\\\n";
    private static string Self => Environment.ProcessPath!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0) return await Child(args);
        var fixture = Path.Combine(Path.GetTempPath(), "autel-command-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "commands"));
        Environment.SetEnvironmentVariable("AUTEL_COMMAND_FIXTURE", fixture);
        Environment.SetEnvironmentVariable("AUTEL_COMMAND_SELF", Self.Replace('\\', '/'));
        foreach (var name in new[] { "getprop", "dumpsys", "ip", "ifconfig", "route", "ss", "netstat", "ping", "pm", "appops", "settings" })
        {
            var path = Path.Combine(fixture, "commands", name);
            File.WriteAllText(path, "#!/bin/sh\nexec \"$AUTEL_COMMAND_SELF\" fake-probe " + name + " \"$@\"\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var failures = 0;
        try
        {
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("DuplexPipesCannotDeadlock", DuplexPipesCannotDeadlock),
            ("CancellationDuringInputTerminatesChild", CancellationDuringInputTerminatesChild),
            ("WindowlessMirrorStopSkipsGracePeriod", WindowlessMirrorStopSkipsGracePeriod),
            ("Utf8RawSubprocessBoundary", Utf8RawSubprocessBoundary),
            ("TextPipesDeclareUtf8", TextPipesDeclareUtf8),
            ("BinaryOutputRemainsByteExact", BinaryOutputRemainsByteExact),
            ("LiveLogcatUsesUtf8", LiveLogcatUsesUtf8),
            ("ShellResultPreservesExactStreamsAndHostStatus", ShellResultPreservesExactStreamsAndHostStatus),
            ("ShellFailureRetainsExitCodeAndSeparateStreams", ShellFailureRetainsExitCodeAndSeparateStreams),
            ("ShellSuccessRemainsRawForParsers", ShellSuccessRemainsRawForParsers),
            ("BatchCountsNonzeroEvenWithStdout", BatchCountsNonzeroEvenWithStdout),
            ("LegacyAdbHostZeroRetainsRemoteFailure", LegacyAdbHostZeroRetainsRemoteFailure),
            ("MissingRemoteExitStatusCannotPass", MissingRemoteExitStatusCannotPass),
            ("DnsNoLegacyPropertiesIsNotFailure", DnsNoLegacyPropertiesIsNotFailure),
            ("DnsUnsupportedAndDeniedAreDistinct", DnsUnsupportedAndDeniedAreDistinct),
            ("DnsHistoricalLogTextDoesNotBecomeQueryError", DnsHistoricalLogTextDoesNotBecomeQueryError),
            ("DnsCommandFailureIsNotUnsupported", DnsCommandFailureIsNotUnsupported),
            ("NetworkFallbackProbesHaveIndependentStatuses", NetworkFallbackProbesHaveIndependentStatuses),
            ("LegacyNetworkQueryRetainsRemoteFailure", LegacyNetworkQueryRetainsRemoteFailure),
            ("LegacyFailedPingRetainsRemoteFailure", LegacyFailedPingRetainsRemoteFailure),
            ("AdvancedPackageFailureDiagnostics", () => AdvancedShellFailure(async (service, serial) => { await service.ListPackagesAsync(serial, "User"); })),
            ("AdvancedPermissionFailureDiagnostics", () => AdvancedShellFailure(async (service, serial) => { await service.SetRuntimePermissionAsync(serial, "com.test.app", "android.permission.CAMERA", true); })),
            ("AdvancedAppOpSetFailureDiagnostics", () => AdvancedShellFailure(async (service, serial) => { await service.SetAppOpAsync(serial, "com.test.app", "CAMERA", "allow"); })),
            ("AdvancedAppOpGetFailureDiagnostics", () => AdvancedShellFailure(async (service, serial) => { await service.GetAppOpsAsync(serial, "com.test.app"); })),
            ("AdvancedRotationFailureDiagnostics", () => AdvancedShellFailure(async (service, serial) => { await service.GetRotationStateAsync(serial); })),
            ("AdvancedMutationSuccessDisplaysStreams", AdvancedMutationSuccessDisplaysStreams),
            ("AudioFailureDiagnostics", AudioFailureDiagnostics),
            ("BugreportFailureDiagnostics", BugreportFailureDiagnostics)
        })
        {
            try { await run(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + "\n" + ex); }
        }
        }
        finally { Directory.Delete(fixture, true); }
        return failures == 0 ? 0 : 1;
    }

    private static async Task DuplexPipesCannotDeadlock()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var result = await ToolRunner.RunAsync(Self, ["duplex"], new string('i', 1_048_576), ct: deadline.Token);
        Check(result.ExitCode == 0 && result.Output == new string('o', 1_048_576) && result.Error == new string('e', 1_048_576),
            "Large simultaneous stdin/stdout/stderr did not round-trip");
    }

    private static async Task CancellationDuringInputTerminatesChild()
    {
        var marker = Path.Combine(Environment.GetEnvironmentVariable("AUTEL_COMMAND_FIXTURE")!, "input-child.lock");
        using var cts = new CancellationTokenSource();
        var running = ToolRunner.RunAsync(Self, ["blocked-input", marker], new string('x', 4_194_304), ct: cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Check(File.Exists(marker), "Child did not reach the blocked input stage");
            cts.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            // The fixture holds this file exclusively until its process exits.
            using var unlocked = File.Open(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { cts.Cancel(); }
    }

    private static async Task WindowlessMirrorStopSkipsGracePeriod()
    {
        using var process = ToolRunner.Launch(Self, ["windowless-wait"]);
        using var session = new ScrcpySession(process, IntPtr.Zero);
        await Task.Delay(150);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        session.Stop();
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "A mirror without a main window waited the 1.2s graceful-close timeout");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task Utf8RawSubprocessBoundary()
    {
        var oldOutput = Console.OutputEncoding;
        var oldInput = Console.InputEncoding;
        try
        {
            // Force a non-UTF-8 host default, reproducing a Windows code-page boundary on any OS.
            Console.OutputEncoding = Encoding.Latin1;
            Console.InputEncoding = Encoding.Latin1;
            var result = await ToolRunner.RunAsync(Self, ["raw-text"], Expected);
            Check(result.Output == Expected, "stdout UTF-8 bytes were decoded using the host code page");
            Check(result.Error == Expected, "stderr UTF-8 bytes were decoded using the host code page");
            Check(result.ExitCode == 0, "stdin was not sent as UTF-8 bytes");
        }
        finally { Console.OutputEncoding = oldOutput; Console.InputEncoding = oldInput; }
    }

    private static async Task BinaryOutputRemainsByteExact()
    {
        var result = await ToolRunner.RunBinaryAsync(Self, ["raw-binary"]);
        Check(result.Output.SequenceEqual(new byte[] { 0x89, 0x50, 0x00, 0xFF, 0x0D, 0x0A }), "Binary bytes were transcoded");
    }

    private static Task TextPipesDeclareUtf8()
    {
        // Linux defaults to UTF-8 even when Console encoding is changed; verify the
        // explicit pipe contract too, so this suite catches Windows code-page regressions.
        var create = typeof(ToolRunner).GetMethod("Create", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var process = (System.Diagnostics.Process)create.Invoke(null, new object?[] { Self, new[] { "raw-text" }, null, true })!;
        Check(process.StartInfo.StandardOutputEncoding?.CodePage == 65001, "stdout encoding is OS-dependent");
        Check(process.StartInfo.StandardErrorEncoding?.CodePage == 65001, "stderr encoding is OS-dependent");
        Check(process.StartInfo.StandardInputEncoding?.CodePage == 65001, "stdin encoding is OS-dependent");
        return Task.CompletedTask;
    }

    private static async Task ShellFailureRetainsExitCodeAndSeparateStreams()
    {
        try { await new AndroidBridge(Self).RunShellCommandAsync(Device, "printf 'partial 中文\\n'; printf 'rejected é\\n' >&2; exit 23"); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("Exit code: 23"), "Missing nonzero exit code: " + ex.Message);
            Check(ex.Message.Contains("stdout:\npartial 中文\n") && ex.Message.Contains("stderr:\nrejected é\n"), "Streams were merged or discarded: " + ex.Message);
            Check(ex.GetType().Name == "AdbCommandException", "Command failure is not typed");
            return;
        }
        throw new Exception("Nonzero exit code became success");
    }

    private static async Task LiveLogcatUsesUtf8()
    {
        using var process = new AndroidBridge(Self).StartLogcat(Device);
        Check(process.StartInfo.StandardOutputEncoding?.CodePage == 65001 && process.StartInfo.StandardErrorEncoding?.CodePage == 65001, "Live logcat bypassed explicit UTF-8");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(await output == Expected && await error == Expected, "Live logcat changed Unicode text");
    }

    private static async Task ShellResultPreservesExactStreamsAndHostStatus()
    {
        var bridge = new AndroidBridge(Self);
        foreach (var command in new[]
        {
            "printf '%s' \"中文 é 🚀 & \\\"'\\$%\\\\\"; printf 'err\\n' >&2",
            "printf 'no newline' # final comment",
            "printf 'line one\\nline two\\n\\n'",
            "printf 'ends in CR\\r'",
            "exec printf 'exec output'",
            "printf 'before exit'; exit 19"
        })
        {
            var shellPath = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/sh";
            var direct = await ToolRunner.RunAsync(shellPath, ["-c", command]);
            var result = await bridge.RunShellCommandResultAsync("legacy-host-zero", command);
            Check(result.Output == direct.Output && result.Error == direct.Error, "Status capture changed raw streams for " + command);
            Check(result.ExitCode == 0 && result.RemoteExitCode == direct.ExitCode, "Host/remote statuses were conflated");
            Check(result.Succeeded == (direct.ExitCode == 0), "Remote failure classification changed");
        }
        var empty = await bridge.RunShellCommandResultAsync(Device, "printf ''");
        Check(empty.FormatForDisplay().Contains("stdout:\n(empty)") && empty.FormatForDisplay().Contains("stderr:\n(empty)"), "Empty streams were omitted");
        var missing = await bridge.RunShellCommandResultAsync("missing-marker", "printf ok");
        Check(!missing.Succeeded && missing.ExitCode == 0 && missing.RemoteExitCode is null, "Unknown remote status was fabricated");
        Check(missing.FormatForDisplay().Contains("ADB exit code: 0") && missing.FormatForDisplay().Contains("Remote exit code: unavailable"), "Missing remote status lost actual host status");
    }

    private static async Task ShellSuccessRemainsRawForParsers()
    {
        Check(await new AndroidBridge(Self).RunShellCommandAsync(Device, "printf 'property=value 中文\\n'") == "property=value 中文", "Legacy successful output was decorated");
    }

    private static async Task BatchCountsNonzeroEvenWithStdout()
    {
        var passed = 0;
        var failed = 0;
        foreach (var serial in new[] { "success-device", "failed-device", "failed-device" })
        {
            try { await new AndroidBridge(Self).RunShellCommandAsync(serial, "printf 'some output\\n'; exit " + (serial == "failed-device" ? "5" : "0")); passed++; }
            catch (InvalidOperationException) { failed++; }
        }
        Check(passed == 1 && failed == 2, $"Batch counted {passed} passed, {failed} failed");
    }

    private static async Task LegacyAdbHostZeroRetainsRemoteFailure()
    {
        try { await new AndroidBridge(Self).RunShellCommandAsync("legacy-host-zero", "printf 'partial output\\n'; exit 23"); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("Exit code: 23") && ex.Message.Contains("partial output"), "Remote status or partial output was lost");
            return;
        }
        throw new Exception("Legacy ADB host exit 0 hid remote exit 23");
    }

    private static async Task MissingRemoteExitStatusCannotPass()
    {
        try { await new AndroidBridge(Self).RunShellCommandAsync("missing-marker", "printf ok"); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("could not be verified"), "Missing remote status was not identified: " + ex.Message);
            return;
        }
        throw new Exception("Unverified remote status was counted as success");
    }

    private static async Task DnsNoLegacyPropertiesIsNotFailure()
    {
        var report = await new AdvancedAdbService(Self).GetNetworkReportAsync("dns-empty");
        Check(report.Contains("No legacy DNS properties reported"), "No-match grep was treated as query failure: " + report);
        Check(report.Contains("DNS resolver") && report.Contains("1.1.1.1"), "Modern resolver diagnostic not collected");
        Check(!report.Contains("DNS is working", StringComparison.OrdinalIgnoreCase), "Configuration was claimed as a resolution test");
    }

    private static async Task DnsUnsupportedAndDeniedAreDistinct()
    {
        var unsupported = await new AdvancedAdbService(Self).GetNetworkReportAsync("dns-unsupported");
        var denied = await new AdvancedAdbService(Self).GetNetworkReportAsync("dns-denied");
        Check(unsupported.Contains("DNS resolver: Unsupported"), "Missing unsupported classification: " + unsupported);
        Check(denied.Contains("DNS resolver: Permission denied"), "Missing permission classification: " + denied);
        Check(!denied.Contains("DNS resolver: Unsupported"), "Permission denial was conflated with unsupported");
        Check(denied.Contains("Exit code: 13") && denied.Contains("stderr:"), "Denied diagnostic lost command evidence");
    }

    private static async Task DnsCommandFailureIsNotUnsupported()
    {
        var report = await new AdvancedAdbService(Self).GetNetworkReportAsync("dns-failed");
        Check(report.Contains("DNS resolver: Query failed"), "Transport failure was not reported as query failure: " + report);
        Check(report.Contains("Exit code: 7") && report.Contains("device offline"), "Failure lost status or stderr");
    }

    private static async Task DnsHistoricalLogTextDoesNotBecomeQueryError()
    {
        var report = await new AdvancedAdbService(Self).GetNetworkReportAsync("dns-history");
        Check(report.Contains("DNS resolver: Reported"), "Historical/configuration text was mistaken for a query error: " + report);
        Check(report.Contains("Private DNS mode: unsupported") && report.Contains("Historical log: permission denied"), "Raw resolver evidence was discarded");
        Check(report.Contains("Filtered DNS properties:"), "Filtered properties were presented as raw stdout");
    }

    private static async Task NetworkFallbackProbesHaveIndependentStatuses()
    {
        var report = await new AdvancedAdbService(Self).GetNetworkReportAsync("fallback-device");
        foreach (var title in new[] { "IP interfaces", "Routes", "Open TCP/UDP sockets" })
            Check(report.Contains(title + ": Unsupported") && report.Contains(title + " (legacy fallback): Reported"), "Fallback status was conflated with primary probe: " + report);
        Check(report.Contains("not found") && report.Contains("legacy probe output"), "A probe's raw evidence was lost");
    }

    private static async Task LegacyNetworkQueryRetainsRemoteFailure()
    {
        var report = await new AdvancedAdbService(Self).GetNetworkReportAsync("legacy-query-failure");
        Check(report.Contains("DNS resolver: Query failed"), "Host zero hid remote query failure: " + report);
        Check(report.Contains("ADB exit code: 0") && report.Contains("Remote exit code: 7"), "Network query lost separate host/remote statuses");
    }

    private static async Task LegacyFailedPingRetainsRemoteFailure()
    {
        var report = await new AdvancedAdbService(Self).PingAsync("legacy-ping-failure", "example.test");
        Check(report.Contains("Exit code: 1") && report.Contains("ADB exit code: 0") && report.Contains("Remote exit code: 1"), "Host zero hid failed ping: " + report);
        Check(report.Contains("100% packet loss"), "Failed ping evidence was lost");
    }

    private static async Task AdvancedShellFailure(Func<AdvancedAdbService, string, Task> operation)
    {
        try { await operation(new AdvancedAdbService(Self), "legacy-advanced-failure"); }
        catch (AdbCommandException ex)
        {
            Check(ex.Result.ExitCode == 0 && ex.Result.RemoteExitCode == 29, "Advanced operation lost host/remote status: " + ex.Message);
            Check(ex.Result.Output == "partial 中文\n" && ex.Result.Error == "rejected é\n", "Advanced operation changed or merged raw streams");
            return;
        }
        throw new Exception("Advanced operation did not retain typed command failure");
    }

    private static async Task AdvancedMutationSuccessDisplaysStreams()
    {
        var service = new AdvancedAdbService(Self);
        var permission = await service.SetRuntimePermissionAsync(Device, "com.test.app", "android.permission.CAMERA", false);
        var appop = await service.SetAppOpAsync(Device, "com.test.app", "CAMERA", "allow");
        foreach (var display in new[] { permission, appop })
            Check(display.Contains("Exit code: 0") && display.Contains("stdout:\nother diagnostic\n") && display.Contains("stderr:\n(empty)"), "Successful mutation omitted command evidence: " + display);
    }

    private static async Task AudioFailureDiagnostics()
    {
        var device = new AutelVysor.Models.AndroidDevice("legacy-advanced-failure", "device", "Test", "", "", "", "", ApiLevel: 33);
        var report = await new AdvancedAdbService(Self).GetAudioRouteReportAsync(device, false, false);
        Check(report.Contains("Audio diagnostics: Query failed") && report.Contains("Remote exit code: 29") && report.Contains("ADB exit code: 0"), "Audio diagnostics lost failure status: " + report);
        Check(report.Contains("stdout:\npartial 中文\n") && report.Contains("stderr:\nrejected é\n"), "Audio diagnostics discarded unfiltered command evidence");
    }

    private static async Task BugreportFailureDiagnostics()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("AUTEL_COMMAND_FIXTURE")!, "bugreports");
        try { await new AdvancedAdbService(Self).ExportBugreportAsync("bugreport-failed", directory); }
        catch (AdbCommandException ex)
        {
            Check(ex.Result.ExitCode == 31 && ex.Result.Output == "partial 中文\n" && ex.Result.Error == "rejected é\n", "Bugreport lost host status or raw streams");
            return;
        }
        throw new Exception("Bugreport did not retain typed command failure");
    }

    private static async Task<int> Child(string[] args)
    {
        if (args[0] is "duplex" or "blocked-input" or "windowless-wait")
        {
            // Safety watchdog prevents a deliberately broken parent from orphaning a test process.
            _ = Task.Run(async () => { await Task.Delay(8000); Environment.Exit(77); });
            if (args[0] == "windowless-wait") { await Task.Delay(Timeout.Infinite); return 0; }
            if (args[0] == "blocked-input")
            {
                using var held = File.Open(args[1], FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(new string('o', 1_048_576)));
            await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(new string('e', 1_048_576)));
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            return await input.ReadToEndAsync() == new string('i', 1_048_576) ? 0 : 9;
        }
        if (args[0] == "fake-probe") return await Probe(args.Skip(1).ToArray());
        if (args[0] == "raw-text")
        {
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            await Console.OpenStandardOutput().WriteAsync(Utf8Payload);
            await Console.OpenStandardError().WriteAsync(Utf8Payload);
            return input.ToArray().SequenceEqual(Utf8Payload) ? 0 : 9;
        }
        if (args[0] == "raw-binary")
        {
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0x89, 0x50, 0x00, 0xFF, 0x0D, 0x0A });
            return 0;
        }
        var serial = args.Length >= 2 ? args[1] : "";
        if (args.Contains("bugreport")) return await Emit("partial 中文\n", "rejected é\n", 31);
        if (args.Contains("logcat"))
        {
            await Console.OpenStandardOutput().WriteAsync(Utf8Payload);
            await Console.OpenStandardError().WriteAsync(Utf8Payload);
            return 0;
        }
        var shell = Array.IndexOf(args, "shell");
        if (shell < 0) return 2;
        var command = string.Join(" ", args.Skip(shell + 1));
        if (serial == "missing-marker") return await Emit("truncated output\n");
        // Exercise actual POSIX parsing for both raw and status-wrapped probes.
        // shell-v1 reports host exit 0 even when the remote command fails.
        Environment.SetEnvironmentVariable("AUTEL_COMMAND_DEVICE", serial);
        var shellPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/sh";
        var result = await ToolRunner.RunAsync(shellPath,
            ["-c", "export PATH=\"$PWD/commands:$PATH\"; " + command],
            workingDirectory: Environment.GetEnvironmentVariable("AUTEL_COMMAND_FIXTURE"));
        return await Emit(result.Output, result.Error, serial.StartsWith("legacy-", StringComparison.Ordinal) ? 0 : result.ExitCode);
    }

    private static async Task<int> Probe(string[] args)
    {
        var serial = Environment.GetEnvironmentVariable("AUTEL_COMMAND_DEVICE");
        if (serial == "legacy-advanced-failure" && args[0] is "pm" or "appops" or "settings" or "dumpsys")
            return await Emit("partial 中文\n", "rejected é\n", 29);
        if (args[0] == "getprop") return await Emit("[ro.build.version.release]: [14]\n");
        if (args[0] == "dumpsys" && args.ElementAtOrDefault(1) == "dnsresolver")
        {
            return serial switch
            {
                "dns-unsupported" => await Emit("Can't find service: dnsresolver\n"),
                "dns-denied" => await Emit("", "Permission Denial: can't dump DnsResolver without android.permission.DUMP\n", 13),
                "dns-failed" => await Emit("partial resolver result\n", "error: device offline\n", 7),
                "dns-history" => await Emit("DNS servers: 1.1.1.1\nPrivate DNS mode: unsupported\nHistorical log: permission denied during old lookup\n"),
                "legacy-query-failure" => await Emit("partial resolver output\n", "", 7),
                _ => await Emit("DNS servers: 1.1.1.1\n")
            };
        }
        if (args[0] == "dumpsys" && args.ElementAtOrDefault(1) == "connectivity") return await Emit("LinkProperties: DnsAddresses: [ /1.1.1.1 ]\n");
        if (serial == "fallback-device")
        {
            if (args[0] is "ip" or "ss") return await Emit("", "/system/bin/sh: " + args[0] + ": not found\n", 127);
            if (args[0] is "ifconfig" or "route" or "netstat") return await Emit("legacy probe output\n");
        }
        if (args[0] == "ping" && serial == "legacy-ping-failure") return await Emit("4 packets transmitted, 0 received, 100% packet loss\n", "", 1);
        return await Emit("other diagnostic\n");
    }

    private static async Task<int> Emit(string output, string error = "", int exitCode = 0)
    {
        await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(output));
        await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(error));
        return exitCode;
    }
}
