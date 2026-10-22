namespace AutelVysor.Services;

public sealed class AppNameService(AndroidBridge bridge)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public async Task<string?> ReadAsync(string serial, string package, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        var paths = await bridge.RunShellCommandAsync(serial, "pm path " + package, ct);
        var apk = paths.Split('\n', StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("package:", StringComparison.Ordinal))
            .Select(x => x[8..]).FirstOrDefault(x => x.EndsWith("/base.apk", StringComparison.Ordinal))
            ?? paths.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(x => x.StartsWith("package:", StringComparison.Ordinal))?[8..];
        if (apk is null) return null;
        var key = serial + "\n" + apk + "\n" + System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var temp = Path.Combine(Path.GetTempPath(), "autel-label-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            await bridge.PullAsync(serial, apk, temp, ct);
            var label = await Task.Run(() => ApkLabelReader.Read(temp), ct);
            if (!string.IsNullOrWhiteSpace(label)) Cache[key] = label;
            return label;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
}
