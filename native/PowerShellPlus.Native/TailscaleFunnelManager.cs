using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed record TailscaleFunnelPreflight(string ExecutablePath, string DnsName, Uri PublicUrl);
internal sealed class TailscaleNotInstalledException : InvalidOperationException
{
    public TailscaleNotInstalledException() : base(
        "Global mode requires Tailscale only on this PC. PowerShellPlus can download and open the verified installer for you. Nothing needs to be installed on the phone.") { }
}
internal sealed class TailscaleLoginRequiredException : InvalidOperationException
{
    public string ExecutablePath { get; }

    public TailscaleLoginRequiredException(string executablePath) : base(
        "Tailscale is installed and running, but this PC has not been signed in yet. The Windows client lives in the system tray instead of opening a normal app window.")
    {
        ExecutablePath = executablePath;
    }
}
internal sealed record TailscaleCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
        .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
}

internal sealed class TailscaleFunnelManager : IAsyncDisposable
{
    internal const int HttpsPort = 443;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FunnelApprovalTimeout = TimeSpan.FromMinutes(3.25);
    private static readonly TimeSpan PublicReadinessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PublicProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly Uri[] PublicDnsResolvers =
    [
        new("https://dns.google/resolve"),
        new("https://cloudflare-dns.com/dns-query")
    ];
    private static readonly HttpClient PublicDnsClient = CreatePublicDnsClient();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object outputGate = new();
    private readonly StringBuilder foregroundOutput = new();
    private Process? funnelProcess;
    private Task? standardOutputPump;
    private Task? standardErrorPump;
    private string? executablePath;
    private string? target;
    private bool disposed;

    public bool IsRunning => funnelProcess is { HasExited: false };
    public Uri? PublicUrl { get; private set; }

    public async Task<TailscaleFunnelPreflight> PreflightAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var executable = ResolveExecutable();
        var status = await RunCommandAsync(executable, ["status", "--json"], CommandTimeout, cancellationToken);
        if (status.ExitCode != 0)
            throw new InvalidOperationException(BuildFailure(
                "Tailscale is installed but its status could not be read. Open Tailscale, sign in, and try again.", status));

        var identity = ParseIdentity(status.StandardOutput, executable);
        var funnelStatus = await RunCommandAsync(executable, ["funnel", "status", "--json"], CommandTimeout, cancellationToken);
        if (funnelStatus.ExitCode != 0 && !IsEmptyFunnelStatus(funnelStatus.CombinedOutput))
            throw new InvalidOperationException(BuildFailure(
                "This Tailscale version could not inspect its Funnel configuration. Update Tailscale and try again.", funnelStatus));
        if (FunnelPortInUse(funnelStatus.StandardOutput, HttpsPort))
            throw new InvalidOperationException($"Tailscale Funnel HTTPS port {HttpsPort} is already being used by another service on this PC. Stop that mapping, then try Global mode again. PowerShellPlus did not change it.");
        var serveStatus = await RunCommandAsync(executable, ["serve", "status", "--json"], CommandTimeout, cancellationToken);
        if (serveStatus.ExitCode == 0 && FunnelPortInUse(serveStatus.StandardOutput, HttpsPort))
            throw new InvalidOperationException($"Tailscale Serve HTTPS port {HttpsPort} is already being used by a private service on this PC. PowerShellPlus will not replace or publish it; move that service or turn it off first.");
        return identity;
    }

    public async Task StartAsync(int localPort, TailscaleFunnelPreflight preflight,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (localPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            var localTarget = $"http://127.0.0.1:{localPort}";
            var current = await RunCommandAsync(preflight.ExecutablePath, ["funnel", "status", "--json"], CommandTimeout, cancellationToken);
            if (FunnelPortInUse(current.StandardOutput, HttpsPort))
                throw new InvalidOperationException($"Tailscale Funnel HTTPS port {HttpsPort} became busy before Global mode could start. PowerShellPlus did not replace the existing mapping.");
            var currentServe = await RunCommandAsync(preflight.ExecutablePath, ["serve", "status", "--json"], CommandTimeout, cancellationToken);
            if (currentServe.ExitCode == 0 && FunnelPortInUse(currentServe.StandardOutput, HttpsPort))
                throw new InvalidOperationException($"Tailscale Serve HTTPS port {HttpsPort} became busy before Global mode could start. PowerShellPlus did not publish or replace it.");

            foregroundOutput.Clear();
            executablePath = preflight.ExecutablePath;
            target = localTarget;
            var process = CreateProcess(preflight.ExecutablePath, BuildFunnelArguments(localPort));
            if (!process.Start()) throw new InvalidOperationException("Tailscale Funnel did not start.");
            funnelProcess = process;
            standardOutputPump = PumpAsync(process.StandardOutput);
            standardErrorPump = PumpAsync(process.StandardError);

            var deadline = DateTime.UtcNow.AddSeconds(45);
            var approvalBrowserOpened = false;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    await AwaitPumpsAsync();
                    throw new InvalidOperationException($"Tailscale Funnel stopped before Global mode was ready.{Environment.NewLine}{GetForegroundOutput()}");
                }

                await Task.Delay(250, cancellationToken);
                if (!approvalBrowserOpened
                    && TailscaleLoginManager.TryParseLoginUri(GetForegroundOutput(), out var approvalUri))
                {
                    TailscaleLoginManager.OpenOfficialBrowser(approvalUri);
                    approvalBrowserOpened = true;
                    deadline = DateTime.UtcNow.Add(FunnelApprovalTimeout);
                }
                var status = await RunCommandAsync(preflight.ExecutablePath, ["funnel", "status", "--json"], CommandTimeout, cancellationToken);
                if (status.ExitCode == 0 && FunnelStatusHasMapping(status.StandardOutput, preflight.DnsName, HttpsPort, localTarget))
                {
                    progress?.Report("Funnel is active; verifying public DNS and HTTPS from outside the tailnet…");
                    await WaitForPublicEndpointAsync(preflight.PublicUrl, process, progress, cancellationToken);
                    PublicUrl = preflight.PublicUrl;
                    return;
                }
            }

            var waitDescription = approvalBrowserOpened ? "the three-minute browser approval window" : "45 seconds";
            throw new TimeoutException($"Tailscale Funnel did not become ready within {waitDescription}. Approve Funnel/HTTPS in the browser, then try again. Public DNS can take a few minutes on first use.{Environment.NewLine}{GetForegroundOutput()}");
        }
        catch
        {
            await StopCoreAsync(throwOnCleanupFailure: false);
            throw;
        }
        finally { lifecycleGate.Release(); }
    }

    private static async Task WaitForPublicEndpointAsync(Uri publicUrl, Process funnelProcess,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var deadline = started.Add(PublicReadinessTimeout);
        var lastProgressSecond = -1;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (funnelProcess.HasExited)
                throw new InvalidOperationException("Tailscale Funnel stopped while PowerShellPlus was checking its public internet path.");

            var addresses = await ResolvePublicAddressesAsync(publicUrl.DnsSafeHost, cancellationToken);
            foreach (var address in addresses.Where(IsPublicInternetAddress))
            {
                if (await ProbePublicIngressAsync(publicUrl, address, cancellationToken)) return;
            }

            var elapsedSeconds = Math.Max(0, (int)(DateTime.UtcNow - started).TotalSeconds);
            if (elapsedSeconds / 10 != lastProgressSecond / 10)
            {
                lastProgressSecond = elapsedSeconds;
                progress?.Report($"Global tunnel is active; waiting for public DNS/HTTPS… {elapsedSeconds}s");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new TimeoutException(
            "Tailscale's public Funnel address did not become reachable within 10 minutes. " +
            "PowerShellPlus kept the mapping active for the full documented DNS propagation window, but public DNS or HTTPS never passed. " +
            "Check whether your network blocks dns.google and cloudflare-dns.com, then try Global mode again.");
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(string dnsName,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in PublicDnsResolvers)
        {
            try
            {
                var builder = new UriBuilder(resolver)
                {
                    Query = $"name={Uri.EscapeDataString(dnsName)}&type=A"
                };
                using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PublicProbeTimeout);
                using var response = await PublicDnsClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                var addresses = ParsePublicDnsAddresses(json);
                if (addresses.Count > 0) return addresses;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            catch (JsonException) { }
        }
        return [];
    }

    internal static IReadOnlyList<IPAddress> ParsePublicDnsAddresses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("Status", out var status) && status.TryGetInt32(out var statusCode) && statusCode != 0)
            return [];
        if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) return [];

        var result = new List<IPAddress>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) || !type.TryGetInt32(out var recordType)
                || recordType is not (1 or 28)
                || !answer.TryGetProperty("data", out var data)
                || !IPAddress.TryParse(data.GetString(), out var address))
                continue;
            if (!result.Contains(address)) result.Add(address);
        }
        return result;
    }

    internal static bool IsPublicInternetAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not (0 or 10 or 127)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && bytes[0] < 224;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6SiteLocal
            && (bytes[0] & 0xfe) != 0xfc;
    }

    private static async Task<bool> ProbePublicIngressAsync(Uri publicUrl, IPAddress address,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = PublicProbeTimeout,
            ConnectCallback = async (_, connectCancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, publicUrl.Port), connectCancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublicProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, publicUrl);
            request.Headers.UserAgent.ParseAdd("PowerShellPlus-PublicReadiness/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token);
            return read > 0 && Encoding.UTF8.GetString(buffer, 0, read).Contains("PowerShellPlus", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (SocketException) { return false; }
    }

    private static HttpClient CreatePublicDnsClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = PublicProbeTimeout
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PowerShellPlus-PublicReadiness/1.0");
        return client;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (disposed && funnelProcess is null) return;
        await lifecycleGate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(throwOnCleanupFailure: true, cancellationToken); }
        finally { lifecycleGate.Release(); }
    }

    public void SignalShutdown()
    {
        try
        {
            if (funnelProcess is { HasExited: false } process) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private async Task StopCoreAsync(bool throwOnCleanupFailure, CancellationToken cancellationToken = default)
    {
        var process = funnelProcess;
        var executable = executablePath;
        var localTarget = target;
        funnelProcess = null;
        executablePath = null;
        target = null;
        PublicUrl = null;

        TailscaleCommandResult? offResult = null;
        if (!string.IsNullOrWhiteSpace(executable) && !string.IsNullOrWhiteSpace(localTarget))
        {
            try
            {
                var localPort = new Uri(localTarget).Port;
                offResult = await RunCommandAsync(executable, BuildStopArguments(localPort), CommandTimeout, cancellationToken);
            }
            catch when (!throwOnCleanupFailure) { }
        }

        if (process is not null)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(wait.Token);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
            await AwaitPumpsAsync();
            process.Dispose();
        }

        if (throwOnCleanupFailure && offResult is { ExitCode: not 0 }
            && !IsEmptyFunnelStatus(offResult.CombinedOutput))
            throw new InvalidOperationException(BuildFailure("PowerShellPlus stopped its local server, but Tailscale reported that its Funnel endpoint cleanup failed.", offResult));
    }

    private async Task AwaitPumpsAsync()
    {
        var pumps = new[] { standardOutputPump, standardErrorPump }.Where(value => value is not null).Cast<Task>().ToArray();
        standardOutputPump = null;
        standardErrorPump = null;
        if (pumps.Length == 0) return;
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) return;
            lock (outputGate)
            {
                if (foregroundOutput.Length < 16_384)
                    foregroundOutput.Append(buffer, 0, Math.Min(read, 16_384 - foregroundOutput.Length));
            }
        }
    }

    private string GetForegroundOutput()
    {
        lock (outputGate)
        {
            var value = foregroundOutput.ToString().Trim();
            return value.Length == 0 ? "Tailscale did not provide additional details." : value;
        }
    }

    internal static TailscaleFunnelPreflight ParseIdentity(string statusJson, string executable)
    {
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            var root = document.RootElement;
            var backend = root.TryGetProperty("BackendState", out var backendElement) ? backendElement.GetString() : null;
            if (string.Equals(backend, "NeedsLogin", StringComparison.OrdinalIgnoreCase))
                throw new TailscaleLoginRequiredException(executable);
            if (!string.Equals(backend, "Running", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Tailscale is not connected. Open Tailscale and sign in on this PC, then try again.");
            if (!root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Tailscale did not report this PC's tailnet identity.");
            if (self.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("This PC is signed in to Tailscale but is currently offline.");
            var dnsName = self.TryGetProperty("DNSName", out var dnsElement) ? dnsElement.GetString()?.Trim().TrimEnd('.') : null;
            if (string.IsNullOrWhiteSpace(dnsName) || !dnsName.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                || Uri.CheckHostName(dnsName) != UriHostNameType.Dns)
                throw new InvalidOperationException("Tailscale MagicDNS did not provide a valid ts.net hostname. Enable MagicDNS/HTTPS in the tailnet and try again.");
            var publicUrl = new UriBuilder(Uri.UriSchemeHttps, dnsName, HttpsPort).Uri;
            return new TailscaleFunnelPreflight(executable, dnsName, publicUrl);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Tailscale returned an unreadable status response. Update Tailscale and try again.", exception);
        }
    }

    internal static bool FunnelPortInUse(string? statusJson, int httpsPort)
    {
        if (string.IsNullOrWhiteSpace(statusJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            return ContainsPort(document.RootElement, httpsPort);
        }
        catch (JsonException) { return false; }
    }

    private static bool ContainsPort(JsonElement element, int httpsPort)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var portName = httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if ((property.Name.Equals(portName, StringComparison.Ordinal)
                        || property.Name.EndsWith($":{portName}", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return true;
                if (ContainsPort(property.Value, httpsPort)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) if (ContainsPort(item, httpsPort)) return true;
        }
        return false;
    }

    internal static bool FunnelStatusHasMapping(string? statusJson, string dnsName, int httpsPort, string localTarget)
    {
        if (string.IsNullOrWhiteSpace(statusJson)) return false;
        return statusJson.Contains(localTarget, StringComparison.OrdinalIgnoreCase)
            && (statusJson.Contains($"{dnsName}:{httpsPort}", StringComparison.OrdinalIgnoreCase)
                || FunnelPortInUse(statusJson, httpsPort));
    }

    internal static IReadOnlyList<string> BuildFunnelArguments(int localPort) =>
        ["funnel", "--yes", $"--https={HttpsPort}", $"http://127.0.0.1:{localPort}"];

    internal static IReadOnlyList<string> BuildStopArguments(int localPort) =>
        ["funnel", $"--https={HttpsPort}", $"http://127.0.0.1:{localPort}", "off"];

    private static string ResolveExecutable()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "Tailscale", "tailscale.exe"));
        if (!string.IsNullOrWhiteSpace(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, "Tailscale", "tailscale.exe"));
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(folder.Trim('"'), "tailscale.exe"));
        var match = candidates.FirstOrDefault(File.Exists);
        if (match is not null) return Path.GetFullPath(match);
        throw new TailscaleNotInstalledException();
    }

    private static Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static async Task<TailscaleCommandResult> RunCommandAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, arguments);
        if (!process.Start()) return new TailscaleCommandResult(-1, string.Empty, "The process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Tailscale did not respond within {timeout.TotalSeconds:0} seconds.");
        }
        return new TailscaleCommandResult(process.ExitCode, await output, await error);
    }

    private static bool IsEmptyFunnelStatus(string value) => string.IsNullOrWhiteSpace(value)
        || value.Trim() is "{}" or "null"
        || value.Contains("no serve config", StringComparison.OrdinalIgnoreCase)
        || value.Contains("no funnel config", StringComparison.OrdinalIgnoreCase)
        || value.Contains("funnel is not enabled", StringComparison.OrdinalIgnoreCase)
        || value.Contains("not configured", StringComparison.OrdinalIgnoreCase);

    private static string BuildFailure(string message, TailscaleCommandResult result)
    {
        var detail = result.CombinedOutput;
        return detail.Length == 0 ? message : $"{message}{Environment.NewLine}{detail}";
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try { await StopAsync(); }
        finally
        {
            disposed = true;
            lifecycleGate.Dispose();
        }
    }
}
