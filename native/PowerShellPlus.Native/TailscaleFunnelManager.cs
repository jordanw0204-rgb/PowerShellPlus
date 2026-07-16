using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed record TailscaleFunnelPreflight(string ExecutablePath, string DnsName, Uri PublicUrl);
internal sealed class TailscaleNotInstalledException : InvalidOperationException
{
    public TailscaleNotInstalledException() : base(
        "Global mode requires Tailscale only on this PC. PowerShellPlus can download and open the verified installer for you. Nothing needs to be installed on the phone.") { }
}
internal sealed record TailscaleCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
        .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
}

internal sealed class TailscaleFunnelManager : IAsyncDisposable
{
    internal const int HttpsPort = 8443;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
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

    public async Task StartAsync(int localPort, TailscaleFunnelPreflight preflight, CancellationToken cancellationToken = default)
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
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    await AwaitPumpsAsync();
                    throw new InvalidOperationException($"Tailscale Funnel stopped before Global mode was ready.{Environment.NewLine}{GetForegroundOutput()}");
                }

                await Task.Delay(250, cancellationToken);
                var status = await RunCommandAsync(preflight.ExecutablePath, ["funnel", "status", "--json"], CommandTimeout, cancellationToken);
                if (status.ExitCode == 0 && FunnelStatusHasMapping(status.StandardOutput, preflight.DnsName, HttpsPort, localTarget))
                {
                    PublicUrl = preflight.PublicUrl;
                    return;
                }
            }

            throw new TimeoutException($"Tailscale Funnel did not become ready within 45 seconds. Approve Funnel/HTTPS in the browser window Tailscale opens, then try again. Public DNS can take a few minutes on first use.{Environment.NewLine}{GetForegroundOutput()}");
        }
        catch
        {
            await StopCoreAsync(throwOnCleanupFailure: false);
            throw;
        }
        finally { lifecycleGate.Release(); }
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
