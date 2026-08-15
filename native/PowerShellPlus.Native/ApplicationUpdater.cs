using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed record UpdateRelease(
    Version Version,
    string TagName,
    string DisplayVersion,
    string Notes,
    Uri ReleasePage,
    Uri InstallerUri,
    long InstallerSize,
    string InstallerSha256);

internal sealed record UpdateCheckResult(UpdateRelease? Release, bool IsUpdateAvailable, string CurrentVersion);

internal static class ApplicationUpdater
{
    internal const string InstallerAssetName = "PowerShellPlus-Setup-x64.exe";
    internal const long MaximumInstallerBytes = 350L * 1024 * 1024;
    internal static readonly Uri LatestReleaseApiUri = new("https://api.github.com/repos/jordanw0204-rgb/PowerShellPlus/releases/latest");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    internal static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version is { } value
        ? new Version(value.Major, value.Minor, Math.Max(0, value.Build))
        : new Version(0, 0, 0);
    internal static string CurrentVersionText => CurrentVersion.ToString(3);

    internal static async Task<UpdateCheckResult> CheckLatestAsync(Version? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var installed = currentVersion ?? CurrentVersion;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        using var request = CreateGitHubRequest(LatestReleaseApiUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(null, false, installed.ToString(3));
        response.EnsureSuccessStatusCode();
        var release = ParseLatestRelease(await response.Content.ReadAsStringAsync(cancellationToken));
        return new UpdateCheckResult(release, release.Version > installed, installed.ToString(3));
    }

    internal static UpdateRelease ParseLatestRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new InvalidDataException("GitHub returned a draft instead of a published release.");
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            throw new InvalidDataException("GitHub returned a prerelease instead of a stable release.");

        var tag = RequiredString(root, "tag_name");
        if (!TryParseReleaseVersion(tag, out var version))
            throw new InvalidDataException($"The latest release tag '{tag}' is not a supported stable version.");
        var releasePage = new Uri(RequiredString(root, "html_url"), UriKind.Absolute);
        if (!IsOfficialReleasePage(releasePage))
            throw new InvalidDataException("The release page was not hosted by the official PowerShellPlus GitHub repository.");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The latest release did not include downloadable assets.");
        JsonElement? installerAsset = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            {
                installerAsset = asset;
                break;
            }
        }
        if (installerAsset is not { } installer)
            throw new InvalidDataException($"The latest release is missing {InstallerAssetName}.");

        var installerUri = new Uri(RequiredString(installer, "browser_download_url"), UriKind.Absolute);
        if (!IsOfficialInstallerUri(installerUri, tag))
            throw new InvalidDataException("The installer URL was outside the official PowerShellPlus GitHub release.");
        var size = installer.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
        if (size <= 0 || size > MaximumInstallerBytes)
            throw new InvalidDataException("The installer asset size was missing or outside the updater safety limit.");
        var digest = RequiredString(installer, "digest");
        if (!TryNormalizeSha256Digest(digest, out var sha256))
            throw new InvalidDataException("GitHub did not provide a valid SHA-256 digest for the installer.");

        var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;
        return new UpdateRelease(version, tag, version.ToString(3), NormalizeReleaseNotes(body), releasePage,
            installerUri, size, sha256);
    }

    internal static async Task<string> DownloadAndVerifyInstallerAsync(UpdateRelease release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerShellPlus", "updates", release.TagName);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, InstallerAssetName);
        if (File.Exists(finalPath) && VerifySha256(finalPath, release.InstallerSha256))
        {
            progress?.Report(1);
            return finalPath;
        }

        var partialPath = finalPath + ".partial";
        try
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            var currentUri = release.InstallerUri;
            HttpResponseMessage? response = null;
            try
            {
                for (var redirect = 0; redirect <= 5; redirect++)
                {
                    using var request = CreateGitHubRequest(currentUri);
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                    {
                        var location = response.Headers.Location
                            ?? throw new InvalidDataException("GitHub returned an installer redirect without a destination.");
                        currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        response.Dispose();
                        response = null;
                        if (!IsAllowedDownloadUri(currentUri))
                            throw new InvalidDataException("The installer download redirected outside GitHub's release infrastructure.");
                        continue;
                    }
                    break;
                }
                if (response is null) throw new HttpRequestException("GitHub exceeded the installer redirect limit.");
                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > MaximumInstallerBytes || contentLength is > 0 && contentLength != release.InstallerSize)
                    throw new InvalidDataException("The downloaded installer size did not match GitHub's release metadata.");

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumInstallerBytes || total > release.InstallerSize)
                        throw new InvalidDataException("The installer download exceeded GitHub's declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(Math.Min(1, (double)total / release.InstallerSize));
                }
                await destination.FlushAsync(cancellationToken);
                if (total != release.InstallerSize)
                    throw new InvalidDataException("The installer download ended before GitHub's declared size was reached.");
            }
            finally { response?.Dispose(); }

            if (!VerifySha256(partialPath, release.InstallerSha256))
                throw new InvalidDataException("The installer SHA-256 did not match GitHub's release digest. Nothing was opened.");
            File.Move(partialPath, finalPath, true);
            progress?.Report(1);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            throw;
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath) => new(installerPath)
    {
        Arguments = "/SILENT /SP- /CLOSEAPPLICATIONS /NORESTART /UPDATE=1",
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
    };

    internal static bool VerifySha256(string path, string expectedSha256)
    {
        if (!File.Exists(path) || !TryNormalizeSha256Digest(expectedSha256, out var normalized)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        var value = tag?.Trim();
        if (value is { Length: > 0 } && value[0] is 'v' or 'V') value = value[1..];
        if (value is not null && value.Count(character => character == '.') == 2
            && value.All(character => char.IsDigit(character) || character == '.')
            && Version.TryParse(value, out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0 && parsed.Build >= 0)
        {
            version = new Version(parsed.Major, parsed.Minor, parsed.Build);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    internal static bool TryNormalizeSha256Digest(string? digest, out string value)
    {
        value = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : digest ?? string.Empty;
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static HttpRequestMessage CreateGitHubRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"PowerShellPlus/{CurrentVersionText} Update-Client");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool IsOfficialReleasePage(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/jordanw0204-rgb/PowerShellPlus/releases/", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialInstallerUri(Uri uri, string tag) => IsAllowedDownloadUri(uri)
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals($"/jordanw0204-rgb/PowerShellPlus/releases/download/{Uri.EscapeDataString(tag)}/{InstallerAssetName}",
            StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsAllowedDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string RequiredString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
            : throw new InvalidDataException($"GitHub's release response was missing '{property}'.");

    private static string NormalizeReleaseNotes(string notes)
    {
        var value = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return value.Length <= 1800 ? value : value[..1800].TrimEnd() + "\n\n…";
    }

    internal static async Task<bool> RunContractSmokeAsync(string reportPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PowerShellPlus-update-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = Encoding.UTF8.GetBytes("PowerShellPlus updater contract");
            var installerPath = Path.Combine(directory, InstallerAssetName);
            await File.WriteAllBytesAsync(installerPath, bytes);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            var fixture = $$"""
            {
              "tag_name":"v99.2.3",
              "html_url":"https://github.com/jordanw0204-rgb/PowerShellPlus/releases/tag/v99.2.3",
              "draft":false,
              "prerelease":false,
              "body":"Updater fixture",
              "assets":[{
                "name":"{{InstallerAssetName}}",
                "browser_download_url":"https://github.com/jordanw0204-rgb/PowerShellPlus/releases/download/v99.2.3/{{InstallerAssetName}}",
                "size":{{bytes.Length.ToString(CultureInfo.InvariantCulture)}},
                "digest":"sha256:{{digest.ToLowerInvariant()}}"
              }]
            }
            """;
            var release = ParseLatestRelease(fixture);
            var startInfo = CreateInstallerStartInfo(installerPath);
            var startupGateDirectory = Path.Combine(directory, "startup-gate");
            var success = release.Version == new Version(99, 2, 3)
                && release.InstallerSize == bytes.Length
                && release.InstallerSha256 == digest.ToLowerInvariant()
                && VerifySha256(installerPath, digest)
                && !VerifySha256(installerPath, new string('0', 64))
                && TryParseReleaseVersion("v4.10.0", out var parsed) && parsed == new Version(4, 10, 0)
                && !TryParseReleaseVersion("v4.10.0-beta", out _)
                && startInfo.Arguments.Contains("/UPDATE=1", StringComparison.Ordinal)
                && startInfo.Arguments.Contains("/CLOSEAPPLICATIONS", StringComparison.Ordinal)
                && startInfo.UseShellExecute
                && StartupUpdateGate.ContractPassesForTest(startupGateDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath,
                $"{(success ? "PASS" : "FAIL")} GitHub release parsing, semantic versioning, installer trust boundaries, SHA-256 verification, and update launch arguments.\n" +
                $"Release={release.TagName}\nDigestVerified={VerifySha256(installerPath, digest)}\nUnsafePrereleaseRejected={!TryParseReleaseVersion("v4.10.0-beta", out _)}\nStartupUpdatePreferenceGate={StartupUpdateGate.ContractPassesForTest(startupGateDirectory)}\nInstallerArguments={startInfo.Arguments}");
            return success;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, $"FAIL Updater contract smoke threw an exception.\n{exception}");
            return false;
        }
        finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
    }
}
