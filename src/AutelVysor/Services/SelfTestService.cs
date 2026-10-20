using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutelVysor.Services;

internal static class SelfTestService
{
    private const int ToolTimeoutMs = 10_000;

    public static int Run()
    {
        var report = Path.Combine(AppContext.BaseDirectory, "AutelVysor-self-test.txt");
        var lines = new List<string>
        {
            "Autel Vysor self-test",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"OS: {RuntimeInformation.OSDescription}",
            $"Architecture: {RuntimeInformation.ProcessArchitecture}",
            $"UTC: {DateTimeOffset.UtcNow:O}"
        };

        int Finish(int code, string status)
        {
            lines.Add($"Result: {status}");
            lines.Add($"ExitCode: {code}");
            try { File.WriteAllLines(report, lines); } catch { }
            return code;
        }

        try
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var adb = Path.Combine(tools, "adb.exe");
            var scrcpy = Path.Combine(tools, "scrcpy.exe");

            if (!OperatingSystem.IsWindows())
                return Finish(11, "FAIL - Windows required");
            if (!Environment.Is64BitProcess)
                return Finish(12, "FAIL - x64 process required");
            if (!File.Exists(adb))
                return Finish(21, "FAIL - adb.exe missing");
            if (!File.Exists(scrcpy))
                return Finish(22, "FAIL - scrcpy.exe missing");

            var adbVersion = RunTool(adb, ["version"], tools);
            lines.Add("ADB: " + Format(adbVersion));
            if (adbVersion.TimedOut)
                return Finish(33, "FAIL - adb version timed out");
            if (adbVersion.ExitCode != 0)
                return Finish(31, "FAIL - adb version");

            var scrcpyVersion = RunTool(scrcpy, ["--version"], tools);
            lines.Add("scrcpy: " + Format(scrcpyVersion));
            if (scrcpyVersion.TimedOut)
                return Finish(34, "FAIL - scrcpy version timed out");
            if (scrcpyVersion.ExitCode != 0)
                return Finish(32, "FAIL - scrcpy version");

            return Finish(0, "PASS");
        }
        catch (Exception ex)
        {
            lines.Add(ex.ToString());
            return Finish(99, "FAIL - unexpected exception");
        }
    }

    private static ToolProbeResult RunTool(string file, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();

        if (!process.WaitForExit(ToolTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(2_000); } catch { }
            return new ToolProbeResult(124, string.Empty, "Timed out", true);
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        return new ToolProbeResult(process.ExitCode, output, error, false);
    }

    private static string Format(ToolProbeResult result)
    {
        var text = (result.Output + Environment.NewLine + result.Error)
            .Trim()
            .Replace(Environment.NewLine, " | ");
        return $"exit={result.ExitCode}; timeout={result.TimedOut}; {text}";
    }

    private sealed record ToolProbeResult(int ExitCode, string Output, string Error, bool TimedOut);
}
