using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PowerShellPlus.Native;

internal sealed record LanRemoteSession(string Id, string Name, string WorkingDirectory, TerminalPane Pane);
internal sealed record LanRemoteAddress(string Url, string IpAddress, string AdapterName, string AdapterKind, bool IsRecommended)
{
    public string Heading => IsRecommended ? $"{AdapterName} · Recommended" : AdapterName;
    public string Details => $"{Url} · {AdapterKind}";
}

internal sealed class LanRemoteServer : IAsyncDisposable
{
    private const string SessionCookieName = "psp_lan_device";
    private const int MaximumConnections = 8;
    private const int MaximumPairedDevices = 32;
    private const int MaximumInputMessageBytes = 32_768;
    private const int MaximumSnapshotCharacters = 1_000_000;
    private static readonly TimeSpan PairedDeviceLifetime = TimeSpan.FromDays(365);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, byte[]> AssetCache = new(StringComparer.Ordinal);
    private readonly Dispatcher dispatcher;
    private readonly Func<IReadOnlyList<LanRemoteSession>> sessionProvider;
    private readonly object pairedDevicesGate = new();
    private readonly Dictionary<string, LanRemotePairedDevice> pairedDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> persistedLastSeen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairingFailures> pairingFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ActiveRemoteSocket> activeSockets = [];
    private readonly SemaphoreSlim connectionSlots = new(MaximumConnections, MaximumConnections);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private WebApplication? application;
    private LanNetworkPolicy? networkPolicy;
    private IReadOnlyList<IPAddress> listenAddresses = [];
    private int connectedClients;
    private int port;
    private bool disposed;
    private volatile bool allowInput;

    public LanRemoteServer(Dispatcher dispatcher, Func<IReadOnlyList<LanRemoteSession>> sessionProvider)
    {
        this.dispatcher = dispatcher;
        this.sessionProvider = sessionProvider;
        foreach (var device in LanRemotePairingStore.Load())
        {
            pairedDevices[device.Id] = device;
            persistedLastSeen[device.Id] = device.LastSeenUtc;
        }
        PruneExpiredPairedDevices(saveChanges: true);
        PairingCode = CreatePairingCode();
    }

    public bool IsRunning => application is not null;
    public bool AllowInput { get => allowInput; set => allowInput = value; }
    public int ConnectedClients => Volatile.Read(ref connectedClients);
    public string PairingCode { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> Urls { get; private set; } = [];
    public IReadOnlyList<LanRemoteAddress> Addresses { get; private set; } = [];
    public IReadOnlyList<LanRemotePairedDeviceView> PairedDevices
    {
        get
        {
            var connected = activeSockets.Values.Select(value => value.DeviceId).ToHashSet(StringComparer.Ordinal);
            lock (pairedDevicesGate)
            {
                return pairedDevices.Values
                    .OrderByDescending(value => connected.Contains(value.Id))
                    .ThenByDescending(value => value.LastSeenUtc)
                    .Select(value => new LanRemotePairedDeviceView(value.Id, value.Name, value.CreatedUtc, value.LastSeenUtc,
                        value.ExpiresUtc, value.LastAddress, connected.Contains(value.Id)))
                    .ToArray();
            }
        }
    }

    public async Task StartAsync(bool loopbackOnly = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (application is not null) return;

        var policy = loopbackOnly ? LanNetworkPolicy.CreateLoopbackOnly() : LanNetworkPolicy.CreateForActivePrivateNetworks();
        if (!loopbackOnly && policy.PrivateAddresses.Count == 0)
            throw new InvalidOperationException("No active private IPv4 LAN connection was found. Connect this PC to a private Wi-Fi or Ethernet network, then try again.");

        var addresses = new[] { IPAddress.Loopback }.Concat(policy.PrivateAddresses).Distinct().ToArray();
        var selectedPort = FindAvailablePort(addresses);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LanRemoteServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestHeaderCount = 40;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in addresses)
                options.Listen(address, selectedPort, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        networkPolicy = policy;
        listenAddresses = addresses;
        port = selectedPort;
        PairingCode = CreatePairingCode();
        pairingFailures.Clear();
        PruneExpiredPairedDevices(saveChanges: true);
        Addresses = loopbackOnly
            ? [new LanRemoteAddress($"http://{IPAddress.Loopback}:{selectedPort}", IPAddress.Loopback.ToString(), "This PC", "Loopback test address", true)]
            : policy.AddressDetails.Select((address, index) => new LanRemoteAddress(
                $"http://{address.Address}:{selectedPort}", address.Address.ToString(), address.AdapterName,
                address.IsVirtual ? "Virtual adapter" : address.HasGateway ? "Wi-Fi/Ethernet with internet gateway" : "Private network adapter",
                index == 0)).ToArray();
        Urls = Addresses.Select(value => value.Url).ToArray();

        app.Use(async (context, next) =>
        {
            ApplySecurityHeaders(context.Response);
            if (!IsAllowedRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("LAN access denied.");
                return;
            }
            await next();
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
        app.MapGet("/", () => AssetResult("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => AssetResult("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/styles.css", () => AssetResult("styles.css", "text/css; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", () => AssetResult("manifest.webmanifest", "application/manifest+json; charset=utf-8"));
        app.MapGet("/vendor/xterm.js", () => AssetResult("xterm.js", "text/javascript; charset=utf-8"));
        app.MapGet("/vendor/xterm.css", () => AssetResult("xterm.css", "text/css; charset=utf-8"));
        app.MapGet("/vendor/xterm-license", () => AssetResult("xterm.LICENSE", "text/plain; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapPost("/api/pair", (Delegate)(Func<HttpContext, Task<IResult>>)PairAsync);
        app.MapGet("/api/sessions", (Delegate)(Func<HttpContext, Task<IResult>>)SessionsAsync);
        app.MapGet("/api/health", (Delegate)(Func<HttpContext, Task<IResult>>)HealthAsync);
        app.MapGet("/ws", HandleWebSocketAsync);

        try
        {
            await app.StartAsync(cancellationToken);
            application = app;
        }
        catch
        {
            await app.DisposeAsync();
            networkPolicy = null;
            listenAddresses = [];
            Urls = [];
            Addresses = [];
            throw;
        }
    }

    public async Task StopAsync()
    {
        var app = application;
        application = null;
        if (app is null) return;
        PairingCode = CreatePairingCode();
        foreach (var socket in activeSockets.Values)
        {
            try
            {
                if (socket.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "LAN sharing stopped", CancellationToken.None);
            }
            catch { }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
        networkPolicy = null;
        listenAddresses = [];
        Urls = [];
        Addresses = [];
        SavePairedDevicesNoThrow();
    }

    public async Task<bool> RevokePairedDeviceAsync(string deviceId)
    {
        LanRemotePairedDevice? removed;
        lock (pairedDevicesGate)
        {
            if (!pairedDevices.Remove(deviceId, out removed)) return false;
            persistedLastSeen.Remove(deviceId);
            try { LanRemotePairingStore.Save(pairedDevices.Values); }
            catch
            {
                pairedDevices[deviceId] = removed;
                persistedLastSeen[deviceId] = removed.LastSeenUtc;
                throw;
            }
        }

        foreach (var connection in activeSockets.Values.Where(value => value.DeviceId == deviceId))
        {
            try
            {
                if (connection.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Paired device removed", CancellationToken.None);
            }
            catch { }
        }
        return true;
    }

    private bool IsAllowedRequest(HttpContext context)
    {
        var remote = NormalizeAddress(context.Connection.RemoteIpAddress);
        if (remote is null || networkPolicy?.Allows(remote) != true) return false;
        var host = context.Request.Host;
        if (host.Port != port || string.IsNullOrWhiteSpace(host.Host)) return false;
        if (host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IsLoopback(remote);
        return IPAddress.TryParse(host.Host, out var hostAddress)
            && listenAddresses.Contains(NormalizeAddress(hostAddress));
    }

    private async Task<IResult> PairAsync(HttpContext context)
    {
        var remoteKey = NormalizeAddress(context.Connection.RemoteIpAddress)?.ToString() ?? "unknown";
        var failures = pairingFailures.GetOrAdd(remoteKey, _ => new PairingFailures());
        if (failures.IsBlocked(DateTimeOffset.UtcNow))
            return Results.Json(new { error = "Too many attempts. Wait one minute and try again." }, statusCode: StatusCodes.Status429TooManyRequests);

        PairRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<PairRequest>(JsonOptions, context.RequestAborted);
        }
        catch (JsonException)
        {
            failures.RecordFailure(DateTimeOffset.UtcNow);
            return Results.Json(new { error = "Invalid pairing request." }, statusCode: StatusCodes.Status400BadRequest);
        }
        if (request is null || !PairingCodeMatches(request.Code))
        {
            failures.RecordFailure(DateTimeOffset.UtcNow);
            return Results.Json(new { error = "That pairing code did not match." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        failures.Reset();
        var now = DateTimeOffset.UtcNow;
        PruneExpiredPairedDevices(saveChanges: true);
        var deviceName = NormalizeDeviceName(request.DeviceName, context.Request.Headers.UserAgent.ToString());
        var deviceId = Guid.NewGuid().ToString("N");
        var secret = CreateSessionToken();
        var token = $"{deviceId}.{secret}";
        var expires = now.Add(PairedDeviceLifetime);
        var device = new LanRemotePairedDevice
        {
            Id = deviceId,
            Name = deviceName,
            SecretHash = LanRemotePairingStore.HashSecret(secret),
            CreatedUtc = now,
            LastSeenUtc = now,
            ExpiresUtc = expires,
            LastAddress = remoteKey,
            UserAgent = Limit(context.Request.Headers.UserAgent.ToString(), 256)
        };
        lock (pairedDevicesGate)
        {
            if (pairedDevices.Count >= MaximumPairedDevices)
                return Results.Json(new { error = "The saved-device list is full. Remove a device in the LAN Remote window, then try again." }, statusCode: StatusCodes.Status409Conflict);
            pairedDevices[device.Id] = device;
            persistedLastSeen[device.Id] = now;
            try { LanRemotePairingStore.Save(pairedDevices.Values); }
            catch
            {
                pairedDevices.Remove(device.Id);
                persistedLastSeen.Remove(device.Id);
                return Results.Json(new { error = "PowerShellPlus could not save this paired device." }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        context.Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
            MaxAge = PairedDeviceLifetime,
            IsEssential = true
        });
        PairingCode = CreatePairingCode();
        return Results.Json(new { paired = true, deviceId, deviceName, expiresUtc = expires });
    }

    private async Task<IResult> SessionsAsync(HttpContext context)
    {
        if (!TryAuthorize(context, out _)) return Results.Unauthorized();
        var workspace = await GetRemoteWorkspaceStateAsync();
        return Results.Json(new
        {
            allowInput = AllowInput,
            sessions = workspace.Views,
            quickCommands = workspace.QuickCommands
        }, JsonOptions);
    }

    private async Task<IResult> HealthAsync(HttpContext context)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress)) return Results.NotFound();
        var sessions = await GetSessionsAsync();
        return Results.Json(new { status = "ok", mode = "lan", sessions = sessions.Count });
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!TryAuthorize(context, out var authorization))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!HasAllowedOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!connectionSlots.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var socketId = Guid.NewGuid();
        WebSocket? socket = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            activeSockets[socketId] = new ActiveRemoteSocket(socket, authorization.DeviceId);
            Interlocked.Increment(ref connectedClients);
            var remaining = authorization.ExpiresUtc - DateTimeOffset.UtcNow;
            using var expiryCancellation = new CancellationTokenSource(remaining < TimeSpan.FromHours(24) ? remaining : TimeSpan.FromHours(24));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetimeCancellation.Token, expiryCancellation.Token);
            await RunConnectionAsync(socket, linked.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception exception)
        {
            LastError = exception.ToString();
            try
            {
                Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
                File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] LAN Remote WebSocket: {exception}\n");
            }
            catch { }
        }
        finally
        {
            if (socket is not null)
            {
                activeSockets.TryRemove(socketId, out _);
                Interlocked.Decrement(ref connectedClients);
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                }
                catch { }
                socket.Dispose();
            }
            connectionSlots.Release();
        }
    }

    private async Task RunConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var output = Channel.CreateBounded<OutputFrame>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        var subscriptions = new Dictionary<string, (TerminalPane Pane, Action<TerminalPane, string> Handler)>(StringComparer.Ordinal);
        var sendLock = new SemaphoreSlim(1, 1);
        var outputDropped = 0;
        var rateLimiter = new SlidingMessageRateLimiter(600, TimeSpan.FromMinutes(1));

        async Task SendAsync(object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await sendLock.WaitAsync(cancellationToken);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
            finally { sendLock.Release(); }
        }

        async Task RefreshSubscriptionsAsync(bool forceSnapshots)
        {
            var workspace = await GetRemoteWorkspaceStateAsync();
            var sessions = workspace.Sessions;
            var currentIds = sessions.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in subscriptions.Keys.Where(value => !currentIds.Contains(value)).ToArray())
            {
                var subscription = subscriptions[stale];
                subscription.Pane.RawOutputReceived -= subscription.Handler;
                subscriptions.Remove(stale);
            }
            var added = new List<LanRemoteSession>();
            foreach (var session in sessions)
            {
                if (subscriptions.ContainsKey(session.Id)) continue;
                await dispatcher.InvokeAsync(session.Pane.EnableRemoteOutputCapture, DispatcherPriority.Background);
                Action<TerminalPane, string> handler = (_, data) =>
                {
                    var dimensions = session.Pane.GetRemoteDimensions();
                    if (!output.Writer.TryWrite(new OutputFrame(session.Id, data, dimensions.Columns, dimensions.Rows)))
                        Interlocked.Exchange(ref outputDropped, 1);
                };
                session.Pane.RawOutputReceived += handler;
                subscriptions[session.Id] = (session.Pane, handler);
                added.Add(session);
            }
            await SendAsync(new
            {
                type = "sessions",
                allowInput = AllowInput,
                sessions = workspace.Views,
                quickCommands = workspace.QuickCommands
            });
            foreach (var session in forceSnapshots ? sessions : added)
            {
                var source = await dispatcher.InvokeAsync(session.Pane.GetRemoteSnapshotSource, DispatcherPriority.Background);
                var snapshot = await Task.Run(() => TerminalPane.CaptureRemoteScreen(source), cancellationToken);
                if (snapshot.Text.Length > MaximumSnapshotCharacters)
                {
                    var text = snapshot.Text[^MaximumSnapshotCharacters..];
                    var firstCompleteLine = text.IndexOf('\n');
                    if (firstCompleteLine >= 0) text = text[(firstCompleteLine + 1)..];
                    snapshot = snapshot with { Text = text };
                }
                await SendAsync(new
                {
                    type = "snapshot",
                    sessionId = session.Id,
                    data = snapshot.Text,
                    columns = snapshot.Columns,
                    rows = snapshot.Rows,
                    cursorColumn = snapshot.CursorColumn,
                    cursorRow = snapshot.CursorRow,
                    composed = snapshot.IsComposed
                });
                await dispatcher.InvokeAsync(session.Pane.RequestRemoteRedraw, DispatcherPriority.Background);
            }
        }

        async Task PumpOutputAsync()
        {
            await foreach (var frame in output.Reader.ReadAllAsync(cancellationToken))
            {
                if (Interlocked.Exchange(ref outputDropped, 0) != 0)
                {
                    while (output.Reader.TryRead(out _)) { }
                    await SendAsync(new { type = "resync" });
                    continue;
                }
                await SendAsync(new
                {
                    type = "output",
                    sessionId = frame.SessionId,
                    data = frame.Data,
                    columns = frame.Columns,
                    rows = frame.Rows
                });
            }
        }

        await RefreshSubscriptionsAsync(true);
        var outputTask = PumpOutputAsync();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken);
                if (message is null) break;
                if (!rateLimiter.TryTake(DateTimeOffset.UtcNow))
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Message rate exceeded", cancellationToken);
                    break;
                }
                using var document = message;
                if (!document.RootElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) continue;
                var type = typeElement.GetString();
                if (type == "sync")
                {
                    var snapshots = document.RootElement.TryGetProperty("snapshots", out var snapshotsElement) && snapshotsElement.ValueKind == JsonValueKind.True;
                    await RefreshSubscriptionsAsync(snapshots);
                    continue;
                }
                if (type is not ("input" or "queue-add" or "command")) continue;
                if (!AllowInput)
                {
                    await SendAsync(new { type = "input-denied" });
                    continue;
                }
                if (!document.RootElement.TryGetProperty("sessionId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String) continue;
                var sessionId = idElement.GetString();
                var sessions = await GetSessionsAsync();
                var session = sessions.FirstOrDefault(value => value.Id.Equals(sessionId, StringComparison.Ordinal));
                if (string.IsNullOrEmpty(sessionId) || session is null) continue;

                if (type == "input")
                {
                    if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String) continue;
                    var data = dataElement.GetString();
                    if (string.IsNullOrEmpty(data) || data.Length > MaximumInputMessageBytes) continue;
                    var accepted = await dispatcher.InvokeAsync(() => session.Pane.WriteRemoteInput(data), DispatcherPriority.Input);
                    await SendAsync(new { type = "input-ack", sessionId, accepted });
                    continue;
                }

                if (!document.RootElement.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String) continue;
                var command = commandElement.GetString();
                if (string.IsNullOrWhiteSpace(command) || command.Length > MaximumInputMessageBytes) continue;
                var requestId = document.RootElement.TryGetProperty("requestId", out var requestElement) && requestElement.ValueKind == JsonValueKind.String
                    ? requestElement.GetString()
                    : null;
                if (type == "queue-add")
                {
                    var accepted = await dispatcher.InvokeAsync(() => session.Pane.QueueRemoteCommand(command), DispatcherPriority.Input);
                    await SendAsync(new { type = "queue-ack", sessionId, requestId, accepted });
                    await RefreshSubscriptionsAsync(false);
                    continue;
                }

                int? queuedIndex = null;
                if (document.RootElement.TryGetProperty("queueIndex", out var queueElement)
                    && queueElement.ValueKind == JsonValueKind.Number && queueElement.TryGetInt32(out var parsedIndex))
                    queuedIndex = parsedIndex;
                var commandTask = await dispatcher.InvokeAsync(
                    () => session.Pane.RunRemoteCommandAsync(command, queuedIndex), DispatcherPriority.Input);
                var commandAccepted = await commandTask;
                await SendAsync(new { type = "command-ack", sessionId, requestId, accepted = commandAccepted });
                await RefreshSubscriptionsAsync(false);
            }
        }
        finally
        {
            output.Writer.TryComplete();
            foreach (var subscription in subscriptions.Values) subscription.Pane.RawOutputReceived -= subscription.Handler;
            try { await outputTask; } catch (OperationCanceledException) { }
            sendLock.Dispose();
        }
    }

    private async Task<IReadOnlyList<LanRemoteSession>> GetSessionsAsync()
    {
        if (dispatcher.CheckAccess()) return sessionProvider();
        return await dispatcher.InvokeAsync(sessionProvider, DispatcherPriority.Background);
    }

    private async Task<RemoteWorkspaceState> GetRemoteWorkspaceStateAsync()
    {
        if (dispatcher.CheckAccess()) return BuildRemoteWorkspaceState();
        return await dispatcher.InvokeAsync(BuildRemoteWorkspaceState, DispatcherPriority.Background);
    }

    private RemoteWorkspaceState BuildRemoteWorkspaceState()
    {
        var sessions = sessionProvider();
        var views = sessions.Select(session =>
        {
            var dimensions = session.Pane.GetRemoteDimensions();
            var appearance = session.Pane.GetRemoteAppearance();
            return new RemoteSessionView(session.Id, session.Name, session.WorkingDirectory,
                dimensions.Columns, dimensions.Rows, appearance.FontFace, appearance.FontSize,
                session.Pane.GetRemotePendingCommands());
        }).ToArray();
        var quickCommands = sessions.FirstOrDefault()?.Pane.GetRemoteQuickCommands()
            .Select(value => new RemoteQuickCommand(value.Id, value.Name, value.Category, value.Command))
            .ToArray() ?? [];
        return new RemoteWorkspaceState(sessions, views, quickCommands);
    }

    private bool TryAuthorize(HttpContext context, out DeviceAuthorization authorization)
    {
        authorization = default;
        if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var token) || string.IsNullOrWhiteSpace(token)) return false;
        var separator = token.IndexOf('.');
        if (separator != 32 || separator == token.Length - 1) return false;
        var deviceId = token[..separator];
        var secret = token[(separator + 1)..];
        var now = DateTimeOffset.UtcNow;
        var saveLastSeen = false;
        var removedExpired = false;
        lock (pairedDevicesGate)
        {
            if (!pairedDevices.TryGetValue(deviceId, out var device) || !LanRemotePairingStore.SecretMatches(device.SecretHash, secret)) return false;
            if (device.ExpiresUtc <= now)
            {
                pairedDevices.Remove(deviceId);
                persistedLastSeen.Remove(deviceId);
                removedExpired = true;
            }
            else
            {
                device.LastSeenUtc = now;
                device.LastAddress = NormalizeAddress(context.Connection.RemoteIpAddress)?.ToString() ?? device.LastAddress;
                authorization = new DeviceAuthorization(device.Id, device.ExpiresUtc);
                if (!persistedLastSeen.TryGetValue(deviceId, out var persisted) || now - persisted >= TimeSpan.FromMinutes(2))
                {
                    persistedLastSeen[deviceId] = now;
                    saveLastSeen = true;
                }
            }
        }
        if (removedExpired || saveLastSeen) SavePairedDevicesNoThrow();
        return authorization.DeviceId is not null;
    }

    private bool HasAllowedOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.Port != port) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IsLoopback(context.Connection.RemoteIpAddress);
        return IPAddress.TryParse(uri.Host, out var address) && listenAddresses.Contains(NormalizeAddress(address));
    }

    private bool PairingCodeMatches(string? candidate)
    {
        if (candidate is null || candidate.Length != PairingCode.Length) return false;
        var expectedBytes = Encoding.ASCII.GetBytes(PairingCode);
        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private void PruneExpiredPairedDevices(bool saveChanges)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        lock (pairedDevicesGate)
        {
            foreach (var deviceId in pairedDevices.Values.Where(value => value.ExpiresUtc <= now).Select(value => value.Id).ToArray())
            {
                changed |= pairedDevices.Remove(deviceId);
                persistedLastSeen.Remove(deviceId);
            }
        }
        if (changed && saveChanges) SavePairedDevicesNoThrow();
    }

    private void SavePairedDevicesNoThrow()
    {
        try
        {
            LanRemotePairedDevice[] snapshot;
            lock (pairedDevicesGate) snapshot = pairedDevices.Values.Select(value => value.Clone()).ToArray();
            LanRemotePairingStore.Save(snapshot);
        }
        catch (Exception exception) { LastError = exception.ToString(); }
    }

    private static string NormalizeDeviceName(string? requested, string userAgent)
    {
        var value = string.Concat((requested ?? string.Empty).Where(character => !char.IsControl(character))).Trim();
        if (value.Length > 60) value = value[..60];
        if (value.Length > 0) return value;
        var platform = userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android device"
            : userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows PC"
            : userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "Mac"
            : "LAN device";
        var browser = userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : string.Empty;
        return browser.Length == 0 ? platform : $"{platform} · {browser}";
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static IResult AssetResult(string assetName, string contentType)
    {
        var bytes = AssetCache.GetOrAdd(assetName, static name =>
        {
            var resourceName = $"PowerShellPlus.Native.RemoteWeb.{name}";
            using var stream = typeof(LanRemoteServer).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded LAN Remote asset was not found: {resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });
        return Results.Bytes(bytes, contentType);
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws:; img-src 'self' data:; font-src 'self'; manifest-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    private static async Task<JsonDocument?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var memory = writer.GetMemory(4096);
            var result = await socket.ReceiveAsync(memory, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "Text messages only", cancellationToken);
                return null;
            }
            writer.Advance(result.Count);
            if (writer.WrittenCount > MaximumInputMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", cancellationToken);
                return null;
            }
            if (result.EndOfMessage) break;
        }
        try { return JsonDocument.Parse(writer.WrittenMemory); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static int FindAvailablePort(IReadOnlyList<IPAddress> addresses)
    {
        var start = RandomNumberGenerator.GetInt32(43170, 43240);
        for (var offset = 0; offset < 70; offset++)
        {
            var candidate = 43170 + ((start - 43170 + offset) % 70);
            var sockets = new List<Socket>();
            try
            {
                foreach (var address in addresses)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                    socket.Bind(new IPEndPoint(address, candidate));
                    sockets.Add(socket);
                }
                return candidate;
            }
            catch (SocketException) { }
            finally { foreach (var socket in sockets) socket.Dispose(); }
        }
        throw new InvalidOperationException("PowerShellPlus could not find an available LAN Remote port in the reserved 43170-43239 range.");
    }

    private static string CreatePairingCode() => RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private static string CreateSessionToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static IPAddress? NormalizeAddress(IPAddress? address) => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    private static bool IsLoopback(IPAddress? address) => NormalizeAddress(address) is { } normalized && IPAddress.IsLoopback(normalized);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetimeCancellation.Cancel();
        await StopAsync();
        lifetimeCancellation.Dispose();
        connectionSlots.Dispose();
    }

    public void SignalShutdown() => lifetimeCancellation.Cancel();

    private sealed record PairRequest(string Code, string? DeviceName = null);
    private readonly record struct DeviceAuthorization(string DeviceId, DateTimeOffset ExpiresUtc);
    private sealed record ActiveRemoteSocket(WebSocket Socket, string DeviceId);
    private sealed record OutputFrame(string SessionId, string Data, int Columns, int Rows);
    private sealed record RemoteSessionView(string Id, string Name, string WorkingDirectory, int Columns, int Rows,
        string FontFace, int FontSize, IReadOnlyList<string> PendingCommands);
    private sealed record RemoteQuickCommand(string Id, string Name, string Category, string Command);
    private sealed record RemoteWorkspaceState(IReadOnlyList<LanRemoteSession> Sessions,
        IReadOnlyList<RemoteSessionView> Views, IReadOnlyList<RemoteQuickCommand> QuickCommands);

    private sealed class PairingFailures
    {
        private readonly Queue<DateTimeOffset> failures = new();
        public bool IsBlocked(DateTimeOffset now)
        {
            lock (failures)
            {
                Trim(now);
                return failures.Count >= 5;
            }
        }
        public void RecordFailure(DateTimeOffset now) { lock (failures) { Trim(now); failures.Enqueue(now); } }
        public void Reset() { lock (failures) failures.Clear(); }
        private void Trim(DateTimeOffset now) { while (failures.TryPeek(out var value) && now - value > TimeSpan.FromMinutes(1)) failures.Dequeue(); }
    }

    private sealed class SlidingMessageRateLimiter(int maximum, TimeSpan window)
    {
        private readonly Queue<DateTimeOffset> messages = new();
        public bool TryTake(DateTimeOffset now)
        {
            while (messages.TryPeek(out var value) && now - value > window) messages.Dequeue();
            if (messages.Count >= maximum) return false;
            messages.Enqueue(now);
            return true;
        }
    }
}

internal sealed class LanNetworkPolicy
{
    private readonly IReadOnlyList<LanNetwork> networks;
    private readonly bool loopbackOnly;
    private LanNetworkPolicy(IReadOnlyList<LanNetwork> networks, bool loopbackOnly)
    {
        this.networks = networks.OrderByDescending(value => value.Priority).ThenBy(value => value.AdapterName, StringComparer.OrdinalIgnoreCase).ToArray();
        this.loopbackOnly = loopbackOnly;
        AddressDetails = this.networks.GroupBy(value => value.Address).Select(value => value.First()).ToArray();
        PrivateAddresses = AddressDetails.Select(value => value.Address).ToArray();
    }

    public IReadOnlyList<IPAddress> PrivateAddresses { get; }
    public IReadOnlyList<LanNetwork> AddressDetails { get; }
    public bool Allows(IPAddress address) => IPAddress.IsLoopback(address) || (!loopbackOnly && networks.Any(network => network.Contains(address)));

    public static LanNetworkPolicy CreateLoopbackOnly() => new([], true);

    public static LanNetworkPolicy CreateForActivePrivateNetworks()
    {
        var networks = new List<LanNetwork>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            var hasGateway = properties.GatewayAddresses.Any(value => value.Address.AddressFamily == AddressFamily.InterNetwork
                && !value.Address.Equals(IPAddress.Any));
            var isVirtual = IsVirtualAdapter(adapter);
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                var mask = unicast.IPv4Mask;
                if (address.AddressFamily != AddressFamily.InterNetwork || mask is null || !IsRfc1918(address)) continue;
                networks.Add(new LanNetwork(address, mask, adapter.Name, adapter.Description, adapter.NetworkInterfaceType, hasGateway, isVirtual));
            }
        }
        return new LanNetworkPolicy(networks, false);
    }

    public static bool IsRfc1918(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168));
    }

    private static bool IsVirtualAdapter(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        return identity.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("vmware", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("docker", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("wsl", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("host-only", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record LanNetwork(IPAddress Address, IPAddress Mask, string AdapterName = "Private network",
        string AdapterDescription = "", NetworkInterfaceType InterfaceType = NetworkInterfaceType.Unknown,
        bool HasGateway = false, bool IsVirtual = false)
    {
        public int Priority => (IsVirtual ? -1000 : 0) + (HasGateway ? 500 : 0)
            + (InterfaceType == NetworkInterfaceType.Wireless80211 ? 200
                : InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet ? 150 : 0);

        public bool Contains(IPAddress candidate)
        {
            if (candidate.AddressFamily != AddressFamily.InterNetwork) return false;
            var local = Address.GetAddressBytes();
            var remote = candidate.GetAddressBytes();
            var mask = Mask.GetAddressBytes();
            for (var index = 0; index < 4; index++) if ((local[index] & mask[index]) != (remote[index] & mask[index])) return false;
            return true;
        }
    }
}
