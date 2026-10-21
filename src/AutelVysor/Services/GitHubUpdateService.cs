using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AutelVysor.Services;

public sealed record AutelReleaseInfo(Version Version, string Tag, string Url, bool IsPrerelease);

/// <summary>
/// Only reads public GitHub release metadata. Never silently downloads or executes binaries.
/// </summary>
public sealed class GitHubUpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/WooYu/AutelVysor/releases?per_page=15";
    private const string OfficialReleasesUrl = "https://github.com/WooYu/AutelVysor/releases";

    public async Task<AutelReleaseInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutelVysor", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.GetAsync(ApiUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        AutelReleaseInfo? latest = null;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                continue;
            var prerelease = item.GetProperty("prerelease").GetBoolean();
            var url = item.GetProperty("html_url").GetString() ?? "";
            if (!url.StartsWith(OfficialReleasesUrl + "/", StringComparison.Ordinal))
                continue;

            if (version > current && (latest is null || version > latest.Version))
                latest = new AutelReleaseInfo(version, tag, url, prerelease);
        }
        return latest;
    }
}
