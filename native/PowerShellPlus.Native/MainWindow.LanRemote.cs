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
    private TailscaleFunnelManager? globalRemoteTunnel;

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
            var dialog = new LanRemoteDialog(lanRemoteServer, SwitchRemoteAccessModeAsync, StopRemoteAccessAsync) { Owner = this };
            dialog.ShowDialog();
            UpdateLanRemoteTitleBarState();
            UpdateStatus(lanRemoteServer.IsRunning
                ? $"{(lanRemoteServer.Mode == RemoteAccessMode.Global ? "Global Remote" : "LAN Remote")} is sharing {panes.Count} session{(panes.Count == 1 ? string.Empty : "s")}"
                : "Remote Access stopped");
        }
        catch (Exception exception)
        {
            LogNativeError("Remote Access", exception);
            PowerShellPlusDialog.ShowMessage(this,
                $"PowerShellPlus could not start LAN Remote.\n\n{exception.Message}\n\nMake sure this PC is connected to a private Wi-Fi or Ethernet network. If Windows Firewall prompts, allow Private networks only.",
                "Remote Access unavailable", PowerShellPlusDialogKind.Warning);
            UpdateStatus("Remote Access could not start");
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
            ? $"{(lanRemoteServer!.Mode == RemoteAccessMode.Global ? "Global" : "LAN")} Remote active · {lanRemoteServer.ConnectedClients} connected"
            : "Open LAN / Global Remote Access";
    }

    private async Task SwitchRemoteAccessModeAsync(RemoteAccessMode mode)
    {
        lanRemoteServer ??= new LanRemoteServer(Dispatcher, GetLanRemoteSessions);
        var server = lanRemoteServer;
        if (server.IsRunning && server.Mode == mode) return;
        var previousMode = server.IsRunning ? server.Mode : (RemoteAccessMode?)null;
        var previousAllowInput = server.AllowInput;

        if (mode == RemoteAccessMode.Global)
        {
            globalRemoteTunnel ??= new TailscaleFunnelManager();
            UpdateStatus("Checking secure browser-only Global access…");
            var preflight = await globalRemoteTunnel.PreflightAsync();
            if (server.IsRunning) await server.StopAsync();
            try
            {
                server.AllowInput = false;
                await server.StartGlobalAsync(preflight.PublicUrl);
                UpdateStatus("Opening encrypted Global HTTPS tunnel…");
                await globalRemoteTunnel.StartAsync(server.Port, preflight);
                UpdateStatus($"Global Remote ready at {preflight.PublicUrl.AbsoluteUri.TrimEnd('/')}");
            }
            catch
            {
                try { await globalRemoteTunnel.StopAsync(); } catch { }
                try { await server.StopAsync(); } catch { }
                if (previousMode == RemoteAccessMode.Lan)
                {
                    try
                    {
                        server.AllowInput = previousAllowInput;
                        await server.StartAsync();
                    }
                    catch { }
                }
                UpdateLanRemoteTitleBarState();
                throw;
            }
        }
        else
        {
            Exception? tunnelCleanupError = null;
            try
            {
                if (globalRemoteTunnel is not null) await globalRemoteTunnel.StopAsync();
            }
            catch (Exception exception) { tunnelCleanupError = exception; }
            if (server.IsRunning) await server.StopAsync();
            await server.StartAsync();
            UpdateStatus($"LAN Remote ready at {server.Urls.FirstOrDefault()}");
            if (tunnelCleanupError is not null) throw tunnelCleanupError;
        }
        UpdateLanRemoteTitleBarState();
    }

    private async Task StopRemoteAccessAsync()
    {
        Exception? tunnelCleanupError = null;
        try
        {
            if (globalRemoteTunnel is not null) await globalRemoteTunnel.StopAsync();
        }
        catch (Exception exception) { tunnelCleanupError = exception; }
        if (lanRemoteServer?.IsRunning == true) await lanRemoteServer.StopAsync();
        UpdateLanRemoteTitleBarState();
        if (tunnelCleanupError is not null) throw tunnelCleanupError;
    }

    private void StopLanRemoteForShutdown()
    {
        if (lanRemoteServer is null && globalRemoteTunnel is null) return;
        var server = lanRemoteServer;
        var tunnel = globalRemoteTunnel;
        lanRemoteServer = null;
        globalRemoteTunnel = null;
        server?.SignalShutdown();
        tunnel?.SignalShutdown();
        _ = Task.Run(async () =>
        {
            try
            {
                if (tunnel is not null) await tunnel.DisposeAsync();
                if (server is not null) await server.DisposeAsync();
            }
            catch (Exception exception) { LogNativeError("Remote Access shutdown", exception); }
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
            var appResponse = await client.GetAsync("app.js");
            var appScript = await appResponse.Content.ReadAsStringAsync();
            var stylesResponse = await client.GetAsync("styles.css");
            var styles = await stylesResponse.Content.ReadAsStringAsync();
            var manifestResponse = await client.GetAsync("manifest.webmanifest");
            var manifest = await manifestResponse.Content.ReadAsStringAsync();
            var assetsEmbedded = indexResponse.IsSuccessStatusCode && index.Contains("/vendor/xterm.js", StringComparison.Ordinal)
                && index.Contains("PowerShellPlus Remote", StringComparison.Ordinal)
                && index.Contains("maxlength=\"12\"", StringComparison.Ordinal)
                && index.Contains("Remote Access", StringComparison.Ordinal);
            var responsiveClientEmbedded = appResponse.IsSuccessStatusCode
                && appScript.Contains("ResizeObserver", StringComparison.Ordinal)
                && appScript.Contains("orientationchange", StringComparison.Ordinal)
                && appScript.Contains("is-landscape", StringComparison.Ordinal)
                && appScript.Contains("preferredMaximum", StringComparison.Ordinal)
                && appScript.Contains("queue-add", StringComparison.Ordinal)
                && appScript.Contains("command-input", StringComparison.Ordinal)
                && appScript.Contains("deviceName", StringComparison.Ordinal);
            var stableTerminalSizingEmbedded = stylesResponse.IsSuccessStatusCode
                && appScript.Contains("lastFitKey", StringComparison.Ordinal)
                && appScript.Contains("terminalFits", StringComparison.Ordinal)
                && appScript.Contains("const changed = activeSessionId !== id", StringComparison.Ordinal)
                && styles.Contains(".terminal-host", StringComparison.Ordinal)
                && styles.Contains("overflow: hidden", StringComparison.Ordinal);
            var rotationManifestEmbedded = manifestResponse.IsSuccessStatusCode
                && manifest.Contains("\"orientation\": \"any\"", StringComparison.Ordinal);
            var securityHeadersPresent = indexResponse.Headers.TryGetValues("Content-Security-Policy", out var policies)
                && policies.Any(value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal));
            details.Add($"AssetsEmbedded={assetsEmbedded}");
            details.Add($"ResponsiveCommandClientEmbedded={responsiveClientEmbedded}");
            details.Add($"StableTerminalSizingEmbedded={stableTerminalSizingEmbedded}");
            details.Add($"RotationManifestEmbedded={rotationManifestEmbedded}");
            details.Add($"SecurityHeadersPresent={securityHeadersPresent}");
            var addressMetadataVisible = server.Addresses.Count == 1 && server.Addresses[0].IsRecommended
                && server.Addresses[0].AdapterName == "This PC";
            details.Add($"AddressMetadataVisible={addressMetadataVisible}");

            var unauthorizedResponse = await client.GetAsync("api/sessions");
            var unauthenticatedRejected = unauthorizedResponse.StatusCode == HttpStatusCode.Unauthorized;
            var wrongCode = server.PairingCode == "99999999" ? "88888888" : "99999999";
            var wrongPairResponse = await client.PostAsJsonAsync("api/pair", new { code = wrongCode });
            var wrongCodeRejected = wrongPairResponse.StatusCode == HttpStatusCode.Unauthorized;
            var pairResponse = await client.PostAsJsonAsync("api/pair", new { code = server.PairingCode, deviceName = "LAN smoke browser" });
            var pairingAccepted = pairResponse.IsSuccessStatusCode;
            var pairedCookieState = cookies.GetCookies(baseAddress)["psp_lan_device"];
            var pairedCookie = pairedCookieState?.Value ?? string.Empty;
            var persistentHttpOnlyCookieIssued = pairedCookieState is { HttpOnly: true }
                && pairedCookieState.Expires.ToUniversalTime() > DateTime.UtcNow.AddDays(300);
            var savedPairingListed = server.PairedDevices.Count == 1 && server.PairedDevices[0].Name == "LAN smoke browser";
            var pairingStoreText = File.Exists(LanRemotePairingStore.FilePath) ? File.ReadAllText(LanRemotePairingStore.FilePath) : string.Empty;
            var credentialStoredAsHashOnly = pairedCookie.Length > 40 && !pairingStoreText.Contains(pairedCookie, StringComparison.Ordinal)
                && pairingStoreText.Contains("secretHash", StringComparison.OrdinalIgnoreCase);
            var sessionsResponse = await client.GetAsync("api/sessions");
            var sessionsJson = await sessionsResponse.Content.ReadAsStringAsync();
            var sessionInventoryVisible = sessionsResponse.IsSuccessStatusCode && sessionsJson.Contains(pane.Profile.Id, StringComparison.Ordinal)
                && sessionsJson.Contains(pane.Profile.Name, StringComparison.Ordinal);
            using var sessionDocument = JsonDocument.Parse(sessionsJson);
            var sessionView = sessionDocument.RootElement.GetProperty("sessions").EnumerateArray()
                .First(value => value.GetProperty("id").GetString() == pane.Profile.Id);
            var gridMetadataVisible = sessionView.GetProperty("columns").GetInt32() >= 2
                && sessionView.GetProperty("rows").GetInt32() >= 2
                && !string.IsNullOrWhiteSpace(sessionView.GetProperty("fontFace").GetString())
                && sessionView.GetProperty("fontSize").GetInt32() >= 6;
            var commandMetadataVisible = sessionView.TryGetProperty("pendingCommands", out var pendingElement)
                && pendingElement.ValueKind == JsonValueKind.Array
                && sessionDocument.RootElement.TryGetProperty("quickCommands", out var quickElement)
                && quickElement.ValueKind == JsonValueKind.Array;
            details.Add($"UnauthenticatedRejected={unauthenticatedRejected}");
            details.Add($"WrongCodeRejected={wrongCodeRejected}");
            details.Add($"PairingAccepted={pairingAccepted}");
            details.Add($"SavedPairingListed={savedPairingListed}");
            details.Add($"PersistentHttpOnlyCookieIssued={persistentHttpOnlyCookieIssued}");
            details.Add($"CredentialStoredAsHashOnly={credentialStoredAsHashOnly}");
            details.Add($"SessionInventoryVisible={sessionInventoryVisible}");
            details.Add($"GridMetadataVisible={gridMetadataVisible}");
            details.Add($"CommandMetadataVisible={commandMetadataVisible}");

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
            var sessionGridSeen = false;
            var snapshotGridSeen = false;
            var snapshotComposedSeen = false;
            var snapshotCursorSeen = false;
            var initialDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < initialDeadline && (!sessionFrameSeen || !snapshotSeen))
            {
                var message = await ReceiveWebSocketTextAsync(socket, TimeSpan.FromSeconds(3));
                if (message is null) break;
                using var document = JsonDocument.Parse(message);
                var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "sessions")
                {
                    sessionFrameSeen = true;
                    var remoteSession = document.RootElement.GetProperty("sessions").EnumerateArray()
                        .First(value => value.GetProperty("id").GetString() == pane.Profile.Id);
                    sessionGridSeen = remoteSession.GetProperty("columns").GetInt32() >= 2
                        && remoteSession.GetProperty("rows").GetInt32() >= 2;
                }
                if (type == "snapshot" && document.RootElement.TryGetProperty("sessionId", out var idElement)
                    && idElement.GetString() == pane.Profile.Id)
                {
                    snapshotSeen = true;
                    snapshotGridSeen = document.RootElement.GetProperty("columns").GetInt32() >= 2
                        && document.RootElement.GetProperty("rows").GetInt32() >= 2;
                    snapshotComposedSeen = document.RootElement.TryGetProperty("composed", out var composedElement)
                        && composedElement.ValueKind == JsonValueKind.True;
                    snapshotCursorSeen = document.RootElement.TryGetProperty("cursorColumn", out var cursorColumn)
                        && cursorColumn.GetInt32() >= 1
                        && document.RootElement.TryGetProperty("cursorRow", out var cursorRow)
                        && cursorRow.GetInt32() >= 1;
                }
            }

            const string marker = "PSPLUS_LAN_REMOTE_OK_370";
            var outputEventsBefore = pane.RemoteOutputEventsForTest;
            var input = JsonSerializer.SerializeToUtf8Bytes(new { type = "input", sessionId = pane.Profile.Id, data = $"Write-Output '{marker}'\r" });
            await socket.SendAsync(input, WebSocketMessageType.Text, true, CancellationToken.None);
            var liveOutputSeen = false;
            var remoteInputAccepted = false;
            var outputGridSeen = false;
            var outputDeadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < outputDeadline && !liveOutputSeen)
            {
                var remaining = outputDeadline - DateTime.UtcNow;
                var message = await ReceiveWebSocketTextAsync(socket, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                if (message is null) break;
                if (message.Contains("\"type\":\"input-ack\"", StringComparison.Ordinal)
                    && message.Contains("\"accepted\":true", StringComparison.Ordinal)) remoteInputAccepted = true;
                if (message.Contains(marker, StringComparison.Ordinal))
                {
                    liveOutputSeen = true;
                    using var outputDocument = JsonDocument.Parse(message);
                    outputGridSeen = outputDocument.RootElement.TryGetProperty("columns", out var outputColumns)
                        && outputColumns.GetInt32() >= 2
                        && outputDocument.RootElement.TryGetProperty("rows", out var outputRows)
                        && outputRows.GetInt32() >= 2;
                }
            }
            details.Add($"WebSocketSessionFrame={sessionFrameSeen}");
            details.Add($"WebSocketSnapshot={snapshotSeen}");
            details.Add($"WebSocketSessionGrid={sessionGridSeen}");
            details.Add($"WebSocketSnapshotGrid={snapshotGridSeen}");
            details.Add($"WebSocketSnapshotComposed={snapshotComposedSeen}");
            details.Add($"WebSocketSnapshotCursor={snapshotCursorSeen}");
            details.Add($"RemoteInputAccepted={remoteInputAccepted}");
            details.Add($"LiveConPtyOutput={liveOutputSeen}");
            details.Add($"LiveOutputGrid={outputGridSeen}");
            details.Add($"TerminalOutputEvents={pane.RemoteOutputEventsForTest - outputEventsBefore}");
            details.Add($"TerminalTranscriptContainsMarker={pane.GetRawOutputForTest().Contains(marker, StringComparison.Ordinal)}");

            var resync = JsonSerializer.SerializeToUtf8Bytes(new { type = "sync", snapshots = true });
            await socket.SendAsync(resync, WebSocketMessageType.Text, true, CancellationToken.None);
            var composedSnapshotContainsMarker = false;
            var snapshotDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < snapshotDeadline && !composedSnapshotContainsMarker)
            {
                var remaining = snapshotDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var message = await ReceiveWebSocketTextAsync(socket, remaining);
                if (message is null) break;
                using var document = JsonDocument.Parse(message);
                composedSnapshotContainsMarker = document.RootElement.TryGetProperty("type", out var typeElement)
                    && typeElement.GetString() == "snapshot"
                    && document.RootElement.TryGetProperty("sessionId", out var idElement)
                    && idElement.GetString() == pane.Profile.Id
                    && document.RootElement.TryGetProperty("composed", out var composedElement)
                    && composedElement.ValueKind == JsonValueKind.True
                    && document.RootElement.TryGetProperty("data", out var dataElement)
                    && dataElement.GetString()?.Contains(marker, StringComparison.Ordinal) == true;
            }
            details.Add($"ComposedResyncContainsMarker={composedSnapshotContainsMarker}");

            const string queuedMarker = "PSPLUS_LAN_QUEUE_OK_371";
            var queuedCommand = $"Write-Output '{queuedMarker}'";
            var queuedIndex = pane.GetRemotePendingCommands().Count;
            var queueRequestId = Guid.NewGuid().ToString("N");
            var queueRequest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "queue-add",
                requestId = queueRequestId,
                sessionId = pane.Profile.Id,
                command = queuedCommand
            });
            await socket.SendAsync(queueRequest, WebSocketMessageType.Text, true, CancellationToken.None);
            var queueAccepted = false;
            var queueVisible = false;
            var queueDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < queueDeadline && (!queueAccepted || !queueVisible))
            {
                var remaining = queueDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var message = await ReceiveWebSocketTextAsync(socket, remaining);
                if (message is null) break;
                using var document = JsonDocument.Parse(message);
                var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                queueAccepted |= type == "queue-ack"
                    && document.RootElement.TryGetProperty("requestId", out var requestElement)
                    && requestElement.GetString() == queueRequestId
                    && document.RootElement.GetProperty("accepted").GetBoolean();
                if (type == "sessions") queueVisible |= message.Contains(queuedMarker, StringComparison.Ordinal);
            }

            var commandRequestId = Guid.NewGuid().ToString("N");
            var commandRequest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "command",
                requestId = commandRequestId,
                sessionId = pane.Profile.Id,
                command = queuedCommand,
                queueIndex = queuedIndex
            });
            await socket.SendAsync(commandRequest, WebSocketMessageType.Text, true, CancellationToken.None);
            var queuedCommandAccepted = false;
            var queuedCommandOutputSeen = false;
            var queueRemoved = false;
            var commandDeadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < commandDeadline && (!queuedCommandAccepted || !queuedCommandOutputSeen || !queueRemoved))
            {
                var remaining = commandDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var message = await ReceiveWebSocketTextAsync(socket, remaining);
                if (message is null) break;
                using var document = JsonDocument.Parse(message);
                var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                queuedCommandAccepted |= type == "command-ack"
                    && document.RootElement.TryGetProperty("requestId", out var requestElement)
                    && requestElement.GetString() == commandRequestId
                    && document.RootElement.GetProperty("accepted").GetBoolean();
                queuedCommandOutputSeen |= type == "output" && message.Contains(queuedMarker, StringComparison.Ordinal);
                if (type == "sessions") queueRemoved |= !message.Contains(queuedMarker, StringComparison.Ordinal);
            }
            details.Add($"RemoteQueueAccepted={queueAccepted}");
            details.Add($"RemoteQueueVisible={queueVisible}");
            details.Add($"RemoteQueuedCommandAccepted={queuedCommandAccepted}");
            details.Add($"RemoteQueuedCommandOutput={queuedCommandOutputSeen}");
            details.Add($"RemoteQueueAdvanced={queueRemoved}");

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
            var firstServer = server;
            await firstServer.StopAsync();
            var stoppedCleanly = !firstServer.IsRunning;
            await firstServer.DisposeAsync();

            server = new LanRemoteServer(Dispatcher, GetLanRemoteSessions) { AllowInput = true };
            await server.StartAsync(loopbackOnly: true);
            var restartBaseAddress = new Uri(server.Urls.Single() + "/");
            using var restartHandler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, UseProxy = false };
            using var restartClient = new HttpClient(restartHandler) { BaseAddress = restartBaseAddress, Timeout = TimeSpan.FromSeconds(10) };
            var restartSessionsResponse = await restartClient.GetAsync("api/sessions");
            var pairingSurvivedRestart = restartSessionsResponse.IsSuccessStatusCode;
            var savedDeviceAfterRestart = server.PairedDevices.SingleOrDefault(value => value.Name == "LAN smoke browser");
            var savedDeviceVisibleAfterRestart = savedDeviceAfterRestart is not null;
            using var revocationSocket = new ClientWebSocket();
            revocationSocket.Options.Cookies = cookies;
            revocationSocket.Options.SetRequestHeader("Origin", restartBaseAddress.GetLeftPart(UriPartial.Authority));
            await revocationSocket.ConnectAsync(ToWebSocketUri(restartBaseAddress), CancellationToken.None);
            var pairingRevoked = savedDeviceAfterRestart is not null && await server.RevokePairedDeviceAsync(savedDeviceAfterRestart.Id);
            var revocationDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < revocationDeadline && revocationSocket.State == WebSocketState.Open)
                _ = await ReceiveWebSocketTextAsync(revocationSocket, TimeSpan.FromSeconds(1));
            var activeSocketRevoked = revocationSocket.State is WebSocketState.CloseReceived or WebSocketState.Closed;
            if (revocationSocket.State == WebSocketState.CloseReceived)
                await revocationSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Revocation received", CancellationToken.None);
            var revokedSessionsResponse = await restartClient.GetAsync("api/sessions");
            var revokedCredentialRejected = revokedSessionsResponse.StatusCode == HttpStatusCode.Unauthorized;
            details.Add($"PairingSurvivedRestart={pairingSurvivedRestart}");
            details.Add($"SavedDeviceVisibleAfterRestart={savedDeviceVisibleAfterRestart}");
            details.Add($"PairingRevoked={pairingRevoked}");
            details.Add($"ActiveSocketRevoked={activeSocketRevoked}");
            details.Add($"RevokedCredentialRejected={revokedCredentialRejected}");
            await server.StopAsync();
            stoppedCleanly &= !server.IsRunning;
            details.Add($"StoppedCleanly={stoppedCleanly}");

            var externalAddress = new Uri($"https://psplus-smoke.example.ts.net:{TailscaleFunnelManager.HttpsPort}/");
            await server.StartGlobalAsync(externalAddress);
            var globalLocalAddress = new Uri(server.LocalUrl + "/");
            var globalBoundary = server.Mode == RemoteAccessMode.Global
                && server.PairingCode.Length == 12
                && server.Urls.SequenceEqual([externalAddress.AbsoluteUri.TrimEnd('/')])
                && server.Addresses.Count == 1
                && server.Addresses[0].AdapterName == "Global HTTPS address"
                && globalLocalAddress.Host == IPAddress.Loopback.ToString();
            using var globalHandler = new HttpClientHandler { UseCookies = false, UseProxy = false };
            using var globalClient = new HttpClient(globalHandler) { Timeout = TimeSpan.FromSeconds(10) };
            using var globalIndexRequest = new HttpRequestMessage(HttpMethod.Get, globalLocalAddress);
            globalIndexRequest.Headers.Host = externalAddress.Authority;
            var globalIndexResponse = await globalClient.SendAsync(globalIndexRequest);
            var globalHostAccepted = globalIndexResponse.IsSuccessStatusCode;
            var globalHsts = globalIndexResponse.Headers.TryGetValues("Strict-Transport-Security", out var hstsValues)
                && hstsValues.Any(value => value.Contains("max-age=31536000", StringComparison.Ordinal));
            using var badHostRequest = new HttpRequestMessage(HttpMethod.Get, globalLocalAddress);
            badHostRequest.Headers.Host = $"evil.example:{TailscaleFunnelManager.HttpsPort}";
            var badHostResponse = await globalClient.SendAsync(badHostRequest);
            var globalBadHostRejected = badHostResponse.StatusCode == HttpStatusCode.Forbidden;

            using var globalPairRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(globalLocalAddress, "api/pair"));
            globalPairRequest.Headers.Host = externalAddress.Authority;
            globalPairRequest.Content = JsonContent.Create(new { code = server.PairingCode, deviceName = "Global smoke browser" });
            var globalPairResponse = await globalClient.SendAsync(globalPairRequest);
            var globalPairAccepted = globalPairResponse.IsSuccessStatusCode;
            var setCookie = globalPairResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues)
                ? setCookieValues.FirstOrDefault(value => value.StartsWith("psp_lan_device=", StringComparison.Ordinal))
                : null;
            var globalSecureCookie = setCookie is not null
                && setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase)
                && setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase);
            var cookiePair = setCookie?.Split(';', 2)[0].Split('=', 2);
            var globalCookies = new CookieContainer();
            if (cookiePair is { Length: 2 }) globalCookies.Add(globalLocalAddress, new Cookie(cookiePair[0], cookiePair[1], "/"));

            var globalBadOriginRejected = false;
            using (var globalBadOriginSocket = new ClientWebSocket())
            {
                globalBadOriginSocket.Options.Cookies = globalCookies;
                globalBadOriginSocket.Options.SetRequestHeader("Origin", "https://evil.example");
                try { await globalBadOriginSocket.ConnectAsync(ToWebSocketUri(globalLocalAddress), CancellationToken.None); }
                catch (WebSocketException) { globalBadOriginRejected = true; }
            }
            using var globalSocket = new ClientWebSocket();
            globalSocket.Options.Cookies = globalCookies;
            globalSocket.Options.SetRequestHeader("Origin", externalAddress.GetLeftPart(UriPartial.Authority));
            await globalSocket.ConnectAsync(ToWebSocketUri(globalLocalAddress), CancellationToken.None);
            var globalExactOriginAccepted = globalSocket.State == WebSocketState.Open;
            if (globalSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await globalSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Global smoke complete", closeCancellation.Token); }
                catch (OperationCanceledException) { }
            }

            var globalThrottleResponses = new List<HttpStatusCode>();
            var invalidGlobalCode = new string('9', 12);
            if (invalidGlobalCode == server.PairingCode) invalidGlobalCode = new string('8', 12);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(globalLocalAddress, "api/pair"));
                request.Headers.Host = externalAddress.Authority;
                request.Content = JsonContent.Create(new { code = invalidGlobalCode, deviceName = "Attacker" });
                globalThrottleResponses.Add((await globalClient.SendAsync(request)).StatusCode);
            }
            var globalAttemptLimit = globalThrottleResponses.Take(5).All(value => value == HttpStatusCode.Unauthorized)
                && globalThrottleResponses[5] == HttpStatusCode.TooManyRequests;
            details.Add($"GlobalLoopbackBoundary={globalBoundary}");
            details.Add($"GlobalHostAccepted={globalHostAccepted}");
            details.Add($"GlobalBadHostRejected={globalBadHostRejected}");
            details.Add($"GlobalHsts={globalHsts}");
            details.Add($"GlobalPairAccepted={globalPairAccepted}");
            details.Add($"GlobalSecureCookie={globalSecureCookie}");
            details.Add($"GlobalBadOriginRejected={globalBadOriginRejected}");
            details.Add($"GlobalExactOriginAccepted={globalExactOriginAccepted}");
            details.Add($"GlobalAttemptLimit={globalAttemptLimit}");
            await server.StopAsync();
            stoppedCleanly &= !server.IsRunning;

            const string tailscaleStatusFixture = "{\"BackendState\":\"Running\",\"Self\":{\"Online\":true,\"DNSName\":\"psplus-smoke.example.ts.net.\"}}";
            const string funnelStatusFixture = "{\"TCP\":{\"8443\":{\"HTTPS\":true}},\"Web\":{\"psplus-smoke.example.ts.net:8443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.1:43199\"}}}}}";
            var parsedIdentity = TailscaleFunnelManager.ParseIdentity(tailscaleStatusFixture, @"C:\Program Files\Tailscale\tailscale.exe");
            var funnelContract = parsedIdentity.DnsName == "psplus-smoke.example.ts.net"
                && parsedIdentity.PublicUrl == externalAddress
                && TailscaleFunnelManager.FunnelPortInUse(funnelStatusFixture, TailscaleFunnelManager.HttpsPort)
                && TailscaleFunnelManager.FunnelStatusHasMapping(funnelStatusFixture, parsedIdentity.DnsName,
                    TailscaleFunnelManager.HttpsPort, "http://127.0.0.1:43199")
                && !TailscaleFunnelManager.FunnelPortInUse("{}", TailscaleFunnelManager.HttpsPort);
            var funnelArgumentsSafe = TailscaleFunnelManager.BuildFunnelArguments(43199)
                    .SequenceEqual(["funnel", "--yes", "--https=8443", "http://127.0.0.1:43199"])
                && TailscaleFunnelManager.BuildStopArguments(43199)
                    .SequenceEqual(["funnel", "--https=8443", "http://127.0.0.1:43199", "off"])
                && !TailscaleFunnelManager.BuildFunnelArguments(43199).Contains("--bg", StringComparer.Ordinal);
            details.Add($"FunnelContractParsed={funnelContract}");
            details.Add($"FunnelScopedLifecycleArguments={funnelArgumentsSafe}");

            const string installerIndexFixture = "<a href=\"tailscale-setup-1.98.8.exe\">old</a><a href=\"tailscale-setup-1.98.9.exe\">latest</a>";
            var parsedInstallerUri = TailscaleInstaller.ParseLatestInstallerUri(installerIndexFixture);
            var installerUrlBoundary = parsedInstallerUri == new Uri("https://pkgs.tailscale.com/stable/tailscale-setup-1.98.9.exe")
                && TailscaleInstaller.IsOfficialInstallerUri(parsedInstallerUri)
                && !TailscaleInstaller.IsOfficialInstallerUri(new Uri("http://pkgs.tailscale.com/stable/tailscale-setup-1.98.9.exe"))
                && !TailscaleInstaller.IsOfficialInstallerUri(new Uri("https://evil.example/stable/tailscale-setup-1.98.9.exe"))
                && !TailscaleInstaller.IsOfficialInstallerUri(new Uri("https://pkgs.tailscale.com/stable/tailscale-setup-1.98.9.exe?replace=1"));
            var installerStartInfo = TailscaleInstaller.CreateInstallerStartInfo(@"C:\Temp\tailscale-setup-1.98.9.exe");
            var installerLaunchBoundary = installerStartInfo.UseShellExecute
                && installerStartInfo.FileName == @"C:\Temp\tailscale-setup-1.98.9.exe"
                && installerStartInfo.WorkingDirectory == @"C:\Temp";
            var unsignedInstallerRejected = false;
            var unsignedFixture = Path.Combine(WorkspaceStore.DirectoryPath, "unsigned-tailscale-fixture.exe");
            try
            {
                File.WriteAllText(unsignedFixture, "not an executable or a signed installer");
                try { _ = TailscaleInstaller.VerifyTrustedPublisher(unsignedFixture); }
                catch (InvalidOperationException) { unsignedInstallerRejected = true; }
            }
            finally { try { File.Delete(unsignedFixture); } catch { } }
            details.Add($"TailscaleInstallerUrlBoundary={installerUrlBoundary}");
            details.Add($"TailscaleInstallerLaunchBoundary={installerLaunchBoundary}");
            details.Add($"UnsignedInstallerRejected={unsignedInstallerRejected}");
            var themedDialogContract = PowerShellPlusDialog.ValidateThemeContract();
            details.Add($"ThemedDialogContract={themedDialogContract}");

            var success = assetsEmbedded && responsiveClientEmbedded && stableTerminalSizingEmbedded && rotationManifestEmbedded && securityHeadersPresent && addressMetadataVisible
                && unauthenticatedRejected && wrongCodeRejected && pairingAccepted && savedPairingListed && persistentHttpOnlyCookieIssued && credentialStoredAsHashOnly
                && sessionInventoryVisible && gridMetadataVisible && commandMetadataVisible && badOriginRejected
                && sessionFrameSeen && snapshotSeen && sessionGridSeen && snapshotGridSeen && snapshotComposedSeen && snapshotCursorSeen
                && remoteInputAccepted && liveOutputSeen && outputGridSeen && composedSnapshotContainsMarker
                && queueAccepted && queueVisible && queuedCommandAccepted && queuedCommandOutputSeen && queueRemoved
                && rfc1918Accepted && publicAddressRejected && subnetEnforced
                && pairingSurvivedRestart && savedDeviceVisibleAfterRestart && pairingRevoked && activeSocketRevoked && revokedCredentialRejected
                && globalBoundary && globalHostAccepted && globalBadHostRejected && globalHsts && globalPairAccepted && globalSecureCookie
                && globalBadOriginRejected && globalExactOriginAccepted && globalAttemptLimit && funnelContract && funnelArgumentsSafe
                && installerUrlBoundary && installerLaunchBoundary && unsignedInstallerRejected && themedDialogContract && stoppedCleanly;
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Remote Access preserves LAN behavior and adds a loopback-only, HTTPS-origin-bound, throttled browser-only Global boundary with scoped Funnel lifecycle commands.\n{string.Join(Environment.NewLine, details)}");
            return success;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, $"FAIL Remote Access smoke test threw an exception.\n{string.Join(Environment.NewLine, details)}\nServerError={server?.LastError}\n{exception}");
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
