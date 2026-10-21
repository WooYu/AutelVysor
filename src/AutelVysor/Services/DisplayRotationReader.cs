using System.Globalization;
using System.Text.RegularExpressions;

namespace AutelVysor.Services;

/// <summary>Reads display 0's actual angle, independently of the user's rotation lock.</summary>
internal static class DisplayRotationReader
{
    public static async Task<int> ReadAsync(string adbPath, string serial, CancellationToken ct)
    {
        var window = await ToolRunner.RunAsync(adbPath, ["-s", serial, "shell", "dumpsys", "window", "displays"], ct: ct);
        if (window.ExitCode == 0)
        {
            // Modern Android no longer consistently includes SurfaceOrientation in
            // dumpsys input. Restrict window diagnostics to the default display:
            // a secondary/virtual display may have a different rotation.
            var headers = Regex.Matches(window.Output,
                @"^\s*Display:\s*mDisplayId=(?<id>\d+)\b[^\r\n]*", RegexOptions.Multiline);
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Groups["id"].Value != "0") continue;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : window.Output.Length;
                var section = window.Output[headers[i].Index..end];
                var match = Regex.Match(section, @"\bDisplayFrames\b[^\r\n]*\br=([0-3])\b");
                if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 90;
                match = Regex.Match(section, @"\bm(?:Current)?Rotation=(?:ROTATION_)?(270|180|90|[0-3])\b");
                if (match.Success)
                {
                    var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    return value <= 3 ? value * 90 : value;
                }
            }
        }

        // Older Android (including API 19) reports this field for display 0.
        var input = await ToolRunner.RunAsync(adbPath, ["-s", serial, "shell", "dumpsys", "input"], ct: ct);
        var legacy = Regex.Match(input.Output, @"\bSurfaceOrientation:\s*([0-3])\b", RegexOptions.IgnoreCase);
        if (input.ExitCode == 0 && legacy.Success)
            return int.Parse(legacy.Groups[1].Value, CultureInfo.InvariantCulture) * 90;

        // user_rotation is a preference, NOT display telemetry (even while locked,
        // the foreground app may impose a different orientation).
        throw new InvalidOperationException("Actual Android display rotation is unavailable.");
    }
}
