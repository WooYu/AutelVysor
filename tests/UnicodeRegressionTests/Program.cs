using System.Text;
using AutelVysor.Services;

var tests = new (string, Func<Task>)[] {
    ("Original IME survives repeat activation and restart", Recovery),
    ("Later user IME selection is preserved", UserSelection),
    ("Recovery is isolated per device", DeviceIsolation),
    ("Failed switch retains durable recovery", FailedSwitch),
    ("No receiver does not become successful Unicode input", MissingReceiver),
    ("Receiver must belong to the keyboard", WrongReceiver),
    ("Unicode uses package scoped UTF-8 Base64 and verifies Android text", Unicode),
    ("Broadcast completion without insertion stays unverified", NoInsertion),
    ("Diagnostic failure cannot become receiver availability", DiagnosticFailure),
    ("Readback requires a focused editable field", FocusedField),
    ("Command errors preserve actual exit code and both streams", CommandFailure),
    ("Textual command rejection preserves zero process status", SemanticFailure),
    ("Failed restore retains recovery", FailedRestore),
    ("Broadcast history is not a registered receiver", HistoricalReceiver),
    ("Canceled switch retains durable recovery", CanceledSwitch),
    ("Hung native command is bounded and retains recovery", HungSwitch),
    ("Already matching Android text cannot verify a no-op broadcast", ExistingMatchingText),
    ("Nonempty target is not sent another Unicode payload", ExistingOtherText)
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + " — " + ex.Message); }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task MustFail(Func<Task> action, string message)
{
    try { await action(); } catch (InvalidOperationException) { return; }
    throw new Exception(message);
}
static async Task Recovery()
{
    using var device = new Device();
    var service = device.Service();
    await service.ActivateAsync("one");
    Check(device.JournalExistedBeforeSwitch, "IME was changed before original was durably saved");
    await service.ActivateAsync("one");
    var restarted = device.Service();
    Check(restarted.HasRecovery("one"), "Restart lost recovery journal");
    await restarted.RestoreAsync("one");
    Check(device.Current["one"] == Device.Original, "Original IME was overwritten on repeated activation");
    Check(!restarted.HasRecovery("one"), "Verified restore retained stale recovery");
}
static async Task UserSelection()
{
    using var device = new Device(); var service = device.Service();
    await service.ActivateAsync("one");
    device.Current["one"] = "user.chosen/.Keyboard";
    await service.RestoreAsync("one");
    Check(device.Current["one"] == "user.chosen/.Keyboard", "Restore replaced user's later choice");
}
static async Task DeviceIsolation()
{
    using var device = new Device(); var service = device.Service();
    device.Current["two"] = "other.original/.IME";
    await service.ActivateAsync("one"); await service.ActivateAsync("two");
    await service.RestoreAsync("one");
    Check(service.HasRecovery("two"), "Restoring one device erased another journal");
    await service.RestoreAsync("two");
    Check(device.Current["two"] == "other.original/.IME", "Device original keyboard was mixed up");
}
static async Task FailedSwitch()
{
    using var device = new Device { FailSet = true }; var service = device.Service();
    await MustFail(() => service.ActivateAsync("one"), "Failed switch reported success");
    Check(service.HasRecovery("one"), "Failed switch lost recovery");
}
static async Task MissingReceiver()
{
    using var device = new Device { ReceiverDump = "Registered Receivers:\n  (nothing)" }; var service = device.Service();
    device.Current["one"] = AdbKeyboardService.Component;
    await MustFail(() => service.SendAsync("one", "你好"), "Receiverless result=0 reported success");
    Check(!device.BroadcastSent, "Sent broadcast without registered receiver");
}
static async Task WrongReceiver()
{
    using var device = new Device { ReceiverDump = "Registered Receivers:\nReceiverList{abc rogue.app/123/u0 remote:xyz}\n  Action: \"ADB_INPUT_B64\"\nReceiverList{def com.android.adbkeyboard/100/u0 remote:xyz}\n  Action: \"OTHER\"" };
    device.Current["one"] = AdbKeyboardService.Component;
    await MustFail(() => device.Service().SendAsync("one", "你好"), "Unrelated receiver accepted");
}
static async Task Unicode()
{
    const string text = "中文 café 😀 & 'quoted'\n第二行";
    using var device = new Device { InsertBroadcast = true }; var service = device.Service();
    await service.ActivateAsync("one");
    var result = await service.SendAsync("one", text);
    Check(result.Verified && result.ActualText == text, "Exact Android readback did not verify");
    Check(device.LastBase64 == Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "UTF-8 payload corrupted");
    Check(device.PackageScoped, "Broadcast not restricted to expected package");
}
static async Task NoInsertion()
{
    using var device = new Device(); device.Current["one"] = AdbKeyboardService.Component;
    var result = await device.Service().SendAsync("one", "你好");
    Check(!result.Verified, "Broadcast result=0 fabricated insertion");
}
static async Task DiagnosticFailure()
{
    using var device = new Device { FailDump = true }; device.Current["one"] = AdbKeyboardService.Component;
    await MustFail(() => device.Service().SendAsync("one", "你好"), "Failed diagnostics allowed send");
}
static async Task FocusedField()
{
    using var device = new Device { Editable = false, Text = "你好" };
    await MustFail(() => device.Service().ReadFocusedTextAsync("one"), "Non-editable node passed as field");
}
static async Task CommandFailure()
{
    using var device = new Device { FailSet = true };
    try { await device.Service().ActivateAsync("one"); }
    catch (AdbCommandException ex)
    {
        Check(ex.Result.ExitCode == 1 && ex.Result.Output == "switch diagnostic" && ex.Result.Error == "Permission denied", "Command evidence changed");
        return;
    }
    throw new Exception("Command failure omitted structured exit code and both streams");
}
static async Task SemanticFailure()
{
    using var device = new Device { RejectEnable = true };
    try { await device.Service().ActivateAsync("one"); }
    catch (AdbCommandException ex)
    {
        Check(ex.Result.ExitCode == 0 && ex.Result.Output.Contains("Error:") && ex.Result.Error == "policy detail", "Semantic error evidence changed");
        return;
    }
    throw new Exception("Textual error did not retain actual zero process status");
}
static async Task FailedRestore()
{
    using var device = new Device(); var service = device.Service();
    await service.ActivateAsync("one"); device.FailSet = true;
    await MustFail(() => service.RestoreAsync("one"), "Failed restore reported success");
    Check(service.HasRecovery("one"), "Failed restore erased recovery");
}
static async Task HistoricalReceiver()
{
    using var device = new Device { ReceiverDump = "Registered Receivers:\n  (none)\nHistorical broadcasts:\nReceiverList{abc com.android.adbkeyboard/123/u0 remote:xyz}\n  Action: \"ADB_INPUT_B64\"" };
    device.Current["one"] = AdbKeyboardService.Component;
    await MustFail(() => device.Service().SendAsync("one", "你好"), "Broadcast history was accepted as a live receiver");
}
static async Task CanceledSwitch()
{
    using var device = new Device { BlockSet = true }; var service = device.Service();
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try { await service.ActivateAsync("one", cancel.Token); throw new Exception("Canceled switch reported success"); }
    catch (OperationCanceledException) { }
    Check(service.HasRecovery("one"), "Cancellation erased recovery after switching began");
}
static async Task HungSwitch()
{
    using var device = new Device { BlockSet = true }; var service = device.Service(TimeSpan.FromMilliseconds(30));
    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try { await service.ActivateAsync("one", watchdog.Token); throw new Exception("Hung command reported success"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("timed out"), "Timeout did not explain failure"); }
    Check(service.HasRecovery("one"), "Native timeout erased recovery");
}
static async Task ExistingMatchingText()
{
    using var device = new Device { Text = "你好" }; device.Current["one"] = AdbKeyboardService.Component;
    var result = await device.Service().SendAsync("one", "你好");
    Check(!result.Verified, "Unchanged already-matching text falsely verified insertion");
    Check(!device.BroadcastSent, "Already matching field was sent a duplicate payload");
}
static async Task ExistingOtherText()
{
    using var device = new Device { Text = "existing field" }; device.Current["one"] = AdbKeyboardService.Component;
    var result = await device.Service().SendAsync("one", "你好");
    Check(!result.Verified && !device.BroadcastSent, "Nonempty target was modified despite empty-field requirement");
    Check(device.Text == "existing field", "Existing target text changed");
}

sealed class Device : IDisposable
{
    public const string Original = "original.keyboard/.IME";
    readonly string journal = Path.Combine(Path.GetTempPath(), "autel-unicode-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string> Current = new() { ["one"] = Original };
    public string ReceiverDump = "Registered Receivers:\n  * ReceiverList{abc 123 com.android.adbkeyboard/10123/u0 remote:123}\n    Filter #0: BroadcastFilter{123}\n      Action: \"ADB_INPUT_B64\"";
    public bool FailSet, FailDump, RejectEnable, BlockSet, InsertBroadcast, BroadcastSent, PackageScoped, JournalExistedBeforeSwitch;
    public bool Editable = true;
    public string Text = "", LastBase64 = "";
    public AdbKeyboardService Service(TimeSpan? timeout = null) => new("unused", journal, Run, timeout);
    async Task<(int ExitCode, string Output, string Error)> Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        var a = args.ToArray(); var serial = a[1]; var command = string.Join(" ", a.Skip(2));
        (int, string, string) r = (0, "", "");
        if (command == "shell settings get secure default_input_method") r = (0, Current[serial], "");
        else if (command == "shell pm path com.android.adbkeyboard") r = (0, "package:/data/app/base.apk", "");
        else if (command == "shell ime list -a -s") r = (0, AdbKeyboardService.Component + "\n" + Original, "");
        else if (command.StartsWith("shell ime set "))
        {
            JournalExistedBeforeSwitch = Directory.Exists(journal) && Directory.GetFiles(journal, "*.json").Length > 0;
            if (BlockSet) await Task.Delay(Timeout.Infinite, ct);
            if (FailSet) r = (1, "switch diagnostic", "Permission denied"); else Current[serial] = a[^1];
        }
        else if (command.StartsWith("shell ime enable ") && RejectEnable) r = (0, "Error: keyboard blocked by policy", "policy detail");
        else if (command == "shell dumpsys activity broadcasts") r = FailDump ? (1, "", "denied") : (0, ReceiverDump, "");
        else if (command.StartsWith("shell am broadcast "))
        {
            BroadcastSent = true; LastBase64 = a[^1];
            PackageScoped = command.Contains("-p com.android.adbkeyboard");
            if (InsertBroadcast) Text = Encoding.UTF8.GetString(Convert.FromBase64String(LastBase64));
            r = (0, "Broadcast completed: result=0", "");
        }
        else if (command.StartsWith("shell cat "))
            r = (0, "<?xml version=\"1.0\"?><hierarchy><node focused=\"true\" class=\"" + (Editable ? "android.widget.EditText" : "android.widget.TextView") + "\" text=\"" + System.Security.SecurityElement.Escape(Text).Replace("\n", "&#10;") + "\" /></hierarchy>", "");
        return r;
    }
    public void Dispose() { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
}
