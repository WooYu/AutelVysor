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
            args.Add("--no-audio");

        if (!_settings.ClipboardSync)
            args.Add("--no-clipboard-autosync");
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
