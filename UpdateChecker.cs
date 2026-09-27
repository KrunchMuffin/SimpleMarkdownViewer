using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMarkdownViewer;

/// <summary>
/// Looks up the latest published release on GitHub. Only the public releases API is
/// contacted; nothing about the user or their files is sent.
/// </summary>
internal static class UpdateChecker
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/KrunchMuffin/SimpleMarkdownViewer/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/KrunchMuffin/SimpleMarkdownViewer/releases/latest";
    private const string RepoUrlPrefix = "https://github.com/KrunchMuffin/SimpleMarkdownViewer/";

    public sealed record ReleaseInfo(Version Version, string PageUrl);

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SimpleMarkdownViewer/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        await using var stream = await http.GetStreamAsync(LatestReleaseApiUrl, cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
            throw new FormatException($"Unrecognized release tag '{tag}'");

        // Only ever send the user to this repository's pages
        var pageUrl = json.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        if (pageUrl == null || !pageUrl.StartsWith(RepoUrlPrefix, StringComparison.OrdinalIgnoreCase))
            pageUrl = ReleasesPageUrl;

        return new ReleaseInfo(version, pageUrl);
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text.Trim().TrimStart('v', 'V'), out var parsed))
            return false;
        version = Normalize(parsed);
        return true;
    }

    public static bool IsNewerThanCurrent(Version version) => Normalize(version) > CurrentVersion;

    // Compare as major.minor.patch; "1.5.1" and "1.5.1.0" are the same release
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
