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
    private readonly AndroidBridge _shellBridge = new(adbPath);

    private Task<(int ExitCode, string Output, string Error)> RunAsync(
        IEnumerable<string> args, CancellationToken ct = default)
        => ToolRunner.RunAsync(adbPath, args, workingDirectory: Path.GetDirectoryName(adbPath), ct: ct);

    public async Task<IReadOnlyList<string>> ListPackagesAsync(
        string serial, string filter, CancellationToken ct = default)
    {
        var command = "pm list packages" + (filter switch { "User" => " -3", "System" => " -s", _ => "" });
        var result = await _shellBridge.RunShellCommandResultAsync(serial, command, ct);
        EnsureDiagnosticSuccess(result, "Unable to list Android packages.");
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
        var result = await _shellBridge.RunShellCommandResultAsync(serial,
            $"pm {(grant ? "grant" : "revoke")} {packageName} {permission}", ct);
        EnsureDiagnosticSuccess(result, "Permission operation failed.");
        return $"{(grant ? "Granted" : "Revoked")} {permission} for {packageName}. Android may reject non-runtime permissions.\n"
            + result.FormatForDisplay();
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
        var result = await _shellBridge.RunShellCommandResultAsync(serial, $"appops set {packageName} {op} {mode}", ct);
        EnsureDiagnosticSuccess(result, "AppOps change failed.");
        return $"appops: {packageName} {op} = {mode}\n" + result.FormatForDisplay();
    }

    public async Task<string> GetAppOpsAsync(string serial, string packageName, CancellationToken ct = default)
    {
        ValidatePackage(packageName);
        var result = await _shellBridge.RunShellCommandResultAsync(serial, "appops get " + packageName, ct);
        EnsureDiagnosticSuccess(result, "AppOps unavailable.");
        return result.FormatForDisplay();
    }

    public async Task<(int Degrees, bool AutoRotate)> GetRotationStateAsync(
        string serial, CancellationToken ct = default)
    {
        var auto = await _shellBridge.RunShellCommandResultAsync(serial, "settings get system accelerometer_rotation", ct);
        EnsureDiagnosticSuccess(auto, "Android rotation mode is unavailable.");
        var automatic = auto.Output.Trim() == "1";
        if (auto.Output.Trim() is not ("0" or "1"))
            throw new AdbCommandException("Android rotation mode is unavailable.", auto);
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
            var result = await _shellBridge.RunShellCommandResultAsync(device.Serial, "dumpsys audio", ct);
            var lines = result.Output.Split('\n')
                .Where(line => Regex.IsMatch(line, @"bluetooth|a2dp|le.audio|sco|wired.head|speaker|active.device|mDevices", RegexOptions.IgnoreCase))
                .Take(20)
                .Select(line => line.Trim());
            sb.AppendLine("Android audio diagnostics (routing indicators only):");
            sb.AppendLine("Audio diagnostics: " + DiagnosticStatus(result));
            sb.AppendLine("Filtered routing indicators:");
            foreach (var line in lines) sb.AppendLine("  " + line);
            sb.AppendLine(result.FormatForDisplay(16000));
        }
        catch (OperationCanceledException) { throw; }
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
            ("IP interfaces", "ip addr"),
            ("IP interfaces (legacy fallback)", "ifconfig"),
            ("Routes", "ip route"),
            ("Routes (legacy fallback)", "route -n"),
            ("DNS properties", "getprop"),
            ("DNS resolver", "dumpsys dnsresolver"),
            ("Connectivity / DNS link properties", "dumpsys connectivity"),
            ("Open TCP/UDP sockets", "ss -tuna"),
            ("Open TCP/UDP sockets (legacy fallback)", "netstat -an"),
            ("Wi-Fi", "dumpsys wifi")
        })
        {
            sb.AppendLine("\n=== " + title + " ===");
            try
            {
                var result = await _shellBridge.RunShellCommandResultAsync(serial, command, ct);
                var status = DiagnosticStatus(result);
                sb.AppendLine(title + ": " + status);
                if (title == "DNS properties" && status == "Reported")
                {
                    // grep's exit code 1 means no matches, not an ADB/getprop failure.
                    // Filter on the host so the getprop status remains intact.
                    var lines = result.Output.Split('\n').Where(line =>
                        Regex.IsMatch(line, @"^\[(?:[^\]]*dns[^\]]*|dhcp\.[^\]]*gateway)\]:", RegexOptions.IgnoreCase));
                    var properties = string.Join('\n', lines);
                    if (properties.Length == 0)
                        sb.AppendLine("No legacy DNS properties reported. Modern Android may expose DNS only through resolver or connectivity services.");
                    sb.AppendLine("Filtered DNS properties:");
                    sb.AppendLine(properties.Length == 0 ? "(none)" : properties);
                    sb.AppendLine(result.FormatForDisplay(16000));
                }
                else
                    sb.AppendLine(result.FormatForDisplay(16000));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { sb.AppendLine("Query failed: " + ex.Message); }
        }
        sb.AppendLine("\nDNS entries describe reported configuration; they do not verify successful hostname resolution. Use a hostname ping and compare with an IP ping; packet loss or blocked ICMP alone does not establish DNS failure.");
        return sb.ToString();
    }

    private static string DiagnosticStatus(CommandResult result)
    {
        // Only clear error diagnostic lines can override a successful process
        // status. dumpsys also contains historical events and capability fields
        // with words such as "unsupported" or "permission denied".
        var lines = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1));
        bool Matches(string pattern) => lines.Any(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
        // Some Android services print these errors but still exit 0.
        if (Matches(@"^(?:error:\s*)?(?:permission\s+(?:denied|denial)\b|(?:java\.lang\.)?securityexception\b|requires?\s+(?:android\.)?permission\b|not allowed to dump\b)|^(?:\S+:\s*){1,2}permission denied\b"))
            return "Permission denied";
        if (result.EffectiveExitCode == 127 || Matches(@"^(?:error:\s*)?(?:(?:can't|cannot|could not) find service\b|unknown (?:command|service)\b|(?:command|service)\b[^:\r\n]*\bnot found\b|not supported\b|unsupported\b)|^(?:\S+:\s*){1,2}not found\b"))
            return "Unsupported";
        if (!result.Succeeded || Matches(@"^(?:error:|exception(?:\s|:))"))
            return "Query failed";
        return "Reported";
    }

    private static void EnsureDiagnosticSuccess(CommandResult result, string message)
    {
        result.EnsureSuccess(message);
        if (DiagnosticStatus(result) != "Reported")
            throw new AdbCommandException(message, result);
    }

    public async Task<string> PingAsync(string serial, string host, CancellationToken ct = default)
    {
        host = host.Trim();
        if (!Regex.IsMatch(host, @"^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$"))
            throw new InvalidOperationException("Enter an IPv4 address or DNS hostname.");
        var result = await _shellBridge.RunShellCommandResultAsync(serial, "ping -c 4 -W 2 " + host, ct);
        // A nonzero ping exit is useful evidence and must remain visible in the report.
        return result.FormatForDisplay();
    }

    public async Task<string> ExportBugreportAsync(string serial, string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var safeSerial = Regex.Replace(serial, @"[^A-Za-z0-9_.-]", "_");
        var outputPath = Path.Combine(outputDirectory, $"autel-bugreport-{safeSerial}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(7));
        var raw = await RunAsync(["-s", serial, "bugreport", outputPath], timeout.Token);
        var result = new CommandResult(raw.ExitCode, raw.Output, raw.Error);
        result.EnsureSuccess("Bugreport failed.");
        var file = File.Exists(outputPath) ? outputPath :
            Directory.EnumerateFiles(outputDirectory, $"autel-bugreport-{safeSerial}-*")
                     .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null)
            throw new AdbCommandException("ADB finished but did not produce a bugreport file. Check device support.", result);
        return file;
    }

}

