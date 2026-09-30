using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SimpleMarkdownViewer;

/// <summary>
/// Looks up the latest published release on GitHub and, for copies installed by our
/// Windows installer, downloads and runs the new installer. Only this repository's
/// releases are contacted; nothing about the user or their files is sent.
/// </summary>
internal static class UpdateChecker
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/KrunchMuffin/SimpleMarkdownViewer/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/KrunchMuffin/SimpleMarkdownViewer/releases/latest";
    private const string RepoUrlPrefix = "https://github.com/KrunchMuffin/SimpleMarkdownViewer/";
    private const string DownloadUrlPrefix = RepoUrlPrefix + "releases/download/";

    // Inno Setup's uninstall key for installer.iss's AppId
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3D2A1B-5C4E-4F6A-9B8D-7E2F1A3C5D4E}_is1";

    public sealed record ReleaseInfo(Version Version, string PageUrl, InstallerAsset? WindowsInstaller);

    /// <param name="Sha256">Lowercase hex digest GitHub computed for the uploaded file</param>
    public sealed record InstallerAsset(string Name, string DownloadUrl, string Sha256);

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Whether this copy was put in place by our Windows installer, so running a newer
    /// installer updates it. Portable copies, macOS and Linux get the release page instead.
    /// </summary>
    public static bool CanInstallUpdates { get; } = IsInstalledCopy();

    public static async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(10));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        await using var stream = await http.GetStreamAsync(LatestReleaseApiUrl, cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
            throw new FormatException($"Unrecognized release tag '{tag}'");

        // Only ever send the user to this repository's pages
        var pageUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        if (pageUrl == null || !pageUrl.StartsWith(RepoUrlPrefix, StringComparison.OrdinalIgnoreCase))
            pageUrl = ReleasesPageUrl;

        return new ReleaseInfo(version, pageUrl, FindWindowsInstaller(root));
    }

    private static InstallerAsset? FindWindowsInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name == null
                || !name.StartsWith("SimpleMarkdownViewer-Setup-", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;

            // Without a published checksum there's nothing to verify the download against
            if (downloadUrl == null || !downloadUrl.StartsWith(DownloadUrlPrefix, StringComparison.OrdinalIgnoreCase)
                || digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;

            return new InstallerAsset(name, downloadUrl, digest["sha256:".Length..].ToLowerInvariant());
        }
        return null;
    }

    /// <summary>
    /// Downloads the installer to a temp folder and checks it against the release's
    /// SHA-256 digest. A verified copy from an earlier attempt is reused.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(InstallerAsset installer, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SimpleMarkdownViewer-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, installer.Name);

        if (File.Exists(path) && await HashFileAsync(path, cancellationToken) == installer.Sha256)
            return path;

        using var http = CreateClient(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.AppendData(buffer, 0, read);
                received += read;
                if (total > 0)
                    progress?.Report((double)received / total.Value);
            }
        }

        if (Convert.ToHexStringLower(sha.GetHashAndReset()) != installer.Sha256)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("The download didn't match the checksum published with the release.");
        }
        return path;
    }

    /// <summary>
    /// Runs the installer without its wizard. Windows asks for administrator permission
    /// first; declining throws a Win32Exception with error 1223. The installer closes this
    /// app if it's still running and starts it again when it's done.
    /// </summary>
    public static void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1",
            UseShellExecute = true
        });
    }

    /// <summary>Removes installers left from earlier updates.</summary>
    public static void DeleteDownloadedInstallers()
    {
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "SimpleMarkdownViewer-Update"), recursive: true); }
        catch { }
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

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SimpleMarkdownViewer/{CurrentVersion}");
        return http;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private static bool IsInstalledCopy()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
                return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
