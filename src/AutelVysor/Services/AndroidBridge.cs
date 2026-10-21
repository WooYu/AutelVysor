using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class AndroidBridge(string adbPath)
{
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

        var enriched = await Task.WhenAll(devices.Select(device =>
            device.IsOnline ? EnrichAsync(device, ct) : Task.FromResult(device)));
        return enriched;
    }

    private async Task<AndroidDevice> EnrichAsync(AndroidDevice device, CancellationToken ct)
    {
        try
        {
            var sdkTask = GetPropAsync(device.Serial, "ro.build.version.sdk", ct);
            var releaseTask = GetPropAsync(device.Serial, "ro.build.version.release", ct);
            var abiTask = GetPropAsync(device.Serial, "ro.product.cpu.abi", ct);
            await Task.WhenAll(sdkTask, releaseTask, abiTask);

            _ = int.TryParse(sdkTask.Result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var api);
            return device with
            {
                ApiLevel = api,
                AndroidVersion = releaseTask.Result,
                Abi = abiTask.Result
            };
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
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(combined) ? fallback : combined);
    }
}
