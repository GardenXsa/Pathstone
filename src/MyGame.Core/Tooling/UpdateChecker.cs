using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyGame.Core.Tooling;

/// <summary>
/// Checks GitHub releases for a newer version. Issue #54.
/// Notifies the user that a new version is available with a link.
/// No auto-download/swap — manual download (appropriate for free project).
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/GardenXsa/Pathstone/releases/latest";

    public static async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pathstone-UpdateChecker/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tagName)) return null;
            var versionStr = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out var latest)) return null;
            if (latest <= currentVersion) return null;
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            var body = doc.RootElement.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            return new UpdateInfo { Version = latest, TagName = tagName, ReleaseUrl = htmlUrl, ReleaseNotes = body };
        }
        catch { return null; }
    }
}

public sealed record UpdateInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string ReleaseUrl { get; init; }
    public string ReleaseNotes { get; init; } = "";
}
