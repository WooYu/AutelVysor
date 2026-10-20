using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class LegacyMinicapService
{
    private readonly string _adbPath;
    private readonly string _toolsRoot;

    public LegacyMinicapService(string adbPath, string toolsRoot)
    {
        _adbPath = adbPath;
        _toolsRoot = toolsRoot;
    }

    public bool CanAccelerate(AndroidDevice device)
    {
        if (device.ApiLevel != 19)
            return false;

        var abi = NormalizeAbi(device.Abi);
        if (abi is null)
            return false;

        var dir = AssetDirectory(abi);
        return File.Exists(Path.Combine(dir, "minicap")) && File.Exists(Path.Combine(dir, "minicap.so"));
    }

    public async Task<LegacyMinicapSession> StartAsync(AndroidDevice device, CancellationToken ct = default)
    {
        if (!CanAccelerate(device))
            throw new InvalidOperationException("No compatible minicap API 19 assets are available for this device ABI.");

        var abi = NormalizeAbi(device.Abi)!;
        var assetDir = AssetDirectory(abi);
        var remoteBinary = "/data/local/tmp/autel-minicap";
        var remoteLibrary = "/data/local/tmp/autel-minicap.so";

        await RunCheckedAsync(["-s", device.Serial, "push", Path.Combine(assetDir, "minicap"), remoteBinary], "Unable to push minicap.", ct);
        await RunCheckedAsync(["-s", device.Serial, "push", Path.Combine(assetDir, "minicap.so"), remoteLibrary], "Unable to push minicap.so.", ct);
        await RunCheckedAsync(["-s", device.Serial, "shell", "chmod", "755", remoteBinary], "Unable to prepare minicap executable.", ct);

        var (realWidth, realHeight) = await GetDisplaySizeAsync(device.Serial, ct);
        var rotation = await GetRotationDegreesAsync(device.Serial, ct);

        // Half-size projection is a deliberate performance choice for older Android 4.4 hardware.
        var virtualWidth = MakeEven(Math.Max(2, realWidth / 2));
        var virtualHeight = MakeEven(Math.Max(2, realHeight / 2));
        var projection = $"{realWidth}x{realHeight}@{virtualWidth}x{virtualHeight}/{rotation}";
        var command = $"LD_LIBRARY_PATH=/data/local/tmp {remoteBinary} -P {projection}";

        var test = await RunAsync(["-s", device.Serial, "shell", "sh", "-c", command + " -t"], ct);
        var testText = (test.Output + test.Error).Trim();
        if (test.ExitCode != 0 || !testText.Contains("OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(testText) ? "minicap compatibility test failed." : testText);

        var forward = await RunAsync(["-s", device.Serial, "forward", "tcp:0", "localabstract:minicap"], ct);
        if (forward.ExitCode != 0 || !int.TryParse(forward.Output.Trim(), out var port) || port <= 0)
            throw new InvalidOperationException("Unable to allocate local ADB forwarding port for minicap.");

        Process? remoteProcess = null;
        TcpClient? client = null;
        try
        {
            remoteProcess = ToolRunner.Launch(
                _adbPath,
                ["-s", device.Serial, "shell", "sh", "-c", command],
                Path.GetDirectoryName(_adbPath));

            client = new TcpClient();
            var connected = false;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 20 && !connected; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, ct);
                    connected = true;
                }
                catch (Exception ex) when (ex is SocketException or InvalidOperationException)
                {
                    lastError = ex;
                    client.Dispose();
                    client = new TcpClient();
                    await Task.Delay(120, ct);
                }
            }

            if (!connected)
                throw new InvalidOperationException("Unable to connect to the minicap stream.", lastError);

            var session = new LegacyMinicapSession(
                _adbPath,
                device.Serial,
                port,
                remoteProcess,
                client,
                realWidth,
                realHeight,
                virtualWidth,
                virtualHeight,
                rotation);

            await session.ReadGlobalHeaderAsync(ct);
            return session;
        }
        catch
        {
            try { client?.Dispose(); } catch { }
            try
            {
                if (remoteProcess is not null && !remoteProcess.HasExited)
                    remoteProcess.Kill(entireProcessTree: true);
            }
            catch { }
            remoteProcess?.Dispose();
            try { await RunAsync(["-s", device.Serial, "forward", "--remove", $"tcp:{port}"], CancellationToken.None); } catch { }
            throw;
        }
    }

    private string AssetDirectory(string abi)
        => Path.Combine(_toolsRoot, "legacy", "minicap", "api19", abi);

    private static string? NormalizeAbi(string abi)
    {
        abi = (abi ?? string.Empty).Trim().ToLowerInvariant();
        return abi switch
        {
            "armeabi-v7a" => "armeabi-v7a",
            "armeabi" => "armeabi-v7a",
            "x86" => "x86",
            _ => null
        };
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private async Task<(int Width, int Height)> GetDisplaySizeAsync(string serial, CancellationToken ct)
    {
        var wm = await RunAsync(["-s", serial, "shell", "wm", "size"], ct);
        var text = wm.Output + "\n" + wm.Error;
        var matches = Regex.Matches(text, @"(?<w>\d{2,5})x(?<h>\d{2,5})");
        if (matches.Count > 0)
        {
            var match = matches[^1];
            return (int.Parse(match.Groups["w"].Value), int.Parse(match.Groups["h"].Value));
        }

        var dumpsys = await RunAsync(["-s", serial, "shell", "dumpsys", "window"], ct);
        text = dumpsys.Output + "\n" + dumpsys.Error;
        foreach (var pattern in new[]
                 {
                     @"mUnrestrictedScreen=.*?(?<w>\d{2,5})x(?<h>\d{2,5})",
                     @"cur=(?<w>\d{2,5})x(?<h>\d{2,5})"
                 })
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return (int.Parse(match.Groups["w"].Value), int.Parse(match.Groups["h"].Value));
        }

        throw new InvalidOperationException("Unable to determine Android display size for minicap.");
    }

    private async Task<int> GetRotationDegreesAsync(string serial, CancellationToken ct)
    {
        var result = await RunAsync(["-s", serial, "shell", "dumpsys", "input"], ct);
        var match = Regex.Match(result.Output + result.Error, @"SurfaceOrientation:\s*(?<r>[0-3])", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups["r"].Value) * 90 : 0;
    }

    private async Task RunCheckedAsync(IEnumerable<string> args, string fallback, CancellationToken ct)
    {
        var result = await RunAsync(args, ct);
        if (result.ExitCode != 0)
        {
            var message = (result.Output + result.Error).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallback : message);
        }
    }

    private Task<(int ExitCode, string Output, string Error)> RunAsync(IEnumerable<string> args, CancellationToken ct)
        => ToolRunner.RunAsync(_adbPath, args, workingDirectory: Path.GetDirectoryName(_adbPath), ct: ct);
}

public sealed class LegacyMinicapSession : IDisposable
{
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly int _forwardPort;
    private readonly Process _remoteProcess;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _headerRead;
    private bool _disposed;

    internal LegacyMinicapSession(
        string adbPath,
        string serial,
        int forwardPort,
        Process remoteProcess,
        TcpClient client,
        int realWidth,
        int realHeight,
        int virtualWidth,
        int virtualHeight,
        int rotation)
    {
        _adbPath = adbPath;
        _serial = serial;
        _forwardPort = forwardPort;
        _remoteProcess = remoteProcess;
        _client = client;
        _stream = client.GetStream();
        RealWidth = realWidth;
        RealHeight = realHeight;
        VirtualWidth = virtualWidth;
        VirtualHeight = virtualHeight;
        Rotation = rotation;
    }

    public int RealWidth { get; private set; }
    public int RealHeight { get; private set; }
    public int VirtualWidth { get; private set; }
    public int VirtualHeight { get; private set; }
    public int Rotation { get; private set; }
    public int RemotePid { get; private set; }

    internal async Task ReadGlobalHeaderAsync(CancellationToken ct)
    {
        var prefix = await ReadExactlyAsync(2, ct);
        var headerSize = prefix[1];
        if (prefix[0] != 1 || headerSize < 24)
            throw new InvalidOperationException($"Unsupported minicap header: version={prefix[0]}, size={headerSize}.");

        var rest = await ReadExactlyAsync(headerSize - 2, ct);
        var header = new byte[headerSize];
        header[0] = prefix[0];
        header[1] = prefix[1];
        Buffer.BlockCopy(rest, 0, header, 2, rest.Length);

        RemotePid = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        RealWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6, 4));
        RealHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10, 4));
        VirtualWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4));
        VirtualHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18, 4));
        Rotation = header[22] * 90;
        _headerRead = true;
    }

    public async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        if (!_headerRead)
            await ReadGlobalHeaderAsync(ct);

        var lengthBytes = await ReadExactlyAsync(4, ct);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length is < 4 or > 32 * 1024 * 1024)
            throw new InvalidOperationException($"Invalid minicap frame size: {length}.");

        var frame = await ReadExactlyAsync((int)length, ct);
        if (frame.Length < 2 || frame[0] != 0xFF || frame[1] != 0xD8)
            throw new InvalidOperationException("minicap returned a non-JPEG frame.");
        return frame;
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read <= 0)
                throw new EndOfStreamException("minicap stream ended.");
            offset += read;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try
        {
            if (!_remoteProcess.HasExited)
                _remoteProcess.Kill(entireProcessTree: true);
        }
        catch { }
        try { _remoteProcess.Dispose(); } catch { }

        try
        {
            var args = new[] { "-s", _serial, "forward", "--remove", $"tcp:{_forwardPort}" };
            _ = ToolRunner.RunAsync(_adbPath, args, workingDirectory: Path.GetDirectoryName(_adbPath), ct: CancellationToken.None);
        }
        catch { }

        if (RemotePid > 0)
        {
            try
            {
                var args = new[] { "-s", _serial, "shell", "kill", RemotePid.ToString() };
                _ = ToolRunner.RunAsync(_adbPath, args, workingDirectory: Path.GetDirectoryName(_adbPath), ct: CancellationToken.None);
            }
            catch { }
        }

        GC.SuppressFinalize(this);
    }
}
