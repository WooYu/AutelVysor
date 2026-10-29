using System.Diagnostics;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class ScrcpyService(string scrcpyPath, AppSettings settings)
{
    public string ExecutablePath { get; } = scrcpyPath;
    private readonly AppSettings _settings = settings;
    private readonly Dictionary<string, RecordingSession> _recordings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _recordingLock = new();
    private bool _stoppingRecordings;

    public bool HasRecordings
    {
        get { lock (_recordingLock) return _recordings.Count > 0; }
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await ToolRunner.RunAsync(ExecutablePath, ["--version"], workingDirectory: Path.GetDirectoryName(ExecutablePath), ct: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Unable to read the scrcpy version." : result.Error.Trim());
        return (result.Output + result.Error).Trim();
    }

    public Process StartExternal(AndroidDevice device, params string[] extraArguments)
    {
        var args = BaseArguments(device);
        ApplyRuntimeOptions(args, device);
        args.AddRange(extraArguments);
        return ToolRunner.Launch(ExecutablePath, args, Path.GetDirectoryName(ExecutablePath));
    }

    public async Task<ScrcpySession> StartEmbeddedAsync(AndroidDevice device, CancellationToken ct = default, string keyboardMode = "auto")
    {
        var args = BaseArguments(device);
        ApplyRuntimeOptions(args, device, keyboardMode);
        args.Add("--window-borderless");
        args.Add("--window-width=640");
        args.Add("--window-height=960");

        var process = ToolRunner.Launch(ExecutablePath, args, Path.GetDirectoryName(ExecutablePath));
        try
        {
            var hwnd = await NativeWindowLocator.WaitForMainWindowAsync(process, TimeSpan.FromSeconds(12), ct);
            return new ScrcpySession(process, hwnd);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    public Process StartRecording(AndroidDevice device, string path)
    {
        lock (_recordingLock)
        {
            if (_stoppingRecordings)
                throw new InvalidOperationException("Recordings are stopping. Wait before starting another.");
            if (_recordings.TryGetValue(device.Serial, out var existing))
            {
                if (!existing.Process.HasExited)
                    throw new InvalidOperationException("This device is already recording. Use Stop recordings first.");
                existing.Process.Dispose();
                _recordings.Remove(device.Serial);
            }
            var args = BaseArguments(device);
            // Keep an off-screen SDL window as a graceful WM_CLOSE endpoint.
            // --no-window has no CloseMainWindow target and killing it can leave
            // MP4 without its final index. This is a separate, non-interactive capture.
            args.AddRange(["--no-audio", "--no-control", "--window-borderless",
                "--window-width=64", "--window-height=64", "--window-x=-32000", "--window-y=-32000",
                "--record", path]);
            var process = ToolRunner.Launch(ExecutablePath, args, Path.GetDirectoryName(ExecutablePath));
            _recordings.Add(device.Serial, new RecordingSession(process, path));
            return process;
        }
    }

    public async Task StopRecordingsAsync()
    {
        RecordingSession[] recordings;
        lock (_recordingLock)
        {
            if (_stoppingRecordings)
                throw new InvalidOperationException("Recordings are already stopping.");
            _stoppingRecordings = true;
            recordings = _recordings.Values.ToArray();
        }
        var failures = new List<string>();
        try
        {
            // Stop every recorder even if one failed. Only these owned processes
            // are touched; mirroring and external ADB processes are independent.
            foreach (var recording in recordings)
            {
                try { await recording.StopAsync(); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
        }
        finally
        {
            lock (_recordingLock)
            {
                foreach (var (serial, recording) in _recordings.ToArray())
                {
                    // Failed OS termination/inspection must not lose ownership.
                    // Retain live or unknown sessions so duplicates stay blocked
                    // and the user can retry Stop before closing the application.
                    try
                    {
                        if (!recording.Process.HasExited) continue;
                    }
                    catch { continue; }
                    recording.Process.Dispose();
                    _recordings.Remove(serial);
                }
                _stoppingRecordings = false;
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private void ApplyRuntimeOptions(List<string> args, AndroidDevice device, string keyboardMode = "auto")
    {
        var hardware = keyboardMode.ToLowerInvariant() switch
        {
            "uhid" => true,
            "sdk" => false,
            "sdk-text" => false,
            "auto" => device.ApiLevel >= 28,
            _ => throw new ArgumentException("Select Auto, Hardware, Compatible or Text keyboard mode.", nameof(keyboardMode))
        };
        args.Add(hardware ? "--keyboard=uhid" : "--keyboard=sdk");
        if (!hardware) args.Add(keyboardMode.Equals("sdk-text", StringComparison.OrdinalIgnoreCase) ? "--prefer-text" : "--raw-key-events");
        switch ((_settings.MirrorQuality ?? "Standard").Trim().ToLowerInvariant())
        {
            case "smooth":
                args.Add("--max-size=1280");
                args.Add("--video-bit-rate=4M");
                args.Add("--max-fps=30");
                break;
            case "high":
                args.Add("--max-size=2560");
                args.Add("--video-bit-rate=12M");
                args.Add("--max-fps=60");
                break;
            default:
                args.Add("--max-size=1920");
                args.Add("--video-bit-rate=8M");
                args.Add("--max-fps=60");
                break;
        }

        if (!_settings.EnableAudio)
        {
            args.Add("--no-audio");
        }
        else if (_settings.KeepDeviceAudio)
        {
            if (device.ApiLevel >= 33)
            {
                // Android 13+: duplicate app playback to the PC while preserving
                // normal device/Bluetooth playback.
                args.Add("--audio-dup");
            }
            else if (device.ApiLevel is >= 30 and < 33)
            {
                // Android 11/12 scrcpy output capture mutes playback on the device.
                // Prefer the phone/Bluetooth route when the user requests device audio.
                args.Add("--no-audio");
            }
        }

        if (!_settings.ClipboardSync)
            args.Add("--no-clipboard-autosync");

        if (_settings.TurnScreenOffOnMirror)
        {
            args.Add("--turn-screen-off");
            args.Add("--stay-awake");
        }
    }

    private static List<string> BaseArguments(AndroidDevice device)
        =>
        [
            "-s", device.Serial,
            $"--window-title=Autel Vysor - {device.DisplayName}",
            "--no-terminal-title",
            "--push-target=/sdcard/Download/"
        ];
}

