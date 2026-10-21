using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutelVysor.Services;

public sealed record UnicodeSendResult(bool Verified, string? ActualText, string Message);

/// <summary>Opt-in ADBKeyBoard setup, durable IME recovery, and observable text insertion.</summary>
public sealed class AdbKeyboardService
{
    public const string Package = "com.android.adbkeyboard";
    public const string Component = Package + "/.AdbIME";
    private readonly string _journalDirectory;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>> _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _commandTimeout;
    private sealed record Recovery(string Serial, string OriginalIme);

    public AdbKeyboardService(string adbPath, string? journalDirectory = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>>? runner = null,
        TimeSpan? commandTimeout = null)
    {
        _journalDirectory = journalDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutelVysor", "ime-recovery");
        _runner = runner ?? ((args, ct) => ToolRunner.RunAsync(adbPath, args, ct: ct));
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
    }

    private string JournalPath(string serial) => Path.Combine(_journalDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial))) + ".json");

    public bool HasRecovery(string serial) => File.Exists(JournalPath(serial));

    private async Task<string> RunAsync(string serial, CancellationToken ct, params string[] command)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_commandTimeout);
        (int ExitCode, string Output, string Error) result;
        try { result = await _runner(new[] { "-s", serial }.Concat(command).ToArray(), timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InvalidOperationException($"ADBKeyBoard ({serial}) command timed out after {_commandTimeout.TotalSeconds:g} seconds. Any original keyboard recovery is retained; reconnect and use Restore original keyboard.", ex);
        }
        var evidence = new CommandResult(result.ExitCode, result.Output, result.Error);
        evidence.EnsureSuccess($"ADBKeyBoard ({serial}) command failed.");
        var output = (result.Output + "\n" + result.Error).Trim();
        if (Regex.IsMatch(output, @"(?im)^\s*(?:error:|failure\b|exception\b|securityexception\b|java\.[\w.]*exception\b)"))
            throw new AdbCommandException($"ADBKeyBoard ({serial}) command was rejected.", evidence);
        return result.Output.Trim();
    }

    private Task<string> CurrentAsync(string serial, CancellationToken ct) =>
        RunAsync(serial, ct, "shell", "settings", "get", "secure", "default_input_method");

    private static bool IsManaged(string ime) => ime == Component || ime == Package + "/" + Package + ".AdbIME";

    private static bool IsComponent(string ime) => Regex.IsMatch(ime, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+/[A-Za-z0-9_.]+$");

    private async Task RequireInstalledAsync(string serial, CancellationToken ct)
    {
        var path = await RunAsync(serial, ct, "shell", "pm", "path", Package);
        var imes = await RunAsync(serial, ct, "shell", "ime", "list", "-a", "-s");
        if (!path.StartsWith("package:", StringComparison.Ordinal) || !imes.Split('\n').Any(x => IsManaged(x.Trim())))
            throw new InvalidOperationException("ADBKeyBoard is unavailable. Open Unicode setup and explicitly install a trusted local APK first.");
    }

    public async Task<string> InspectAsync(string serial, CancellationToken ct = default)
    {
        var current = await CurrentAsync(serial, ct);
        var installed = await RunAsync(serial, ct, "shell", "pm", "path", Package);
        return $"Device: {serial}\nCurrent keyboard: {current}\nADBKeyBoard: {(installed.StartsWith("package:", StringComparison.Ordinal) ? "installed" : "not installed")}\n" +
            (HasRecovery(serial) ? "Saved original keyboard found. Use Restore original keyboard to recover; nothing is switched automatically." : "No saved keyboard recovery for this device.");
    }

    public async Task<string> InstallApkAsync(string serial, string apkPath, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath) || !string.Equals(Path.GetExtension(apkPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a trusted local ADBKeyBoard .apk file.");
        var result = await RunAsync(serial, ct, "install", "-r", Path.GetFullPath(apkPath));
        if (!result.Split('\n').Any(x => x.Trim() == "Success"))
            throw new InvalidOperationException("APK installation was not confirmed: " + result);
        await RequireInstalledAsync(serial, ct);
        return "Local APK installed. Activation requires the separate Activate temporarily button.";
    }

    public async Task<string> ActivateAsync(string serial, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RequireInstalledAsync(serial, ct);
            var current = await CurrentAsync(serial, ct);
            if (HasRecovery(serial))
            {
                ReadRecovery(serial); // Validate before changing anything, including after restart.
                if (IsManaged(current)) return "ADBKeyBoard is already active; original keyboard recovery is retained.";
                throw new InvalidOperationException("A previous recovery exists. Use Restore original keyboard first; your currently selected keyboard will be preserved.");
            }
            if (!IsComponent(current) || IsManaged(current))
                throw new InvalidOperationException("Cannot save an original keyboard. Select your normal keyboard on Android before activating temporarily.");
            Directory.CreateDirectory(_journalDirectory);
            var path = JournalPath(serial);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Recovery(serial, current)), Encoding.UTF8, ct);
            File.Move(temporary, path, true); // Save recovery before either enabling or switching the IME.
            await RunAsync(serial, ct, "shell", "ime", "enable", Component);
            await RunAsync(serial, ct, "shell", "ime", "set", Component);
            if (!IsManaged(await CurrentAsync(serial, ct)))
                throw new InvalidOperationException("Android did not activate ADBKeyBoard. Original keyboard recovery is retained.");
            return "ADBKeyBoard activated temporarily. Focus an empty Android field. Restore your original keyboard when finished.";
        }
        finally { _gate.Release(); }
    }

    private Recovery ReadRecovery(string serial)
    {
        try
        {
            var recovery = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(JournalPath(serial)));
            if (recovery is null || recovery.Serial != serial || !IsComponent(recovery.OriginalIme) || IsManaged(recovery.OriginalIme))
                throw new InvalidOperationException("Invalid keyboard recovery record.");
            return recovery;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException("Cannot read saved keyboard recovery; select your normal keyboard in Android Settings. " + ex.Message, ex);
        }
    }

    public async Task<string> RestoreAsync(string serial, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!HasRecovery(serial)) return "No saved original keyboard for this device. Select a keyboard in Android Settings if needed.";
            var recovery = ReadRecovery(serial);
            var current = await CurrentAsync(serial, ct);
            if (IsManaged(current))
            {
                await RunAsync(serial, ct, "shell", "ime", "set", recovery.OriginalIme);
                if (await CurrentAsync(serial, ct) != recovery.OriginalIme)
                    throw new InvalidOperationException("Original keyboard restoration was not confirmed. Recovery is retained; retry when the device is available.");
                File.Delete(JournalPath(serial));
                return "Original Android keyboard restored and verified.";
            }
            // The user chose another keyboard after activation. Never overwrite that choice.
            if (!IsComponent(current)) throw new InvalidOperationException("Current keyboard is unavailable. Recovery is retained; reconnect and retry.");
            File.Delete(JournalPath(serial));
            return "Your later Android keyboard selection was preserved. Previous temporary activation recovery was cleared.";
        }
        finally { _gate.Release(); }
    }

    internal static bool HasRegisteredReceiver(string dump)
    {
        // ADBKeyBoard dynamically registers this receiver; pm query-receivers does not find it.
        // Restrict matching to the registered receiver section, never broadcast history.
        var section = Regex.Match(dump, @"(?s)Registered Receivers:(.*?)(?:Receiver Resolver Table:|Historical broadcasts|Sticky broadcasts|Active broadcasts|$)").Groups[1].Value;
        var receivers = Regex.Split(section, @"(?m)^\s*\*?\s*ReceiverList\{");
        return receivers.Skip(1).Any(block =>
            Regex.IsMatch(block.Split('\n')[0], @"(?:^|\s)com\.android\.adbkeyboard(?:/|:|\s)") &&
            Regex.IsMatch(block, @"Action:\s*""ADB_INPUT_B64"""));
    }

    public async Task<UnicodeSendResult> SendAsync(string serial, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4000)
            throw new InvalidOperationException("Enter between 1 and 4000 characters to send.");
        await _gate.WaitAsync(ct);
        try
        {
            await RequireInstalledAsync(serial, ct);
            if (!IsManaged(await CurrentAsync(serial, ct)))
                throw new InvalidOperationException("Open Unicode setup and explicitly activate ADBKeyBoard temporarily first.");
            var receivers = await RunAsync(serial, ct, "shell", "dumpsys", "activity", "broadcasts");
            if (!HasRegisteredReceiver(receivers))
                throw new InvalidOperationException("ADBKeyBoard's Unicode receiver is not confirmed. Focus an Android text field and retry. Broadcast completion alone cannot confirm delivery.");
            // Establish a different before-state; an already matching field must
            // never turn a dropped broadcast into a false insertion success.
            var before = await ReadFocusedTextAsync(serial, ct);
            if (before.Length != 0)
                return new(false, before, "NOT sent: focus an empty Android field before Unicode input. Existing text cannot prove a new insertion and has been preserved.");
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            await RunAsync(serial, ct, "shell", "am", "broadcast", "-p", Package, "-a", "ADB_INPUT_B64", "--es", "msg", base64);
            // Upstream has no receiver acknowledgement. Only actual field readback proves insertion.
            try
            {
                var actual = await ReadFocusedTextAsync(serial, ct);
                return new(actual == text, actual, actual == text
                    ? "Unicode input verified against the exact text in the focused Android field."
                    : "Broadcast submitted, insertion NOT verified: Android field differs. Use an empty field and compare the result; do not resend blindly.");
            }
            catch (InvalidOperationException ex)
            {
                return new(false, null, "Broadcast submitted, insertion NOT verified. " + ex.Message + " Do not resend blindly.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadFocusedTextAsync(string serial, CancellationToken ct = default)
    {
        var path = "/sdcard/autel-unicode-" + Guid.NewGuid().ToString("N") + ".xml";
        try
        {
            await RunAsync(serial, ct, "shell", "uiautomator", "dump", path);
            var xml = await RunAsync(serial, ct, "shell", "cat", path);
            XDocument document;
            try { document = XDocument.Parse(xml); }
            catch (System.Xml.XmlException ex) { throw new InvalidOperationException("Android field readback is unavailable. Inspect the device manually.", ex); }
            var fields = document.Descendants("node").Where(n =>
                (string?)n.Attribute("focused") == "true" &&
                ((string?)n.Attribute("editable") == "true" || ((string?)n.Attribute("class"))?.EndsWith("EditText", StringComparison.Ordinal) == true)).ToArray();
            if (fields.Length != 1 || (string?)fields[0].Attribute("password") == "true")
                throw new InvalidOperationException("Focus one non-password editable Android field. Custom or protected fields may not expose text to UIAutomator.");
            return (string?)fields[0].Attribute("text") ?? "";
        }
        finally
        {
            // Cleanup must not replace a successful readback or an actionable device error.
            try { await RunAsync(serial, ct, "shell", "rm", "-f", path); } catch { }
        }
    }
}
