using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class AndroidBridge(string adbPath)
{
    public string ExecutablePath => adbPath;

    private readonly ConcurrentDictionary<string, AndroidDevice> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static string ValueOf(IEnumerable<string> fields, string key)
    {
        var prefix = key + ":";
        var field = fields.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return field is null ? string.Empty : field[prefix.Length..];
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IEnumerable<string> args,
        string? standardInput = null,
        CancellationToken ct = default)
        => await ToolRunner.RunAsync(adbPath, args, standardInput, Path.GetDirectoryName(adbPath), ct);

    public async Task<IReadOnlyList<AndroidDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["devices", "-l"], ct: ct);
        EnsureSuccess(result, "Unable to list Android devices.");

        var devices = new List<AndroidDevice>();
        foreach (var raw in result.Output.Split('\n').Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('*'))
                continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                continue;

            devices.Add(new AndroidDevice(
                fields[0],
                fields[1],
                ValueOf(fields.Skip(2), "model"),
                ValueOf(fields.Skip(2), "product"),
                ValueOf(fields.Skip(2), "device"),
                ValueOf(fields.Skip(2), "transport_id"),
                string.Join(' ', fields.Skip(2))));
        }

        var online = devices.Where(x => x.IsOnline).Select(x => x.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var serial in _metadataCache.Keys.Where(x => !online.Contains(x)))
            _metadataCache.TryRemove(serial, out _);

        var enriched = await Task.WhenAll(devices.Select(device =>
            device.IsOnline ? EnrichAsync(device, ct) : Task.FromResult(device)));
        return enriched;
    }

    private async Task<AndroidDevice> EnrichAsync(AndroidDevice device, CancellationToken ct)
    {
        if (_metadataCache.TryGetValue(device.Serial, out var cached) && cached.TransportId == device.TransportId)
            return device with { ApiLevel = cached.ApiLevel, AndroidVersion = cached.AndroidVersion, Abi = cached.Abi };
        try
        {
            var sdkTask = GetPropAsync(device.Serial, "ro.build.version.sdk", ct);
            var releaseTask = GetPropAsync(device.Serial, "ro.build.version.release", ct);
            var abiTask = GetPropAsync(device.Serial, "ro.product.cpu.abi", ct);
            await Task.WhenAll(sdkTask, releaseTask, abiTask);

            _ = int.TryParse(sdkTask.Result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var api);
            var enriched = device with
            {
                ApiLevel = api,
                AndroidVersion = releaseTask.Result,
                Abi = abiTask.Result
            };
            if (api > 0 && !string.IsNullOrWhiteSpace(enriched.Abi) && !string.IsNullOrWhiteSpace(enriched.AndroidVersion))
                _metadataCache[device.Serial] = enriched;
            return enriched;
        }
        catch
        {
            return device;
        }
    }

    public async Task<string> GetPropAsync(string serial, string key, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "getprop", key], ct: ct);
        EnsureSuccess(result, $"Unable to read {key}.");
        return result.Output.Trim();
    }

    public async Task<string> PairAsync(string address, string pairingCode, CancellationToken ct = default)
    {
        // Supplying the code as an argument avoids an interactive console prompt, which is unreliable
        // when Autel Vysor is packaged as a WinExe.
        var result = await RunAsync(["pair", address, pairingCode], ct: ct);
        EnsureSuccess(result, "Wireless pairing failed.", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> GetAdbVersionAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["version"], ct: ct);
        EnsureSuccess(result, "Unable to read the ADB version.");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string?> GetWifiIpAsync(string serial, CancellationToken ct = default)
    {
        foreach (var key in new[] { "dhcp.wlan0.ipaddress", "dhcp.wifi.ipaddress" })
        {
            try
            {
                var value = await GetPropAsync(serial, key, ct);
                if (IsUsableIpv4(value))
                    return value;
            }
            catch { }
        }

        var interfaces = new List<string> { "wlan0" };
        try
        {
            var configured = await GetPropAsync(serial, "wifi.interface", ct);
            if (!string.IsNullOrWhiteSpace(configured) && !interfaces.Contains(configured, StringComparer.OrdinalIgnoreCase))
                interfaces.Insert(0, configured);
        }
        catch { }

        foreach (var iface in interfaces)
        {
            try
            {
                var result = await RunAsync(["-s", serial, "shell", "ip", "-f", "inet", "addr", "show", iface], ct: ct);
                if (result.ExitCode != 0)
                    continue;
                var match = Regex.Match(result.Output, @"\binet\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})/");
                if (match.Success && IsUsableIpv4(match.Groups["ip"].Value))
                    return match.Groups["ip"].Value;
            }
            catch { }
        }

        return null;
    }

    private static bool IsUsableIpv4(string value)
        => IPAddress.TryParse(value.Trim(), out var ip)
           && ip.AddressFamily == AddressFamily.InterNetwork
           && !IPAddress.IsLoopback(ip)
           && !ip.Equals(IPAddress.Any);

    public async Task<string> ConnectAsync(string address, CancellationToken ct = default)
    {
        var result = await RunAsync(["connect", address], ct: ct);
        EnsureSuccess(result, "Wireless connection failed.", "failed", "unable", "cannot");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> DisconnectAsync(string address, CancellationToken ct = default)
    {
        var result = await RunAsync(["disconnect", address], ct: ct);
        EnsureSuccess(result, "Wireless disconnect failed.", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> TcpIpAsync(string serial, int port = 5555, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "tcpip", port.ToString(CultureInfo.InvariantCulture)], ct: ct);
        EnsureSuccess(result, "Unable to enable ADB TCP/IP mode.", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> InstallAsync(string serial, string apkPath, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "install", "-r", apkPath], ct: ct);
        EnsureSuccess(result, "APK installation failed.", "failure", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> PushAsync(string serial, string localPath, string remotePath, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "push", localPath, remotePath], ct: ct);
        EnsureSuccess(result, "File transfer failed.", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<string> PullAsync(string serial, string remotePath, string localPath, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "pull", remotePath, localPath], ct: ct);
        EnsureSuccess(result, "File download failed.", "failed", "error");
        return (result.Output + result.Error).Trim();
    }

    public async Task<byte[]> CaptureScreenshotBytesAsync(string serial, CancellationToken ct = default)
    {
        var result = await ToolRunner.RunBinaryAsync(
            adbPath,
            ["-s", serial, "exec-out", "screencap", "-p"],
            Path.GetDirectoryName(adbPath),
            ct);

        if (result.ExitCode == 0 && result.Output.Length > 8 && IsPng(result.Output))
            return result.Output;

        // Conservative Android 4.4 fallback for old adbd implementations where exec-out is unreliable.
        const string remote = "/sdcard/.autel-vysor-screen.png";
        var capture = await RunAsync(["-s", serial, "shell", "screencap", "-p", remote], ct: ct);
        EnsureSuccess(capture, "Screenshot failed.");

        var temp = Path.Combine(Path.GetTempPath(), $"autel-vysor-{Guid.NewGuid():N}.png");
        try
        {
            var pull = await RunAsync(["-s", serial, "pull", remote, temp], ct: ct);
            EnsureSuccess(pull, "Screenshot download failed.", "failed", "error");
            return await File.ReadAllBytesAsync(temp, ct);
        }
        finally
        {
            _ = RunAsync(["-s", serial, "shell", "rm", "-f", remote], ct: CancellationToken.None);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public async Task CaptureScreenshotAsync(string serial, string outputPath, CancellationToken ct = default)
    {
        var bytes = await CaptureScreenshotBytesAsync(serial, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        await File.WriteAllBytesAsync(outputPath, bytes, ct);
    }

    public Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
        => ShellCheckedAsync(serial, ["input", "tap", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)], ct);

    public Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
        => ShellCheckedAsync(serial,
            ["input", "swipe", x1.ToString(CultureInfo.InvariantCulture), y1.ToString(CultureInfo.InvariantCulture), x2.ToString(CultureInfo.InvariantCulture), y2.ToString(CultureInfo.InvariantCulture), Math.Clamp(durationMs, 50, 3000).ToString(CultureInfo.InvariantCulture)], ct);

    public Task KeyEventAsync(string serial, int keyCode, CancellationToken ct = default)
        => ShellCheckedAsync(serial, ["input", "keyevent", keyCode.ToString(CultureInfo.InvariantCulture)], ct);

    public Task<int> GetCurrentRotationDegreesAsync(string serial, CancellationToken ct = default)
        => DisplayRotationReader.ReadAsync(adbPath, serial, ct);

    private static bool RotationCommandSucceeded((int ExitCode, string Output, string Error) result)
    {
        var output = (result.Output + "\n" + result.Error).Trim();
        return result.ExitCode == 0
            && !Regex.IsMatch(output, @"(?:unknown|not found|unsupported|permission denied|securityexception|error:|exception)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Locks the actual Android display rotation. First disable the sensor setting,
    /// then use the system WindowManager command, with a Settings.System fallback for
    /// Android 4.4 and vendor ROMs. This never rotates only the PC capture image.
    /// </summary>
    public async Task<int> ToggleDeviceOrientationAsync(string serial, CancellationToken ct = default)
    {
        var current = await GetCurrentRotationDegreesAsync(serial, ct);
        var next = current is 90 or 270 ? 0 : 1;
        var nextText = next.ToString(CultureInfo.InvariantCulture);

        // On several vendor ROMs, accelerometer_rotation=1 immediately overrides
        // wm user-rotation lock. Disable it before requesting a fixed orientation.
        var sensor = await RunAsync(["-s", serial, "shell", "settings", "put", "system", "accelerometer_rotation", "0"], ct: ct);

        var wm = await RunAsync(["-s", serial, "shell", "wm", "user-rotation", "lock", nextText], ct: ct);
        if (!RotationCommandSucceeded(wm))
            wm = await RunAsync(["-s", serial, "shell", "wm", "set-user-rotation", "lock", nextText], ct: ct);

        // Maintain the settings fallback even on devices that claim wm success: some
        // OEM builds accept the command but leave the rotation setting unchanged.
        var setting = await RunAsync(["-s", serial, "shell", "settings", "put", "system", "user_rotation", nextText], ct: ct);
        if (!RotationCommandSucceeded(wm) && setting.ExitCode != 0)
        {
            var details = (wm.Output + wm.Error + setting.Output + setting.Error).Trim();
            throw new InvalidOperationException("Android rejected rotation lock. " + details);
        }

        if (sensor.ExitCode != 0 && !RotationCommandSucceeded(wm))
            throw new InvalidOperationException("Android could not disable sensor rotation or set a rotation lock.");

        // Verify the WindowManager policy, rather than treating exit code 0 as proof.
        var policy = await RunAsync(["-s", serial, "shell", "wm", "user-rotation"], ct: ct);
        var match = Regex.Match(policy.Output, @"\block\s+(?<rotation>[0-3])\b", RegexOptions.IgnoreCase);
        if (policy.ExitCode == 0 && match.Success && match.Groups["rotation"].Value != nextText)
            throw new InvalidOperationException($"Android kept rotation {match.Groups["rotation"].Value} instead of {nextText}.");

        return next * 90;
    }

    public async Task EnableAutoRotationAsync(string serial, CancellationToken ct = default)
    {
        // Release WindowManager's fixed lock before enabling the sensor again.
        var wm = await RunAsync(["-s", serial, "shell", "wm", "user-rotation", "free"], ct: ct);
        if (!RotationCommandSucceeded(wm))
            wm = await RunAsync(["-s", serial, "shell", "wm", "set-user-rotation", "free"], ct: ct);

        var sensor = await RunAsync(["-s", serial, "shell", "settings", "put", "system", "accelerometer_rotation", "1"], ct: ct);
        EnsureSuccess(sensor, "Unable to enable Android sensor auto-rotation.", "error", "failed", "exception");

        var verify = await RunAsync(["-s", serial, "shell", "settings", "get", "system", "accelerometer_rotation"], ct: ct);
        if (verify.ExitCode == 0 && verify.Output.Trim() == "0")
            throw new InvalidOperationException("Android did not enable sensor auto-rotation; please check device settings.");

        var policy = await RunAsync(["-s", serial, "shell", "wm", "user-rotation"], ct: ct);
        if (RotationCommandSucceeded(wm) &&
            policy.Output.TrimStart().StartsWith("lock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Android is still locked by WindowManager; please check the device orientation policy.");
    }

    public Task InputTextAsync(string serial, string text, CancellationToken ct = default)
    {
        // `input text` on Android 4.4 has limited Unicode support. Keep this path intentionally conservative.
        var safe = text.Replace(" ", "%s", StringComparison.Ordinal)
                       .Replace("&", "\\&", StringComparison.Ordinal)
                       .Replace("<", "\\<", StringComparison.Ordinal)
                       .Replace(">", "\\>", StringComparison.Ordinal)
                       .Replace(";", "\\;", StringComparison.Ordinal);
        return ShellCheckedAsync(serial, ["input", "text", safe], ct);
    }

    public async Task<string> GetPackageDumpAsync(string serial, string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "dumpsys", "package", packageName], ct: ct);
        EnsureSuccess(result, "Unable to read package information.", "Unable to find package", "not found");
        return (result.Output + result.Error).Trim();
    }

    public async Task<AndroidAppInfo> GetAppInfoAsync(string serial, string packageName, CancellationToken ct = default)
    {
        var dump = await GetPackageDumpAsync(serial, packageName, ct);
        var pid = await GetPidAsync(serial, packageName, ct);
        return ParseAppInfo(packageName, dump, pid);
    }

    public async Task<(AndroidAppInfo Info, string Permissions, string PackageDump)> GetAppDetailsAsync(
        string serial, string packageName, CancellationToken ct = default)
    {
        // All three views must use the same dump, rather than starting three
        // potentially expensive dumpsys calls with inconsistent snapshots.
        var dumpTask = GetPackageDumpAsync(serial, packageName, ct);
        var pidTask = GetPidAsync(serial, packageName, ct);
        await Task.WhenAll(dumpTask, pidTask);
        var dump = await dumpTask;
        return (ParseAppInfo(packageName, dump, await pidTask), ParsePermissions(dump), dump);
    }

    private static AndroidAppInfo ParseAppInfo(string packageName, string dump, string pid)
    {
        static string Capture(string text, string pattern)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
        }

        return new AndroidAppInfo(
            packageName,
            Capture(dump, @"^\s*versionName=(?<v>[^\r\n]+)"),
            Capture(dump, @"^\s*versionCode=(?<v>\d+)"),
            Capture(dump, @"\bminSdk=(?<v>\d+)"),
            Capture(dump, @"\btargetSdk=(?<v>\d+)"),
            Capture(dump, @"^\s*firstInstallTime=(?<v>[^\r\n]+)"),
            Capture(dump, @"^\s*lastUpdateTime=(?<v>[^\r\n]+)"),
            Capture(dump, @"^\s*dataDir=(?<v>[^\r\n]+)"),
            pid);
    }

    public async Task<string> GetPidAsync(string serial, string packageName, CancellationToken ct = default)
    {
        try
        {
            var pidof = await RunAsync(["-s", serial, "shell", "pidof", packageName], ct: ct);
            if (pidof.ExitCode == 0 && !string.IsNullOrWhiteSpace(pidof.Output))
                return pidof.Output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        }
        catch { }

        var ps = await GetProcessListAsync(serial, ct);
        foreach (var line in ps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.EndsWith(packageName, StringComparison.OrdinalIgnoreCase)
                && !line.Contains(packageName + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out _))
                return parts[1];
        }
        return string.Empty;
    }

    public async Task<string> GetProcessListAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "ps", "-A"], ct: ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            result = await RunAsync(["-s", serial, "shell", "ps"], ct: ct);
        EnsureSuccess(result, "Unable to read Android process list.", "error", "failed");
        return result.Output.Trim();
    }

    public async Task<string> GetAppPermissionsAsync(string serial, string packageName, CancellationToken ct = default)
    {
        var dump = await GetPackageDumpAsync(serial, packageName, ct);
        return ParsePermissions(dump);
    }

    private static string ParsePermissions(string dump)
    {
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line =>
                line.Contains(".permission.", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("permission.", StringComparison.OrdinalIgnoreCase)
                || line.Contains("granted=", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return lines.Length == 0
            ? "No permission lines were reported by dumpsys package."
            : string.Join(Environment.NewLine, lines);
    }

    public async Task<string> GetLogcatSnapshotAsync(string serial, int maxLines = 2500, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "logcat", "-d", "-v", "threadtime"], ct: ct);
        EnsureSuccess(result, "Unable to read logcat.", "error", "failed");
        var lines = result.Output.Split('\n');
        if (lines.Length > maxLines)
            lines = lines[^maxLines..];
        return string.Join(Environment.NewLine, lines).Trim();
    }

    public async Task ExportLogcatAsync(string serial, string outputPath, CancellationToken ct = default)
    {
        var result = await ToolRunner.RunToFileAsync(
            adbPath,
            ["-s", serial, "logcat", "-d", "-v", "threadtime"],
            outputPath,
            Path.GetDirectoryName(adbPath),
            ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Unable to export logcat." : result.Error.Trim());
    }

    public Process StartLogcat(string serial)
    {
        var info = new System.Diagnostics.ProcessStartInfo(adbPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(adbPath) ?? AppContext.BaseDirectory
        };
        foreach (var arg in new[] { "-s", serial, "logcat", "-v", "threadtime" })
            info.ArgumentList.Add(arg);

        var process = new System.Diagnostics.Process
        {
            StartInfo = info,
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new InvalidOperationException("Unable to start live logcat.");
        return process;
    }

    public async Task UninstallAppAsync(string serial, string packageName, bool keepData = false, CancellationToken ct = default)
    {
        var args = new List<string> { "-s", serial, "uninstall" };
        if (keepData)
            args.Add("-k");
        args.Add(packageName);
        var result = await RunAsync(args, ct: ct);
        EnsureSuccess(result, "Unable to uninstall application.", "failure", "failed", "error");
    }

    public async Task RebootDeviceAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "reboot"], ct: ct);
        EnsureSuccess(result, "Unable to reboot Android device.", "failed", "error");
    }

    public async Task RestartAdbServerAsync(CancellationToken ct = default)
    {
        var kill = await RunAsync(["kill-server"], ct: ct);
        if (kill.ExitCode != 0 && !string.IsNullOrWhiteSpace(kill.Error))
            throw new InvalidOperationException(kill.Error.Trim());

        await Task.Delay(500, ct);
        var start = await RunAsync(["start-server"], ct: ct);
        EnsureSuccess(start, "Unable to restart ADB server.", "failed", "error");
    }

    public async Task<string> RunShellCommandAsync(string serial, string command, CancellationToken ct = default)
    {
        var result = await RunShellCommandResultAsync(serial, command, ct);
        result.EnsureSuccess("Shell command failed.");
        // Keep raw success output for overview/property parsers and existing callers.
        return (result.Output + (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : "\n" + result.Error)).TrimEnd();
    }

    public async Task<CommandResult> RunShellCommandResultAsync(string serial, string command, CancellationToken ct = default)
    {
        command = command.Trim();
        if (command.Length == 0)
            throw new InvalidOperationException("Enter an ADB shell command.");
        if (command.Length > 8192)
            throw new InvalidOperationException("The shell command is too long.");

        // shell-v1 (including Android 4.4) returns host exit 0 for remote failures.
        // Capture the status in a parent shell after a subshell: `exit`, `exec`,
        // redirects and a final comment in the user command cannot skip the marker.
        // The single ADB argument avoids an additional round of shell quoting.
        var marker = "__AUTEL_EXIT_" + Guid.NewGuid().ToString("N") + "__";
        var wrapped = "(\n" + command + "\n)\n__autel_status=$?\nprintf '\\n" + marker + "%s\\n' \"$__autel_status\"";
        var result = await RunAsync(["-s", serial, "shell", wrapped], ct: ct);
        var suffix = Regex.Match(result.Output, "\\n" + marker + "(?<code>[0-9]{1,3})\\n\\z");
        // Older adbd PTYs may translate the marker's LF to CRLF.
        if (!suffix.Success)
            suffix = Regex.Match(result.Output, "\\r\\n" + marker + "(?<code>[0-9]{1,3})\\r\\n\\z");
        int? remoteCode = suffix.Success && int.TryParse(suffix.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            && code <= 255 ? code : null;
        return new CommandResult(result.ExitCode, remoteCode is not null ? result.Output[..suffix.Index] : result.Output, result.Error)
        {
            RequiresRemoteExitCode = true,
            RemoteExitCode = remoteCode
        };
    }

    public async Task<(int Width, int Height)> GetDisplaySizeAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "wm", "size"], ct: ct);
        EnsureSuccess(result, "Unable to read Android display size.");
        var matches = Regex.Matches(result.Output + "\n" + result.Error, @"(?<w>\d{2,5})x(?<h>\d{2,5})");
        if (matches.Count == 0)
            throw new InvalidOperationException("Android did not report a display size.");
        var match = matches[^1];
        return (int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListRemoteFilesAsync(string serial, string remoteDirectory, CancellationToken ct = default)
    {
        remoteDirectory = NormalizeRemotePath(remoteDirectory);
        var result = await RunShellCommandResultAsync(serial, "ls -la " + QuoteRemotePath(remoteDirectory), ct);
        result.EnsureSuccess("Unable to list remote directory.");

        var entries = new List<RemoteFileEntry>();
        foreach (var raw in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(
                line,
                @"^(?<perm>[bcdlps-][rwxStTs-]{9})\s+(?:\d+\s+)?\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+(?<name>.+)$");
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value.Trim();
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                name = name[..arrow];
            if (name is "." or ".." || name.Length == 0)
                continue;

            _ = long.TryParse(match.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var fullPath = remoteDirectory == "/"
                ? "/" + name
                : remoteDirectory.TrimEnd('/') + "/" + name;
            entries.Add(new RemoteFileEntry(
                name,
                fullPath,
                match.Groups["perm"].Value.StartsWith("d", StringComparison.Ordinal),
                size,
                match.Groups["date"].Value));
        }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task CreateRemoteDirectoryAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        remotePath = NormalizeRemotePath(remotePath);
        var result = await RunShellCommandResultAsync(serial, "mkdir -p " + QuoteRemotePath(remotePath), ct);
        result.EnsureSuccess("Unable to create remote directory.");
    }

    public async Task DeleteRemotePathAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        remotePath = NormalizeRemotePath(remotePath);
        if (remotePath is "/" or "/sdcard" or "/storage" or "/storage/emulated" or "/storage/emulated/0")
            throw new InvalidOperationException("Refusing to delete a protected root storage path.");

        var result = await RunShellCommandResultAsync(serial, "rm -rf " + QuoteRemotePath(remotePath), ct);
        result.EnsureSuccess("Unable to delete remote path.");
    }

    public async Task RenameRemotePathAsync(string serial, string source, string destination, CancellationToken ct = default)
    {
        source = NormalizeRemotePath(source);
        destination = NormalizeRemotePath(destination);
        if (source == "/" || source == destination) throw new InvalidOperationException("Select a different destination name.");
        // Refuse overwrite, including dangling symlinks. All paths are quoted as shell data.
        var command = $"if [ -e {QuoteRemotePath(destination)} ] || [ -L {QuoteRemotePath(destination)} ]; then echo 'Destination already exists' >&2; exit 1; fi\nmv {QuoteRemotePath(source)} {QuoteRemotePath(destination)}";
        var result = await RunShellCommandResultAsync(serial, command, ct);
        result.EnsureSuccess("Unable to rename remote item.");
    }

    private static string QuoteRemotePath(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public async Task<string> GetDeviceOverviewAsync(string serial, CancellationToken ct = default)
    {
        var report = new StringBuilder();
        async Task AddProp(string label, string key)
        {
            try
            {
                var value = await GetPropAsync(serial, key, ct);
                report.AppendLine($"{label}: {(string.IsNullOrWhiteSpace(value) ? "?" : value)}");
            }
            catch
            {
                report.AppendLine($"{label}: ?");
            }
        }

        report.AppendLine("=== Device ===");
        await AddProp("Manufacturer", "ro.product.manufacturer");
        await AddProp("Brand", "ro.product.brand");
        await AddProp("Model", "ro.product.model");
        await AddProp("Device", "ro.product.device");
        await AddProp("Android", "ro.build.version.release");
        await AddProp("SDK", "ro.build.version.sdk");
        await AddProp("ABI", "ro.product.cpu.abi");
        await AddProp("Build", "ro.build.display.id");
        await AddProp("Security patch", "ro.build.version.security_patch");
        report.AppendLine($"Serial: {serial}");

        foreach (var section in new[]
        {
            ("Display", "wm size; wm density"),
            ("Battery", "dumpsys battery"),
            ("Memory", "cat /proc/meminfo"),
            ("Storage", "df /data /sdcard 2>/dev/null"),
            ("Network", "ip -f inet addr 2>/dev/null")
        })
        {
            report.AppendLine();
            report.AppendLine($"=== {section.Item1} ===");
            try
            {
                var text = await RunShellCommandAsync(serial, section.Item2, ct);
                report.AppendLine(string.IsNullOrWhiteSpace(text) ? "(no output)" : text);
            }
            catch (Exception ex)
            {
                report.AppendLine($"Unavailable: {ex.Message}");
            }
        }

        return report.ToString().TrimEnd();
    }

    private static string NormalizeRemotePath(string remotePath)
    {
        remotePath = remotePath.Trim().Replace('\\', '/');
        if (remotePath.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("Remote paths cannot contain control characters.");
        if (!remotePath.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("Remote paths must be absolute Android paths.");
        while (remotePath.Contains("//", StringComparison.Ordinal))
            remotePath = remotePath.Replace("//", "/", StringComparison.Ordinal);
        if (remotePath.Contains("/../", StringComparison.Ordinal) || remotePath.EndsWith("/..", StringComparison.Ordinal))
            throw new InvalidOperationException("Parent path traversal is not allowed.");
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".");
        return "/" + string.Join("/", segments);
    }

    public async Task<IReadOnlyList<string>> ListPackagesAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "pm", "list", "packages"], ct: ct);
        EnsureSuccess(result, "Unable to list Android packages.", "error", "failed");
        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["package:".Length..].Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task ForceStopAppAsync(string serial, string packageName, CancellationToken ct = default)
        => ShellCheckedAsync(serial, ["am", "force-stop", packageName], ct);

    public async Task ClearAppDataAsync(string serial, string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(["-s", serial, "shell", "pm", "clear", packageName], ct: ct);
        EnsureSuccess(result, "Unable to clear application data.", "failed", "error");
        var combined = (result.Output + "\n" + result.Error).Trim();
        if (!combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(combined) ? "Android did not confirm that application data was cleared." : combined);
    }

    public async Task StartAppAsync(string serial, string packageName, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["-s", serial, "shell", "monkey", "-p", packageName, "-c", "android.intent.category.LAUNCHER", "1"],
            ct: ct);
        EnsureSuccess(result, "Unable to start application.", "No activities found", "aborted", "error", "failed");
    }

    public async Task RestartAppAsync(string serial, string packageName, CancellationToken ct = default)
    {
        await ForceStopAppAsync(serial, packageName, ct);
        await Task.Delay(180, ct);
        await StartAppAsync(serial, packageName, ct);
    }

    private async Task ShellCheckedAsync(string serial, IEnumerable<string> shellArgs, CancellationToken ct)
    {
        var args = new List<string> { "-s", serial, "shell" };
        args.AddRange(shellArgs);
        var result = await RunAsync(args, ct: ct);
        EnsureSuccess(result, "Android input command failed.", "error", "failed");
    }

    private static bool IsPng(byte[] data)
        => data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static void EnsureSuccess(
        (int ExitCode, string Output, string Error) result,
        string fallback,
        params string[] failureWords)
    {
        var combined = (result.Output + "\n" + result.Error).Trim();
        var failedByText = failureWords.Any(word => combined.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (result.ExitCode != 0 || failedByText)
            throw new AdbCommandException(fallback, new CommandResult(result.ExitCode, result.Output, result.Error));
    }
}

