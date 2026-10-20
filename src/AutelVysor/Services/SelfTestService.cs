using System.Runtime.InteropServices;

namespace AutelVysor.Services;

internal static class SelfTestService
{
    public static int Run()
    {
        try
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var adb = Path.Combine(tools, "adb.exe");
            var scrcpy = Path.Combine(tools, "scrcpy.exe");

            if (!OperatingSystem.IsWindows())
                return 11;
            if (!Environment.Is64BitProcess)
                return 12;
            if (!File.Exists(adb))
                return 21;
            if (!File.Exists(scrcpy))
                return 22;

            var adbVersion = ToolRunner.RunAsync(adb, ["version"], workingDirectory: tools)
                .GetAwaiter().GetResult();
            if (adbVersion.ExitCode != 0)
                return 31;

            var scrcpyVersion = ToolRunner.RunAsync(scrcpy, ["--version"], workingDirectory: tools)
                .GetAwaiter().GetResult();
            if (scrcpyVersion.ExitCode != 0)
                return 32;

            var report = Path.Combine(AppContext.BaseDirectory, "AutelVysor-self-test.txt");
            File.WriteAllText(report, string.Join(Environment.NewLine,
            [
                "Autel Vysor self-test: PASS",
                $"Runtime: {RuntimeInformation.FrameworkDescription}",
                $"OS: {RuntimeInformation.OSDescription}",
                $"Architecture: {RuntimeInformation.ProcessArchitecture}",
                "ADB: " + (adbVersion.Output + adbVersion.Error).Trim().Replace(Environment.NewLine, " | "),
                "scrcpy: " + (scrcpyVersion.Output + scrcpyVersion.Error).Trim().Replace(Environment.NewLine, " | "),
                $"UTC: {DateTimeOffset.UtcNow:O}"
            ]));
            return 0;
        }
        catch
        {
            return 99;
        }
    }
}
