using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

internal sealed record TailscaleInstallerLaunch(string FileName, string Publisher);
internal sealed record TailscaleInstallerPackage(string FilePath, string Publisher);

internal static partial class TailscaleInstaller
{
    internal static readonly Uri PackageIndexUri = new("https://pkgs.tailscale.com/stable/");
    internal static readonly Uri PackageManifestUri = new("https://pkgs.tailscale.com/stable/?mode=json&os=windows");
    internal static readonly Uri BundledFallbackInstallerUri = new("https://pkgs.tailscale.com/stable/tailscale-setup-1.98.9.exe");
    internal static readonly Uri DownloadPageUri = new("https://tailscale.com/download/windows");
    private const long MaximumInstallerBytes = 100 * 1024 * 1024;
    private const string ExpectedPublisher = "Tailscale Inc.";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [GeneratedRegex("href=[\\\"'](?<path>(?:https://pkgs\\.tailscale\\.com/stable/)?tailscale-setup-(?<version>[0-9]+(?:\\.[0-9]+){2,3})\\.exe)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsInstallerLinkPattern();

    [GeneratedRegex("^/stable/tailscale-setup-[0-9]+(?:\\.[0-9]+){2,3}\\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfficialInstallerPathPattern();

    internal static Uri ParseLatestInstallerUri(string packageIndexHtml)
    {
        ArgumentNullException.ThrowIfNull(packageIndexHtml);
        var candidates = WindowsInstallerLinkPattern().Matches(packageIndexHtml)
            .Select(match => (Uri: new Uri(PackageIndexUri, match.Groups["path"].Value),
                Version: Version.Parse(match.Groups["version"].Value)))
            .Where(value => IsOfficialInstallerUri(value.Uri))
            .OrderByDescending(value => value.Version)
            .ToList();
        return candidates.FirstOrDefault().Uri
            ?? throw new InvalidOperationException("Tailscale's stable package page did not contain a Windows installer link.");
    }

    internal static Uri ParseManifestInstallerUri(string packageManifestJson)
    {
        ArgumentNullException.ThrowIfNull(packageManifestJson);
        using var document = JsonDocument.Parse(packageManifestJson);
        if (!document.RootElement.TryGetProperty("Exes", out var executables)
            || executables.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Tailscale's package manifest did not contain a Windows executable list.");

        var candidates = executables.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new Uri(PackageIndexUri, value!))
            .Where(IsOfficialInstallerUri)
            .Select(uri => (Uri: uri, Version: ParseInstallerVersion(uri)))
            .Where(value => value.Version is not null)
            .OrderByDescending(value => value.Version)
            .ToList();
        return candidates.FirstOrDefault().Uri
            ?? throw new InvalidOperationException("Tailscale's package manifest did not contain a supported Windows installer.");
    }

    private static Version? ParseInstallerVersion(Uri installerUri)
    {
        var fileName = Path.GetFileNameWithoutExtension(installerUri.AbsolutePath);
        const string prefix = "tailscale-setup-";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(fileName[prefix.Length..], out var version)
                ? version
                : null;
    }

    internal static bool IsOfficialInstallerUri(Uri? uri) => uri is not null
        && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.Host.Equals("pkgs.tailscale.com", StringComparison.OrdinalIgnoreCase)
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && OfficialInstallerPathPattern().IsMatch(uri.AbsolutePath);

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath) => new(installerPath)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
    };

    internal static async Task<TailscaleInstallerLaunch> DownloadAndLaunchAsync(
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var package = await DownloadAndVerifyAsync(progress, cancellationToken);
        try
        {
            var process = Process.Start(CreateInstallerStartInfo(package.FilePath));
            if (process is null) throw new InvalidOperationException("Windows did not open the Tailscale installer.");
            process.Dispose();
            return new TailscaleInstallerLaunch(Path.GetFileName(package.FilePath), package.Publisher);
        }
        catch
        {
            try { File.Delete(package.FilePath); } catch { }
            throw;
        }
    }

    internal static async Task<TailscaleInstallerPackage> DownloadAndVerifyAsync(
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = DownloadTimeout };
        var appVersion = typeof(TailscaleInstaller).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PowerShellPlus/{appVersion} Tailscale-Installer");

        var installerUri = await ResolveInstallerUriAsync(client, cancellationToken);
        using var response = await SendWithRetryAsync(client,
            () => new HttpRequestMessage(HttpMethod.Get, installerUri),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Tailscale's installer download redirected outside the verified package URL.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            throw new InvalidOperationException("Tailscale's installer was larger than PowerShellPlus's safety limit.");

        var directory = Path.Combine(Path.GetTempPath(), "PowerShellPlus", "Tailscale");
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileNameWithoutExtension(installerUri.AbsolutePath);
        var installerPath = Path.Combine(directory, $"{safeName}-{Guid.NewGuid():N}.exe");
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 128];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumInstallerBytes)
                        throw new InvalidOperationException("Tailscale's installer exceeded PowerShellPlus's safety limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (response.Content.Headers.ContentLength is > 0)
                        progress?.Report(Math.Min(1, (double)total / response.Content.Headers.ContentLength.Value));
                }
                await destination.FlushAsync(cancellationToken);
            }

            var publisher = VerifyTrustedPublisher(installerPath);
            return new TailscaleInstallerPackage(installerPath, publisher);
        }
        catch
        {
            try { File.Delete(installerPath); } catch { }
            throw;
        }
    }

    private static async Task<Uri> ResolveInstallerUriAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var manifestResponse = await SendWithRetryAsync(client,
                () => new HttpRequestMessage(HttpMethod.Get, PackageManifestUri),
                HttpCompletionOption.ResponseContentRead, cancellationToken, maximumAttempts: 1);
            manifestResponse.EnsureSuccessStatusCode();
            var manifest = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
            return ParseManifestInstallerUri(manifest);
        }
        catch (HttpRequestException)
        {
            return BundledFallbackInstallerUri;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BundledFallbackInstallerUri;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A structurally invalid manifest may still be recoverable through the legacy package page.
        }

        try
        {
            using var packageIndexResponse = await SendWithRetryAsync(client,
                () => new HttpRequestMessage(HttpMethod.Get, PackageIndexUri),
                HttpCompletionOption.ResponseContentRead, cancellationToken, maximumAttempts: 1);
            packageIndexResponse.EnsureSuccessStatusCode();
            var packageIndex = await packageIndexResponse.Content.ReadAsStringAsync(cancellationToken);
            return ParseLatestInstallerUri(packageIndex);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            return BundledFallbackInstallerUri;
        }
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client,
        Func<HttpRequestMessage> requestFactory, HttpCompletionOption completionOption,
        CancellationToken cancellationToken, int maximumAttempts = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request, completionOption, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == maximumAttempts) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maximumAttempts)
            {
            }

            var retryDelay = TimeSpan.FromMilliseconds(400 * attempt * attempt);
            await Task.Delay(retryDelay, cancellationToken);
        }
    }

    internal static bool IsTransient(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    internal static string VerifyTrustedPublisher(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("The downloaded Tailscale installer is missing.", installerPath);
        var pathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(installerPath));
        var fileInfoPointer = IntPtr.Zero;
        uint trustResult;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfoPointer = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000080,
                UiContext = 1
            };
            var action = WinTrustActionGenericVerifyV2;
            trustResult = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
        if (trustResult != 0)
            throw new InvalidOperationException($"Windows could not verify the downloaded Tailscale installer (trust error 0x{trustResult:X8}). Nothing was opened.");

        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
        var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!publisher.Equals(ExpectedPublisher, StringComparison.Ordinal))
            throw new InvalidOperationException($"The downloaded installer was signed by '{publisher}', not '{ExpectedPublisher}'. Nothing was opened.");
        return publisher;
    }

    internal static async Task<bool> RunDownloadSmokeAsync(string reportPath)
    {
        TailscaleInstallerPackage? package = null;
        try
        {
            package = await DownloadAndVerifyAsync();
            var length = new FileInfo(package.FilePath).Length;
            var success = length > 0 && package.Publisher == ExpectedPublisher;
            File.WriteAllText(reportPath,
                $"{(success ? "PASS" : "FAIL")} Official Tailscale installer download and Authenticode publisher verification.\n" +
                $"Publisher={package.Publisher}\nBytes={length}\nFile={Path.GetFileName(package.FilePath)}");
            return success;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, $"FAIL Official Tailscale installer verification threw an exception.\n{exception}");
            return false;
        }
        finally
        {
            if (package is not null)
            {
                try { File.Delete(package.FilePath); } catch { }
            }
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
