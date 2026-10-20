using System.Diagnostics;

namespace AutelVysor.Services;

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

        if (standardInput is not null)
        {
            await p.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            await p.StandardInput.FlushAsync(ct);
            p.StandardInput.Close();
        }

        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await WaitAsync(p, ct);
        return (p.ExitCode, await stdout, await stderr);
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
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
            throw;
        }
    }
}
