using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AutelVysor.Models;

namespace AutelVysor.Services;

/// <summary>
/// Optional inspection and device-management commands. The Android shell enforces
/// its own permission model; no command here assumes root privileges.
/// </summary>
public sealed class AdvancedAdbService(string adbPath)
{
    private Task<(int ExitCode, string Output, string Error)> RunAsync(
        IEnumerable<string> args, CancellationToken ct = default)
        => ToolRunner.RunAsync(adbPath, args, workingDirectory: Path.GetDirectoryName(adbPath), ct: ct);

    private async Task<string> ShellAsync(string serial, string command, CancellationToken ct = default)
    {
        // adb joins shell arguments before the device shell parses them. A single
        // command preserves arguments, quotes and operators without a nested sh -c.
        var result = await RunAsync(["-s", serial, "shell", command], ct);
        var output = (result.Output + (string.IsNullOrWhiteSpace(result.Error) ? "" : "\n" + result.Error)).Trim();
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrEmpty(output) ? "ADB shell command failed." : output);
        return output;
    }

    public async Task<IReadOnlyList<string>> ListPackagesAsync(
        string serial, string filter, CancellationToken ct = default)
    {
        var args = new List<string> { "-s", serial, "shell", "pm", "list", "packages" };
        switch (filter)
        {
            case "User": args.Add("-3"); break;
            case "System": args.Add("-s"); break;
        }
        var result = await RunAsync(args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException((result.Output + result.Error).Trim());
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("package:", StringComparison.Ordinal))
            .Select(s => s["package:".Length..].Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ValidatePackage(string packageName)
    {
        if (!Regex.IsMatch(packageName, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$"))
            throw new InvalidOperationException("Select a valid Android package name.");
        return packageName;
    }

    private static string ValidatePermission(string permission)
    {
        permission = permission.Trim();
        if (!Regex.IsMatch(permission, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$"))
            throw new InvalidOperationException("Enter an Android permission such as android.permission.CAMERA.");
        return permission;
    }

    public async Task<string> SetRuntimePermissionAsync(
        string serial, string packageName, string permission, bool grant, CancellationToken ct = default)
    {
        packageName = ValidatePackage(packageName);
        permission = ValidatePermission(permission);
        var result = await RunAsync(["-s", serial, "shell", "pm", grant ? "grant" : "revoke", packageName, permission], ct);
        var output = (result.Output + "\n" + result.Error).Trim();
        if (result.ExitCode != 0 || output.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "Permission operation failed." : output);
        return $"{(grant ? "Granted" : "Revoked")} {permission} for {packageName}. Android may reject non-runtime permissions.";
    }

    public async Task<string> SetAppOpAsync(
        string serial, string packageName, string op, string mode, CancellationToken ct = default)
    {
        ValidatePackage(packageName);
        op = op.Trim();
        if (!Regex.IsMatch(op, @"^[A-Za-z][A-Za-z0-9_]{0,80}$"))
            throw new InvalidOperationException("Invalid appops operation name.");
        if (mode is not ("allow" or "ignore" or "deny" or "default"))
            throw new InvalidOperationException("Select allow, ignore, deny or default.");
        var result = await RunAsync(["-s", serial, "shell", "appops", "set", packageName, op, mode], ct);
        var output = (result.Output + "\n" + result.Error).Trim();
        if (result.ExitCode != 0 || output.Contains("Error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "AppOps change failed." : output);
        return $"appops: {packageName} {op} = {mode}";
    }

    public async Task<string> GetAppOpsAsync(string serial, string packageName, CancellationToken ct = default)
    {
        ValidatePackage(packageName);
        var result = await RunAsync(["-s", serial, "shell", "appops", "get", packageName], ct);
        var output = (result.Output + "\n" + result.Error).Trim();
        return result.ExitCode == 0 ? output : $"AppOps unavailable: {output}";
    }

    public async Task<(int Degrees, bool AutoRotate)> GetRotationStateAsync(
        string serial, CancellationToken ct = default)
    {
        var auto = await RunAsync(["-s", serial, "shell", "settings", "get", "system", "accelerometer_rotation"], ct);
        var automatic = auto.Output.Trim() == "1";
        if (auto.ExitCode != 0 || auto.Output.Trim() is not ("0" or "1"))
            throw new InvalidOperationException("Android rotation mode is unavailable.");
        var rotation = await DisplayRotationReader.ReadAsync(adbPath, serial, ct);
        return (rotation, automatic);
    }

    public async Task<string> GetAudioRouteReportAsync(
        AndroidDevice device, bool pcAudio, bool keepDevice, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Audio policy (configured, not a playback test)");
        sb.AppendLine($"Android API: {device.ApiLevel}");
        if (device.ApiLevel < 30 || !pcAudio)
            sb.AppendLine("PC audio: disabled / unsupported. Device audio remains the default.");
        else if (keepDevice && device.ApiLevel >= 33)
            sb.AppendLine("PC audio: enabled in scrcpy duplicate mode; device/Bluetooth playback remains enabled.");
        else if (keepDevice)
            sb.AppendLine("PC audio: disabled to preserve phone/Bluetooth audio on Android 11/12.");
        else
            sb.AppendLine("PC audio: enabled, device audio may be redirected to PC.");

        try
        {
            var result = await RunAsync(["-s", device.Serial, "shell", "dumpsys", "audio"], ct);
            var lines = result.Output.Split('\n')
                .Where(line => Regex.IsMatch(line, @"bluetooth|a2dp|le.audio|sco|wired.head|speaker|active.device|mDevices", RegexOptions.IgnoreCase))
                .Take(20)
                .Select(line => line.Trim());
            sb.AppendLine("Android audio diagnostics (routing indicators only):");
            foreach (var line in lines) sb.AppendLine("  " + line);
            if (result.ExitCode != 0) sb.AppendLine("  Diagnostics unavailable.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Android audio diagnostics unavailable: {ex.Message}");
        }
        sb.AppendLine("Bluetooth headset output must be confirmed with a real playback test; ADB does not guarantee a reliable active-route report on all ROMs.");
        return sb.ToString();
    }

    public async Task<string> GetNetworkReportAsync(string serial, CancellationToken ct = default)
    {
        var sb = new StringBuilder("Network diagnostics\n");
        foreach (var (title, command) in new (string Title, string Command)[]
        {
            ("IP interfaces", "ip addr 2>/dev/null || ifconfig"),
            ("Routes", "ip route 2>/dev/null || route -n"),
            ("DNS", "getprop | grep -iE 'dns|dhcp.*gateway'"),
            ("Open TCP/UDP sockets", "ss -tuna 2>/dev/null || netstat -an 2>/dev/null"),
            ("Wi-Fi", "dumpsys wifi 2>/dev/null | head -n 90")
        })
        {
            sb.AppendLine("\n=== " + title + " ===");
            try
            {
                var result = await ShellAsync(serial, command, ct);
                sb.AppendLine(result.Length > 16000 ? result[..16000] + "\n[truncated]" : result);
            }
            catch (Exception ex) { sb.AppendLine("Unavailable: " + ex.Message); }
        }
        return sb.ToString();
    }

    public async Task<string> PingAsync(string serial, string host, CancellationToken ct = default)
    {
        host = host.Trim();
        if (!Regex.IsMatch(host, @"^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$"))
            throw new InvalidOperationException("Enter an IPv4 address or DNS hostname.");
        return await ShellAsync(serial, "ping -c 4 -W 2 " + host, ct);
    }

    public async Task<string> ExportBugreportAsync(string serial, string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var safeSerial = Regex.Replace(serial, @"[^A-Za-z0-9_.-]", "_");
        var outputPath = Path.Combine(outputDirectory, $"autel-bugreport-{safeSerial}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(7));
        var result = await RunAsync(["-s", serial, "bugreport", outputPath], timeout.Token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"bugreport failed: {(result.Output + result.Error).Trim()}");
        var file = File.Exists(outputPath) ? outputPath :
            Directory.EnumerateFiles(outputDirectory, $"autel-bugreport-{safeSerial}-*")
                     .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null)
            throw new InvalidOperationException("ADB finished but did not produce a bugreport file. Check device support.");
        return file;
    }

    public async Task<string> SendUnicodeViaAdbKeyboardAsync(
        string serial, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Enter text to send.");
        if (value.Length > 4000)
            throw new InvalidOperationException("Send at most 4000 characters per request.");

        var installed = await RunAsync(["-s", serial, "shell", "pm", "path", "com.android.adbkeyboard"], ct);
        if (installed.ExitCode != 0 || !installed.Output.Contains("package:", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "ADBKeyBoard is not installed. Install and enable the optional open-source ADBKeyBoard IME on the device before sending Unicode text.");

        var active = await RunAsync(["-s", serial, "shell", "settings", "get", "secure", "default_input_method"], ct);
        if (!active.Output.Contains("com.android.adbkeyboard", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Select ADBKeyBoard as the active Android input method first. Autel Vysor will not silently change your keyboard.");

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var result = await RunAsync(["-s", serial, "shell", "am", "broadcast", "-a", "ADB_INPUT_B64", "--es", "msg", base64], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException((result.Output + result.Error).Trim());
        return "Unicode text broadcast sent to ADBKeyBoard. Confirm insertion on the Android device.";
    }
}

