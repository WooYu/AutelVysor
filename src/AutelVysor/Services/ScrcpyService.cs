using System.Diagnostics;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class ScrcpyService(string scrcpyPath, AppSettings settings)
{
    public string ExecutablePath { get; } = scrcpyPath;
    private readonly AppSettings _settings = settings;

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

    public async Task<ScrcpySession> StartEmbeddedAsync(AndroidDevice device, CancellationToken ct = default)
    {
        var args = BaseArguments(device);
        ApplyRuntimeOptions(args, device);
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
        var args = BaseArguments(device);
        args.Add("--no-window");
        args.Add("--no-audio");
        args.Add("--record");
        args.Add(path);
        return ToolRunner.Launch(ExecutablePath, args, Path.GetDirectoryName(ExecutablePath));
    }

    private void ApplyRuntimeOptions(List<string> args, AndroidDevice device)
    {
        // SDK mode is compatible with the widest range of Android devices.
        // Prefer text events for typed characters (VIN, account, URLs) rather than
        // translating every letter to a hardware-style key event.
        args.Add("--keyboard=sdk");
        args.Add("--prefer-text");
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
