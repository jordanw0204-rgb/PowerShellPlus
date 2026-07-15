using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace PowerShellPlus.Native;

public partial class MainWindow
{
    private LanRemoteServer? lanRemoteServer;

    private IReadOnlyList<LanRemoteSession> GetLanRemoteSessions() => state.Sessions
        .Where(profile => panes.TryGetValue(profile.Id, out _))
        .Select(profile => new LanRemoteSession(profile.Id, profile.Name, profile.WorkingDirectory, panes[profile.Id]))
        .ToArray();

    private async void OpenLanRemoteClick(object sender, RoutedEventArgs e)
    {
        if (automationMode) return;
        TitleBarLanRemoteButton.IsEnabled = false;
        try
        {
            lanRemoteServer ??= new LanRemoteServer(Dispatcher, GetLanRemoteSessions);
            if (!lanRemoteServer.IsRunning)
            {
                UpdateStatus("Starting LAN Remote…");
                await lanRemoteServer.StartAsync();
                UpdateLanRemoteTitleBarState();
                UpdateStatus($"LAN Remote ready at {lanRemoteServer.Urls.FirstOrDefault()}");
            }
            var dialog = new LanRemoteDialog(lanRemoteServer) { Owner = this };
            dialog.ShowDialog();
            UpdateLanRemoteTitleBarState();
            UpdateStatus(lanRemoteServer.IsRunning
                ? $"LAN Remote is sharing {panes.Count} session{(panes.Count == 1 ? string.Empty : "s")}"
                : "LAN Remote stopped");
        }
        catch (Exception exception)
        {
            LogNativeError("LAN Remote", exception);
            MessageBox.Show(this,
                $"PowerShellPlus could not start LAN Remote.\n\n{exception.Message}\n\nMake sure this PC is connected to a private Wi-Fi or Ethernet network. If Windows Firewall prompts, allow Private networks only.",
                "LAN Remote unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus("LAN Remote could not start");
        }
        finally
        {
            TitleBarLanRemoteButton.IsEnabled = true;
            UpdateLanRemoteTitleBarState();
        }
    }

    private void UpdateLanRemoteTitleBarState()
    {
        var running = lanRemoteServer?.IsRunning == true;
        LanRemoteStatusDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TitleBarLanRemoteButton.ToolTip = running
            ? $"LAN Remote active · {lanRemoteServer!.ConnectedClients} connected"
            : "Share sessions on this LAN";
    }

    private void StopLanRemoteForShutdown()
    {
        if (lanRemoteServer is null) return;
        var server = lanRemoteServer;
        lanRemoteServer = null;
        server.SignalShutdown();
        _ = Task.Run(async () =>
        {
            try { await server.DisposeAsync(); }
            catch (Exception exception) { LogNativeError("LAN Remote shutdown", exception); }
        });
    }

    public async Task<bool> RunLanRemoteSmokeTestAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        LanRemoteServer? server = null;
        var details = new List<string>();
        try
        {
            await Task.Delay(1800);
            var pane = activePane ?? panes.Values.FirstOrDefault();
            if (pane is null) throw new InvalidOperationException("No terminal pane was available for LAN Remote testing.");

            server = new LanRemoteServer(Dispatcher, GetLanRemoteSessions) { AllowInput = true };
            await server.StartAsync(loopbackOnly: true);
            var baseAddress = new Uri(server.Urls.Single() + "/");
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

            var indexResponse = await client.GetAsync("");
            var index = await indexResponse.Content.ReadAsStringAsync();
            var assetsEmbedded = indexResponse.IsSuccessStatusCode && index.Contains("/vendor/xterm.js", StringComparison.Ordinal)
                && index.Contains("PowerShellPlus Remote", StringComparison.Ordinal);
            var securityHeadersPresent = indexResponse.Headers.TryGetValues("Content-Security-Policy", out var policies)
                && policies.Any(value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal));
            details.Add($"AssetsEmbedded={assetsEmbedded}");
            details.Add($"SecurityHeadersPresent={securityHeadersPresent}");

            var unauthorizedResponse = await client.GetAsync("api/sessions");
            var unauthenticatedRejected = unauthorizedResponse.StatusCode == HttpStatusCode.Unauthorized;
            var wrongCode = server.PairingCode == "99999999" ? "88888888" : "99999999";
            var wrongPairResponse = await client.PostAsJsonAsync("api/pair", new { code = wrongCode });
            var wrongCodeRejected = wrongPairResponse.StatusCode == HttpStatusCode.Unauthorized;
            var pairResponse = await client.PostAsJsonAsync("api/pair", new { code = server.PairingCode });
            var pairingAccepted = pairResponse.IsSuccessStatusCode;
            var sessionsResponse = await client.GetAsync("api/sessions");
            var sessionsJson = await sessionsResponse.Content.ReadAsStringAsync();
            var sessionInventoryVisible = sessionsResponse.IsSuccessStatusCode && sessionsJson.Contains(pane.Profile.Id, StringComparison.Ordinal)
                && sessionsJson.Contains(pane.Profile.Name, StringComparison.Ordinal);
            details.Add($"UnauthenticatedRejected={unauthenticatedRejected}");
            details.Add($"WrongCodeRejected={wrongCodeRejected}");
            details.Add($"PairingAccepted={pairingAccepted}");
            details.Add($"SessionInventoryVisible={sessionInventoryVisible}");

            var badOriginRejected = false;
            using (var badOriginSocket = new ClientWebSocket())
            {
                badOriginSocket.Options.Cookies = cookies;
                badOriginSocket.Options.SetRequestHeader("Origin", "http://evil.example");
                try { await badOriginSocket.ConnectAsync(ToWebSocketUri(baseAddress), CancellationToken.None); }
                catch (WebSocketException) { badOriginRejected = true; }
            }
            details.Add($"BadOriginRejected={badOriginRejected}");

            using var socket = new ClientWebSocket();
            socket.Options.Cookies = cookies;
            socket.Options.SetRequestHeader("Origin", baseAddress.GetLeftPart(UriPartial.Authority));
            await socket.ConnectAsync(ToWebSocketUri(baseAddress), CancellationToken.None);
            var sessionFrameSeen = false;
            var snapshotSeen = false;
            var initialDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < initialDeadline && (!sessionFrameSeen || !snapshotSeen))
            {
                var message = await ReceiveWebSocketTextAsync(socket, TimeSpan.FromSeconds(3));
                if (message is null) break;
                using var document = JsonDocument.Parse(message);
                var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                sessionFrameSeen |= type == "sessions";
                snapshotSeen |= type == "snapshot" && document.RootElement.TryGetProperty("sessionId", out var idElement)
                    && idElement.GetString() == pane.Profile.Id;
            }

            const string marker = "PSPLUS_LAN_REMOTE_OK_370";
            var outputEventsBefore = pane.RemoteOutputEventsForTest;
            var input = JsonSerializer.SerializeToUtf8Bytes(new { type = "input", sessionId = pane.Profile.Id, data = $"Write-Output '{marker}'\r" });
            await socket.SendAsync(input, WebSocketMessageType.Text, true, CancellationToken.None);
            var liveOutputSeen = false;
            var remoteInputAccepted = false;
            var outputDeadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < outputDeadline && !liveOutputSeen)
            {
                var remaining = outputDeadline - DateTime.UtcNow;
                var message = await ReceiveWebSocketTextAsync(socket, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                if (message is null) break;
                if (message.Contains("\"type\":\"input-ack\"", StringComparison.Ordinal)
                    && message.Contains("\"accepted\":true", StringComparison.Ordinal)) remoteInputAccepted = true;
                liveOutputSeen = message.Contains(marker, StringComparison.Ordinal);
            }
            details.Add($"WebSocketSessionFrame={sessionFrameSeen}");
            details.Add($"WebSocketSnapshot={snapshotSeen}");
            details.Add($"RemoteInputAccepted={remoteInputAccepted}");
            details.Add($"LiveConPtyOutput={liveOutputSeen}");
            details.Add($"TerminalOutputEvents={pane.RemoteOutputEventsForTest - outputEventsBefore}");
            details.Add($"TerminalTranscriptContainsMarker={pane.GetRawOutputForTest().Contains(marker, StringComparison.Ordinal)}");

            var rfc1918Accepted = LanNetworkPolicy.IsRfc1918(IPAddress.Parse("192.168.1.20"))
                && LanNetworkPolicy.IsRfc1918(IPAddress.Parse("10.2.3.4"))
                && LanNetworkPolicy.IsRfc1918(IPAddress.Parse("172.31.2.4"));
            var publicAddressRejected = !LanNetworkPolicy.IsRfc1918(IPAddress.Parse("8.8.8.8"))
                && !LanNetworkPolicy.IsRfc1918(IPAddress.Parse("172.32.0.1"));
            var subnetEnforced = new LanNetworkPolicy.LanNetwork(IPAddress.Parse("192.168.5.10"), IPAddress.Parse("255.255.255.0"))
                    .Contains(IPAddress.Parse("192.168.5.200"))
                && !new LanNetworkPolicy.LanNetwork(IPAddress.Parse("192.168.5.10"), IPAddress.Parse("255.255.255.0"))
                    .Contains(IPAddress.Parse("192.168.6.20"));
            details.Add($"Rfc1918Accepted={rfc1918Accepted}");
            details.Add($"PublicAddressRejected={publicAddressRejected}");
            details.Add($"SameSubnetEnforced={subnetEnforced}");

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Smoke complete", closeCancellation.Token); }
                catch (OperationCanceledException) { }
            }
            await server.StopAsync();
            var stoppedCleanly = !server.IsRunning;
            details.Add($"StoppedCleanly={stoppedCleanly}");

            var success = assetsEmbedded && securityHeadersPresent && unauthenticatedRejected && wrongCodeRejected && pairingAccepted
                && sessionInventoryVisible && badOriginRejected && sessionFrameSeen && snapshotSeen && remoteInputAccepted && liveOutputSeen
                && rfc1918Accepted && publicAddressRejected && subnetEnforced && stoppedCleanly;
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Embedded LAN Remote pairing, authorization, session mirroring, live ConPTY streaming, origin validation, and subnet policy.\n{string.Join(Environment.NewLine, details)}");
            return success;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, $"FAIL LAN Remote smoke test threw an exception.\n{string.Join(Environment.NewLine, details)}\nServerError={server?.LastError}\n{exception}");
            return false;
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
        }
    }

    private static Uri ToWebSocketUri(Uri httpBase) => new UriBuilder(httpBase) { Scheme = "ws", Path = "ws" }.Uri;

    private static async Task<string?> ReceiveWebSocketTextAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, cancellation.Token); }
            catch (OperationCanceledException) { return null; }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(memory.ToArray());
            if (memory.Length > 2_000_000) throw new InvalidOperationException("LAN Remote smoke received an oversized WebSocket frame.");
        }
    }
}
