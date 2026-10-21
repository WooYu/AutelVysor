using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AutelVysor.Services;

/// <summary>Unmodified text streams and the process exit code; formatting is a UI concern.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    // ExitCode is always the actual host process status. Legacy ADB shell-v1
    // does not propagate the remote status, so callers can require separate proof.
    public bool RequiresRemoteExitCode { get; init; }
    public int? RemoteExitCode { get; init; }
    public int? EffectiveExitCode => ExitCode != 0 ? ExitCode : RequiresRemoteExitCode ? RemoteExitCode : ExitCode;
    public bool Succeeded => ExitCode == 0 && (!RequiresRemoteExitCode || RemoteExitCode == 0);

    public string FormatForDisplay(int maxStreamCharacters = int.MaxValue)
    {
        static string Stream(string value, int limit)
            => value.Length == 0 ? "(empty)" : value.Length > limit ? value[..limit] + "\n[truncated]" : value;
        if (maxStreamCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maxStreamCharacters));
        return "Exit code: " + (EffectiveExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable")
            + (RequiresRemoteExitCode ? "\nADB exit code: " + ExitCode.ToString(CultureInfo.InvariantCulture)
                + "\nRemote exit code: " + (RemoteExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable (could not be verified)") : "")
            + "\nstdout:\n" + Stream(Output, maxStreamCharacters)
            + "\nstderr:\n" + Stream(Error, maxStreamCharacters);
    }

    public void EnsureSuccess(string message = "ADB command failed.")
    {
        if (!Succeeded) throw new AdbCommandException(
            RequiresRemoteExitCode && RemoteExitCode is null ? "Remote shell exit status could not be verified. " + message : message, this);
    }
}

public sealed class AdbCommandException(string message, CommandResult result)
    : InvalidOperationException(message + "\n" + result.FormatForDisplay())
{
    public CommandResult Result { get; } = result;
}

public static class ToolRunner
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string file,
        IEnumerable<string> arguments,
        string? standardInput = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        using var p = Create(file, arguments, workingDirectory, redirectInput: standardInput is not null);
        p.Start();
        // A child may write before it reads stdin. Drain both output pipes first,
        // otherwise two full pipe buffers can leave parent and child waiting forever.
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            if (standardInput is not null)
            {
                await p.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
                p.StandardInput.Close();
            }
            await WaitAsync(p, ct);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch
        {
            // Cancellation can happen during stdin, before WaitAsync is reached.
            await TerminateAsync(p);
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            throw;
        }
    }

    public static async Task<(int ExitCode, byte[] Output, string Error)> RunBinaryAsync(
        string file,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        using var p = Create(file, arguments, workingDirectory, redirectInput: false);
        using var output = new MemoryStream();
        p.Start();
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await Task.WhenAll(copy, WaitAsync(p, ct));
        return (p.ExitCode, output.ToArray(), await stderr);
    }

    public static async Task<(int ExitCode, string Error)> RunToFileAsync(
        string file,
        IEnumerable<string> arguments,
        string outputPath,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        using var p = Create(file, arguments, workingDirectory, redirectInput: false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        await using var output = File.Create(outputPath);

        p.Start();
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await Task.WhenAll(copy, WaitAsync(p, ct));
        await output.FlushAsync(ct);
        return (p.ExitCode, await stderr);
    }

    public static Process Launch(string file, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(file) ?? AppContext.BaseDirectory
        };

        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);

        return Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {file}.");
    }

    private static Process Create(string file, IEnumerable<string> arguments, string? workingDirectory, bool redirectInput)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectInput,
                // adb emits UTF-8 regardless of the Windows console code page. This
                // affects StreamReaders/Writers only; binary BaseStream is untouched.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = redirectInput ? new UTF8Encoding(false) : null,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(file) ?? AppContext.BaseDirectory
            }
        };
        foreach (var arg in arguments)
            p.StartInfo.ArgumentList.Add(arg);
        return p;
    }

    private static async Task WaitAsync(Process p, CancellationToken ct)
    {
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(p);
            throw;
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { /* Retain the original command/cancellation failure if cleanup races exit. */ }
    }
}
