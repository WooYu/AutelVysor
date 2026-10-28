using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AutelVysor.Services;
using Microsoft.Win32;

namespace AutelVysor;

public partial class UnicodeSetupWindow : Window
{
    private readonly AdbKeyboardService _keyboard;
    private readonly string _serial;
    private readonly bool _clipboardSupported;
    private bool _busy, _activatedHere, _closing, _allowClose;
    private bool _forwardPassed, _reversePassed;
    private string? _androidSource;

    public UnicodeSetupWindow(AdbKeyboardService keyboard, string serial, string text, bool clipboardSupported)
    {
        InitializeComponent();
        AutelVysor.Services.DarkWindowChrome.Attach(this);
        _keyboard = keyboard;
        _serial = serial;
        _clipboardSupported = clipboardSupported;
        DeviceText.Text = "Device: " + serial;
        UnicodeText.Text = text;
        ClipboardSample.Text = "Autel 中文 café 😀 " + Guid.NewGuid().ToString("N")[..8];
        if (!clipboardSupported) AcceptanceText.Text = "Clipboard checks unavailable: compatibility mirror has no scrcpy clipboard protocol. Unicode IME input is separate.";
        Loaded += async (_, _) => await RunAsync(() => _keyboard.InspectAsync(_serial));
        Closing += Window_Closing;
    }

    private async Task RunAsync(Func<Task<string>> operation)
    {
        if (_busy || _closing) return;
        _busy = true;
        try { ResultText.Text = await operation(); }
        catch (Exception ex) { ResultText.Text = "Device " + _serial + ": " + ex.Message; }
        finally { _busy = false; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _keyboard.InspectAsync(_serial));

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Filter = "Android package (*.apk)|*.apk", Title = "Select a trusted local ADBKeyBoard APK" };
        if (picker.ShowDialog(this) == true) await RunAsync(() => _keyboard.InstallApkAsync(_serial, picker.FileName));
    }

    private async void Activate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        try
        {
            return await _keyboard.ActivateAsync(_serial);
        }
        finally
        {
            // ime set may take effect even if ADB disconnects before confirmation.
            // Closing must attempt safe restoration whenever this action created recovery.
            _activatedHere = _keyboard.HasRecovery(_serial);
        }
    });

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var result = await _keyboard.RestoreAsync(_serial);
        _activatedHere = false;
        return result;
    });

    private async void Send_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var result = await _keyboard.SendAsync(_serial, UnicodeText.Text);
        return result.Message + (result.ActualText is null ? "" : "\nActual Android field:\n" + result.ActualText);
    });

    private void RequireClipboard()
    {
        if (!_clipboardSupported) throw new InvalidOperationException("A modern scrcpy session is required. IME input cannot prove scrcpy clipboard synchronization.");
    }

    private void SetAcceptance() => AcceptanceText.Text =
        $"Device {_serial}: PC → Android {(_forwardPassed ? "PASS (actual field matched)" : "not passed")}; Android → PC {(_reversePassed ? "PASS (captured Android source matched PC clipboard)" : "not passed")}.";

    private async void CopySample_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        RequireClipboard();
        Clipboard.SetText(ClipboardSample.Text);
        _forwardPassed = false; SetAcceptance();
        return Task.FromResult("PC sample copied. Paste it into an empty Android field via scrcpy Ctrl+V, then click Verify PC → Android.");
    });

    private async void VerifyForward_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        RequireClipboard();
        _forwardPassed = false; SetAcceptance();
        var actual = await _keyboard.ReadFocusedTextAsync(_serial);
        _forwardPassed = actual == ClipboardSample.Text;
        SetAcceptance();
        return (_forwardPassed ? "PASS: exact Android field matches the PC sample." : "FAIL: Android field differs from the PC sample.") + "\nActual Android field:\n" + actual;
    });

    private async void CaptureSource_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        RequireClipboard();
        _androidSource = null; _reversePassed = false; SetAcceptance();
        var actual = await _keyboard.ReadFocusedTextAsync(_serial);
        if (string.IsNullOrWhiteSpace(actual) || actual == ClipboardSample.Text)
            throw new InvalidOperationException("Enter distinct, nonempty test text on Android before capturing the source.");
        Clipboard.SetText("Autel waiting for Android copy " + Guid.NewGuid().ToString("N"));
        _androidSource = actual;
        return "Actual Android source captured; PC clipboard replaced with a fresh marker. Now select/copy this Android text, then verify Android → PC.\nAndroid source:\n" + actual;
    });

    private async void VerifyReverse_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        RequireClipboard();
        _reversePassed = false; SetAcceptance();
        if (_androidSource is null) throw new InvalidOperationException("Capture the Android source first, then copy it on Android.");
        _reversePassed = Clipboard.ContainsText() && Clipboard.GetText() == _androidSource;
        SetAcceptance();
        return Task.FromResult(_reversePassed
            ? "PASS: the PC clipboard exactly matches the Android field captured before the Android copy action."
            : "FAIL: PC clipboard does not match the captured Android source. Copy the source text on Android with scrcpy active, then retry verification.");
    });

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_busy || _closing) { e.Cancel = true; return; }
        if (!_activatedHere || !_keyboard.HasRecovery(_serial)) return;
        e.Cancel = true;
        _closing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _keyboard.RestoreAsync(_serial, timeout.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not restore the original keyboard. Recovery is saved for this device. Reconnect, open Unicode setup and click Restore original keyboard.\n\n" + ex.Message,
                "Keyboard recovery needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _allowClose = true; Close(); }
    }
}
