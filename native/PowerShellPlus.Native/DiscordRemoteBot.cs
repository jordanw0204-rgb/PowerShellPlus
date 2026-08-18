using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

internal sealed class DiscordRemoteBotSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public List<string> AllowedUserIds { get; set; } = [];
    public bool ReceiveChannelMessages { get; set; } = true;
    public bool MirrorTerminalOutput { get; set; } = true;
    public bool AllowPermissionChanges { get; set; }
    public bool ReconnectOnStartup { get; set; } = true;
    public string SelectedTerminalId { get; set; } = string.Empty;

    public DiscordRemoteBotSettings Clone() => new()
    {
        BotToken = BotToken,
        ApplicationId = ApplicationId,
        GuildId = GuildId,
        ChannelId = ChannelId,
        AllowedUserIds = AllowedUserIds.ToList(),
        ReceiveChannelMessages = ReceiveChannelMessages,
        MirrorTerminalOutput = MirrorTerminalOutput,
        AllowPermissionChanges = AllowPermissionChanges,
        ReconnectOnStartup = ReconnectOnStartup,
        SelectedTerminalId = SelectedTerminalId
    };
}

internal static class DiscordRemoteBotStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PowerShellPlus.DiscordRemoteBot.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string FilePath => Path.Combine(WorkspaceStore.DirectoryPath, "discord-remote-bot.json");

    internal static DiscordRemoteBotSettings Load(string? filePath = null)
    {
        try
        {
            var path = filePath ?? FilePath;
            if (!File.Exists(path)) return new DiscordRemoteBotSettings();
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot is not { Version: 1 }) return new DiscordRemoteBotSettings();
            var token = string.Empty;
            if (!string.IsNullOrWhiteSpace(snapshot.ProtectedBotToken))
            {
                var protectedBytes = Convert.FromBase64String(snapshot.ProtectedBotToken);
                var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                try { token = Encoding.UTF8.GetString(clearBytes); }
                finally { CryptographicOperations.ZeroMemory(clearBytes); }
            }
            return new DiscordRemoteBotSettings
            {
                BotToken = token,
                ApplicationId = snapshot.ApplicationId ?? string.Empty,
                GuildId = snapshot.GuildId ?? string.Empty,
                ChannelId = snapshot.ChannelId ?? string.Empty,
                AllowedUserIds = snapshot.AllowedUserIds?.Where(IsSnowflake).Distinct(StringComparer.Ordinal).ToList() ?? [],
                ReceiveChannelMessages = snapshot.ReceiveChannelMessages,
                MirrorTerminalOutput = snapshot.MirrorTerminalOutput,
                AllowPermissionChanges = snapshot.AllowPermissionChanges,
                ReconnectOnStartup = snapshot.ReconnectOnStartup,
                SelectedTerminalId = snapshot.SelectedTerminalId ?? string.Empty
            };
        }
        catch { return new DiscordRemoteBotSettings(); }
    }

    internal static void Save(DiscordRemoteBotSettings settings, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = filePath ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedToken = string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.BotToken))
        {
            var clearBytes = Encoding.UTF8.GetBytes(settings.BotToken.Trim());
            try
            {
                protectedToken = Convert.ToBase64String(ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser));
            }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
        }
        var snapshot = new Snapshot
        {
            ProtectedBotToken = protectedToken,
            ApplicationId = settings.ApplicationId.Trim(),
            GuildId = settings.GuildId.Trim(),
            ChannelId = settings.ChannelId.Trim(),
            AllowedUserIds = settings.AllowedUserIds.Where(IsSnowflake).Distinct(StringComparer.Ordinal).ToList(),
            ReceiveChannelMessages = settings.ReceiveChannelMessages,
            MirrorTerminalOutput = settings.MirrorTerminalOutput,
            AllowPermissionChanges = settings.AllowPermissionChanges,
            ReconnectOnStartup = settings.ReconnectOnStartup,
            SelectedTerminalId = settings.SelectedTerminalId
        };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, path, true);
    }

    internal static bool EncryptionContractPassesForTest(string directory)
    {
        var path = Path.Combine(directory, "discord-bot-smoke.json");
        const string token = "MTIzNDU2Nzg5MDEyMzQ1Njc4.fake_signature_for_smoke_test";
        try
        {
            Save(new DiscordRemoteBotSettings
            {
                BotToken = token,
                ApplicationId = "123456789012345678",
                GuildId = "223456789012345678",
                ChannelId = "323456789012345678",
                AllowedUserIds = ["423456789012345678"],
                ReceiveChannelMessages = true,
                MirrorTerminalOutput = true,
                ReconnectOnStartup = true
            }, path);
            var serialized = File.ReadAllText(path);
            var restored = Load(path);
            return !serialized.Contains(token, StringComparison.Ordinal)
                && restored.BotToken == token
                && restored.AllowedUserIds.SequenceEqual(["423456789012345678"])
                && restored.ReceiveChannelMessages && restored.MirrorTerminalOutput && restored.ReconnectOnStartup;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    internal static bool IsSnowflake(string? value) => value is { Length: >= 16 and <= 20 }
        && ulong.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public string ProtectedBotToken { get; set; } = string.Empty;
        public string? ApplicationId { get; set; }
        public string? GuildId { get; set; }
        public string? ChannelId { get; set; }
        public List<string>? AllowedUserIds { get; set; }
        public bool ReceiveChannelMessages { get; set; } = true;
        public bool MirrorTerminalOutput { get; set; } = true;
        public bool AllowPermissionChanges { get; set; }
        public bool ReconnectOnStartup { get; set; } = true;
        public string? SelectedTerminalId { get; set; }
    }
}

internal sealed class DiscordRemoteBotService : IAsyncDisposable
{
    private const string ApiRoot = "https://discord.com/api/v10/";
    private const int GuildsIntent = 1 << 0;
    private const int GuildMessagesIntent = 1 << 9;
    private const int MessageContentIntent = 1 << 15;
    private readonly Dispatcher dispatcher;
    private readonly Func<IReadOnlyList<LanRemoteSession>> sessionProvider;
    private readonly HttpClient client;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim gatewaySendGate = new(1, 1);
    private readonly SemaphoreSlim subscriptionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (TerminalPane Pane, Action<TerminalPane, string> Handler)> subscriptions = new(StringComparer.Ordinal);
    private CancellationTokenSource? lifetimeCancellation;
    private Task? gatewayTask;
    private Task? outputTask;
    private ClientWebSocket? activeSocket;
    private Channel<DiscordOutputFrame>? output;
    private DiscordRemoteBotSettings settings = new();
    private string botUserId = string.Empty;
    private string statusText = "Discord bot is stopped";
    private int connected;
    private int disposed;

    internal DiscordRemoteBotService(Dispatcher dispatcher, Func<IReadOnlyList<LanRemoteSession>> sessionProvider,
        HttpClient? client = null)
    {
        this.dispatcher = dispatcher;
        this.sessionProvider = sessionProvider;
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    internal event EventHandler? StatusChanged;
    internal bool IsRunning => lifetimeCancellation is not null;
    internal bool IsConnected => Volatile.Read(ref connected) != 0;
    internal string StatusText => Volatile.Read(ref statusText);

    internal static bool TryValidateSettings(DiscordRemoteBotSettings value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value.BotToken) || value.BotToken.Length is < 30 or > 256
            || value.BotToken.Any(char.IsWhiteSpace) || !value.BotToken.Contains('.', StringComparison.Ordinal))
            error = "Paste the bot token from the Discord Developer Portal.";
        else if (!DiscordRemoteBotStore.IsSnowflake(value.ApplicationId)) error = "Application ID must be a Discord numeric ID.";
        else if (!DiscordRemoteBotStore.IsSnowflake(value.GuildId)) error = "Server ID must be a Discord numeric ID.";
        else if (!DiscordRemoteBotStore.IsSnowflake(value.ChannelId)) error = "Channel ID must be a Discord numeric ID.";
        else if (value.AllowedUserIds.Count == 0 || value.AllowedUserIds.Any(id => !DiscordRemoteBotStore.IsSnowflake(id)))
            error = "Add at least one allowed Discord user ID.";
        return error.Length == 0;
    }

    internal async Task StartAsync(DiscordRemoteBotSettings requested, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!TryValidateSettings(requested, out var error)) throw new InvalidOperationException(error);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            settings = requested.Clone();
            settings.BotToken = settings.BotToken.Trim();
            settings.ApplicationId = settings.ApplicationId.Trim();
            settings.GuildId = settings.GuildId.Trim();
            settings.ChannelId = settings.ChannelId.Trim();
            settings.AllowedUserIds = settings.AllowedUserIds.Select(value => value.Trim())
                .Where(DiscordRemoteBotStore.IsSnowflake).Distinct(StringComparer.Ordinal).ToList();
            DiscordRemoteBotStore.Save(settings);
            SetStatus("Validating Discord bot…");
            botUserId = await ValidateBotAsync(cancellationToken);
            SetStatus("Registering PowerShellPlus slash commands…");
            await RegisterCommandsAsync(cancellationToken);
            output = Channel.CreateBounded<DiscordOutputFrame>(new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await RefreshSubscriptionsAsync();
            outputTask = PumpOutputAsync(lifetimeCancellation.Token);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gatewayTask = RunGatewayLoopAsync(ready, lifetimeCancellation.Token);
            try
            {
                var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));
                if (!ReferenceEquals(completed, ready.Task) || !await ready.Task)
                    throw new InvalidOperationException("Discord did not complete the Gateway connection within 15 seconds. Verify the token and enable Message Content Intent on the Bot page.");
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }
        }
        finally { lifecycleGate.Release(); }
    }

    internal async Task StopAsync()
    {
        await lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycleGate.Release(); }
    }

    internal void SignalShutdown()
    {
        try { lifetimeCancellation?.Cancel(); } catch { }
        try { activeSocket?.Abort(); } catch { }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = lifetimeCancellation;
        lifetimeCancellation = null;
        if (cancellation is null)
        {
            SetConnected(false);
            return;
        }
        cancellation.Cancel();
        var socket = activeSocket;
        activeSocket = null;
        try
        {
            if (socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "PowerShellPlus bot stopped", CancellationToken.None);
        }
        catch { }
        socket?.Dispose();
        output?.Writer.TryComplete();
        if (gatewayTask is not null) try { await gatewayTask; } catch (OperationCanceledException) { } catch { }
        if (outputTask is not null) try { await outputTask; } catch (OperationCanceledException) { } catch { }
        gatewayTask = null;
        outputTask = null;
        output = null;
        cancellation.Dispose();
        await ClearSubscriptionsAsync();
        SetConnected(false);
        SetStatus("Discord bot is stopped");
    }

    private async Task<string> ValidateBotAsync(CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Get, "users/@me", null, true, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var bot = root.TryGetProperty("bot", out var botElement) && botElement.ValueKind == JsonValueKind.True;
        if (!bot || !DiscordRemoteBotStore.IsSnowflake(id)) throw new InvalidOperationException("The supplied token is not a Discord bot token.");
        return id!;
    }

    private async Task RegisterCommandsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Put,
            $"applications/{settings.ApplicationId}/guilds/{settings.GuildId}/commands", BuildCommandDefinitions(), true, cancellationToken);
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task RunGatewayLoopAsync(TaskCompletionSource<bool> firstReady, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                SetStatus("Connecting to Discord Gateway…");
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                activeSocket = socket;
                await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), cancellationToken);
                using var hello = await ReceiveGatewayPayloadAsync(socket, cancellationToken)
                    ?? throw new InvalidOperationException("Discord closed the Gateway before Hello.");
                var interval = hello.RootElement.TryGetProperty("d", out var helloData)
                    && helloData.TryGetProperty("heartbeat_interval", out var heartbeatValue)
                    && heartbeatValue.TryGetInt32(out var parsedInterval) ? parsedInterval : 45000;
                await SendGatewayPayloadAsync(socket, new
                {
                    op = 2,
                    d = new
                    {
                        token = settings.BotToken,
                        intents = GuildsIntent | GuildMessagesIntent | (settings.ReceiveChannelMessages ? MessageContentIntent : 0),
                        properties = new Dictionary<string, string>
                        {
                            ["os"] = "windows",
                            ["browser"] = "PowerShellPlus",
                            ["device"] = "PowerShellPlus"
                        }
                    }
                }, cancellationToken);
                long lastSequence = -1;
                using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = RunHeartbeatAsync(socket, interval, () => Interlocked.Read(ref lastSequence), heartbeatCancellation.Token);
                try
                {
                    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                    {
                        using var payload = await ReceiveGatewayPayloadAsync(socket, cancellationToken);
                        if (payload is null) break;
                        var root = payload.RootElement;
                        var opcode = root.TryGetProperty("op", out var opcodeValue) && opcodeValue.TryGetInt32(out var parsedOpcode)
                            ? parsedOpcode : -1;
                        if (root.TryGetProperty("s", out var sequenceValue) && sequenceValue.TryGetInt64(out var sequence))
                            Interlocked.Exchange(ref lastSequence, sequence);
                        if (opcode == 1)
                        {
                            await SendHeartbeatAsync(socket, Interlocked.Read(ref lastSequence), cancellationToken);
                            continue;
                        }
                        if (opcode == 11) continue;
                        if (opcode == 7 || opcode == 9) break;
                        if (opcode != 0 || !root.TryGetProperty("t", out var eventNameElement)
                            || !root.TryGetProperty("d", out var eventData)) continue;
                        var eventName = eventNameElement.GetString();
                        if (eventName == "READY")
                        {
                            if (eventData.TryGetProperty("user", out var user)
                                && user.TryGetProperty("id", out var userId)) botUserId = userId.GetString() ?? botUserId;
                            SetConnected(true);
                            SetStatus("Discord bot connected · messages and slash commands are live");
                            firstReady.TrySetResult(true);
                        }
                        else if (eventName == "MESSAGE_CREATE") await HandleMessageAsync(eventData, cancellationToken);
                        else if (eventName == "INTERACTION_CREATE") await HandleInteractionAsync(eventData, cancellationToken);
                    }
                }
                finally
                {
                    heartbeatCancellation.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) { } catch { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                SetConnected(false);
                SetStatus($"Discord reconnecting · {ShortError(exception)}");
                if (!firstReady.Task.IsCompleted) firstReady.TrySetException(exception);
            }
            finally
            {
                if (ReferenceEquals(activeSocket, socket)) activeSocket = null;
                socket?.Dispose();
            }
            if (!cancellationToken.IsCancellationRequested)
                try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunHeartbeatAsync(ClientWebSocket socket, int intervalMilliseconds, Func<long> sequenceProvider,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMilliseconds, 1000, 120000));
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(interval, cancellationToken);
            await SendHeartbeatAsync(socket, sequenceProvider(), cancellationToken);
        }
    }

    private Task SendHeartbeatAsync(ClientWebSocket socket, long sequence, CancellationToken cancellationToken)
        => SendGatewayPayloadAsync(socket, new { op = 1, d = sequence < 0 ? (long?)null : sequence }, cancellationToken);

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!settings.ReceiveChannelMessages || !MatchesConfiguredLocation(message)) return;
        if (!message.TryGetProperty("author", out var author)) return;
        var authorId = author.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var isBot = author.TryGetProperty("bot", out var botElement) && botElement.ValueKind == JsonValueKind.True;
        if (isBot || !IsAuthorizedUser(authorId)) return;
        var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(content)) return;
        if (!string.IsNullOrEmpty(botUserId))
        {
            content = content.Replace($"<@{botUserId}>", string.Empty, StringComparison.Ordinal)
                .Replace($"<@!{botUserId}>", string.Empty, StringComparison.Ordinal).Trim();
        }
        if (content.Length == 0) return;
        await RefreshSubscriptionsAsync();
        var session = await GetSelectedSessionAsync();
        if (session is null)
        {
            await SendChannelMessageAsync("No PowerShellPlus terminal is available. Create a terminal, then run `/terminal`.", cancellationToken);
            return;
        }
        var accepted = await RunCommandAsync(session, content, queue: false);
        await SendChannelMessageAsync(accepted
            ? $"Sent to **{EscapeMarkdown(session.Name)}**."
            : $"**{EscapeMarkdown(session.Name)}** was not ready to accept the message.", cancellationToken);
    }

    private async Task HandleInteractionAsync(JsonElement interaction, CancellationToken cancellationToken)
    {
        var interactionType = interaction.TryGetProperty("type", out var typeElement) && typeElement.TryGetInt32(out var type) ? type : 0;
        if (!interaction.TryGetProperty("id", out var idElement)
            || !interaction.TryGetProperty("token", out var tokenElement)
            || !interaction.TryGetProperty("data", out var data)) return;
        var interactionId = idElement.GetString() ?? string.Empty;
        var interactionToken = tokenElement.GetString() ?? string.Empty;
        if (interactionType == 4)
        {
            await RespondAutocompleteAsync(interactionId, interactionToken, data, cancellationToken);
            return;
        }
        if (interactionType != 2) return;
        var userId = ReadInteractionUserId(interaction);
        if (!MatchesConfiguredLocation(interaction) || !IsAuthorizedUser(userId))
        {
            await RespondInteractionAsync(interactionId, interactionToken, "This Discord account or channel is not authorized in PowerShellPlus.", cancellationToken);
            return;
        }
        await DeferInteractionAsync(interactionId, interactionToken, cancellationToken);
        string result;
        try { result = await ExecuteCommandAsync(data); }
        catch (Exception exception) { result = $"PowerShellPlus could not complete that command: {ShortError(exception)}"; }
        await EditInteractionAsync(interactionToken, result, cancellationToken);
    }

    private async Task<string> ExecuteCommandAsync(JsonElement data)
    {
        var commandName = data.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        await RefreshSubscriptionsAsync();
        var sessions = await GetSessionsAsync();
        if (commandName is "sessions" or "terminals") return FormatSessionList(sessions);
        if (commandName == "help") return "Commands: `/sessions`, `/terminal`, `/send`, `/queue`, `/status`, `/model`, `/reasoning`, `/permissions`, and `/interrupt`. Normal messages in the configured channel are sent to the selected terminal.";
        if (commandName == "terminal")
        {
            var requested = ReadStringOption(data, "name");
            var selected = ResolveSession(sessions, requested);
            if (selected is null) return "Terminal not found. Run `/sessions` and use its ID or exact name.";
            settings.SelectedTerminalId = selected.Id;
            DiscordRemoteBotStore.Save(settings);
            return $"Selected **{EscapeMarkdown(selected.Name)}** · `{selected.Id}`.";
        }
        var session = ResolveSession(sessions, settings.SelectedTerminalId) ?? sessions.FirstOrDefault();
        if (session is null) return "No terminal is currently available in PowerShellPlus.";
        if (settings.SelectedTerminalId != session.Id)
        {
            settings.SelectedTerminalId = session.Id;
            DiscordRemoteBotStore.Save(settings);
        }
        if (commandName == "status")
        {
            var profile = session.Pane.Profile;
            var location = profile.LiveWorkingDirectoryIsSsh ? $"SSH · {profile.Subtitle}" : profile.Subtitle;
            return $"**{EscapeMarkdown(session.Name)}**\nAgent: {EscapeMarkdown(profile.AgentStatusText)}\nDirectory: `{EscapeCode(location)}`\nTMUX: {(profile.IsTmuxTerminal ? "yes" : "no")}\nQueued messages: {profile.PendingCommands.Count}";
        }
        if (commandName == "interrupt")
        {
            var accepted = await dispatcher.InvokeAsync(() => session.Pane.WriteRemoteInput("\u0003"), DispatcherPriority.Input);
            return accepted ? $"Interrupted **{EscapeMarkdown(session.Name)}**." : "The terminal was not ready to receive Ctrl+C.";
        }
        if (commandName is "send" or "queue")
        {
            var message = ReadStringOption(data, "message")?.Trim() ?? string.Empty;
            if (message.Length == 0) return "A message is required.";
            var accepted = await RunCommandAsync(session, message, commandName == "queue");
            return accepted ? $"{(commandName == "queue" ? "Queued in" : "Sent to")} **{EscapeMarkdown(session.Name)}**."
                : $"**{EscapeMarkdown(session.Name)}** was not ready to accept the message.";
        }
        if (commandName == "model")
        {
            var model = ReadStringOption(data, "name")?.Trim() ?? string.Empty;
            if (!CodexSessionLocator.IsSafeCodexModel(model)) return "Enter a valid Codex model name.";
            var accepted = await RunCommandAsync(session, $"/model {model}", queue: false);
            return accepted ? $"Sent `/model {EscapeCode(model)}` to **{EscapeMarkdown(session.Name)}**." : "The terminal was not ready.";
        }
        if (commandName == "reasoning")
        {
            var effort = ReadStringOption(data, "effort")?.Trim() ?? string.Empty;
            if (effort is not ("minimal" or "low" or "medium" or "high" or "xhigh")) return "Unsupported reasoning effort.";
            var accepted = await RunCommandAsync(session, $"/reasoning {effort}", queue: false);
            return accepted ? $"Sent `/reasoning {effort}` to **{EscapeMarkdown(session.Name)}**." : "The terminal was not ready.";
        }
        if (commandName == "permissions")
        {
            if (!settings.AllowPermissionChanges) return "Remote permission changes are disabled. Enable them in PowerShellPlus Remote Access settings first.";
            var profile = ReadStringOption(data, "profile")?.Trim() ?? string.Empty;
            if (profile is not ("read-only" or "auto" or "full-access")) return "Unsupported permission profile.";
            var accepted = await RunCommandAsync(session, $"/permissions {profile}", queue: false);
            return accepted ? $"Sent `/permissions {profile}` to **{EscapeMarkdown(session.Name)}**. Codex will still enforce its configured approval and sandbox boundaries."
                : "The terminal was not ready.";
        }
        return "Unknown PowerShellPlus command. Run `/help`.";
    }

    private async Task<bool> RunCommandAsync(LanRemoteSession session, string command, bool queue)
    {
        var task = await dispatcher.InvokeAsync(() => queue
            ? session.Pane.QueueRemoteCommandAsync(command, [])
            : session.Pane.RunRemoteCommandAsync(command, null, []), DispatcherPriority.Input);
        return await task;
    }

    private async Task RespondAutocompleteAsync(string interactionId, string interactionToken, JsonElement data,
        CancellationToken cancellationToken)
    {
        var query = ReadFocusedOption(data)?.Trim() ?? string.Empty;
        var sessions = await GetSessionsAsync();
        var choices = sessions.Where(value => query.Length == 0
                || value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || value.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(25).Select(value => new { name = value.Name[..Math.Min(100, value.Name.Length)], value = value.Id }).ToArray();
        await SendInteractionCallbackAsync(interactionId, interactionToken, new { type = 8, data = new { choices } }, cancellationToken);
    }

    private Task DeferInteractionAsync(string id, string token, CancellationToken cancellationToken) =>
        SendInteractionCallbackAsync(id, token, new { type = 5, data = new { flags = 64 } }, cancellationToken);

    private Task RespondInteractionAsync(string id, string token, string content, CancellationToken cancellationToken) =>
        SendInteractionCallbackAsync(id, token, new { type = 4, data = new { content = Limit(content, 1900), flags = 64, allowed_mentions = new { parse = Array.Empty<string>() } } }, cancellationToken);

    private async Task SendInteractionCallbackAsync(string id, string token, object payload, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Post, $"interactions/{id}/{token}/callback", payload, false, cancellationToken);
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task EditInteractionAsync(string token, string content, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Patch,
            $"webhooks/{settings.ApplicationId}/{token}/messages/@original",
            new { content = Limit(content, 1900), allowed_mentions = new { parse = Array.Empty<string>() } }, false, cancellationToken);
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        var channel = output ?? throw new InvalidOperationException("Discord output queue was not created.");
        var lastSendUtc = DateTime.MinValue;
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (!channel.Reader.TryRead(out var first)) continue;
            var selectedId = settings.SelectedTerminalId;
            if (!settings.MirrorTerminalOutput || first.SessionId != selectedId) continue;
            var buffer = new StringBuilder(first.Data);
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            while (channel.Reader.TryRead(out var next))
                if (next.SessionId == selectedId && buffer.Length < 12000) buffer.Append(next.Data);
            var visible = TerminalTextSanitizer.ForTranscript(buffer.ToString()).Trim();
            if (visible.Length == 0 || !visible.Any(char.IsLetterOrDigit)) continue;
            var wait = lastSendUtc.AddMilliseconds(1100) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            var session = (await GetSessionsAsync()).FirstOrDefault(value => value.Id == selectedId);
            var heading = session is null ? "Terminal" : session.Name;
            foreach (var chunk in ChunkText(visible, 1650))
            {
                await SendChannelMessageAsync($"**{EscapeMarkdown(heading)}**\n```text\n{EscapeCodeBlock(chunk)}\n```", cancellationToken);
                lastSendUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task SendChannelMessageAsync(string content, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Post, $"channels/{settings.ChannelId}/messages",
            new { content = Limit(content, 1950), allowed_mentions = new { parse = Array.Empty<string>() } }, true, cancellationToken);
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task RefreshSubscriptionsAsync()
    {
        await subscriptionGate.WaitAsync();
        try
        {
            var sessions = await GetSessionsAsync();
            var ids = sessions.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in subscriptions.Keys.Where(value => !ids.Contains(value)).ToArray())
            {
                if (subscriptions.TryRemove(stale, out var subscription))
                    await dispatcher.InvokeAsync(() => subscription.Pane.RawOutputReceived -= subscription.Handler);
            }
            foreach (var session in sessions)
            {
                if (subscriptions.ContainsKey(session.Id)) continue;
                Action<TerminalPane, string> handler = (_, data) => output?.Writer.TryWrite(new DiscordOutputFrame(session.Id, data));
                await dispatcher.InvokeAsync(() =>
                {
                    session.Pane.EnableRemoteOutputCapture();
                    session.Pane.RawOutputReceived += handler;
                }, DispatcherPriority.Background);
                subscriptions.TryAdd(session.Id, (session.Pane, handler));
            }
            if (string.IsNullOrWhiteSpace(settings.SelectedTerminalId) || !ids.Contains(settings.SelectedTerminalId))
            {
                settings.SelectedTerminalId = sessions.FirstOrDefault()?.Id ?? string.Empty;
                DiscordRemoteBotStore.Save(settings);
            }
        }
        finally { subscriptionGate.Release(); }
    }

    private async Task ClearSubscriptionsAsync()
    {
        var snapshot = subscriptions.ToArray();
        subscriptions.Clear();
        if (dispatcher.HasShutdownStarted) return;
        await dispatcher.InvokeAsync(() =>
        {
            foreach (var item in snapshot) item.Value.Pane.RawOutputReceived -= item.Value.Handler;
        });
    }

    private async Task<IReadOnlyList<LanRemoteSession>> GetSessionsAsync()
    {
        if (dispatcher.CheckAccess()) return sessionProvider();
        return await dispatcher.InvokeAsync(sessionProvider, DispatcherPriority.Background);
    }

    private async Task<LanRemoteSession?> GetSelectedSessionAsync()
    {
        var sessions = await GetSessionsAsync();
        var selected = ResolveSession(sessions, settings.SelectedTerminalId) ?? sessions.FirstOrDefault();
        if (selected is not null && settings.SelectedTerminalId != selected.Id)
        {
            settings.SelectedTerminalId = selected.Id;
            DiscordRemoteBotStore.Save(settings);
        }
        return selected;
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string relativePath, object? payload,
        bool authenticate, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(ApiRoot), relativePath));
        if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.BotToken);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var details = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (details.Length > 500) details = details[..500];
        var statusCode = response.StatusCode;
        var reason = response.ReasonPhrase;
        response.Dispose();
        throw new InvalidOperationException($"Discord API returned {(int)statusCode} {reason}.{(details.Length == 0 ? string.Empty : " " + details)}");
    }

    private async Task SendGatewayPayloadAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await gatewaySendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { gatewaySendGate.Release(); }
    }

    private static async Task<JsonDocument?> ReceiveGatewayPayloadAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            memory.Write(buffer, 0, result.Count);
            if (memory.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Discord sent an oversized Gateway event.");
            if (result.EndOfMessage) return JsonDocument.Parse(memory.ToArray());
        }
    }

    private bool MatchesConfiguredLocation(JsonElement value)
    {
        var guildId = value.TryGetProperty("guild_id", out var guildElement) ? guildElement.GetString() : null;
        var channelId = value.TryGetProperty("channel_id", out var channelElement) ? channelElement.GetString() : null;
        return guildId == settings.GuildId && channelId == settings.ChannelId;
    }

    private bool IsAuthorizedUser(string? userId) => userId is not null && settings.AllowedUserIds.Contains(userId, StringComparer.Ordinal);

    private static string? ReadInteractionUserId(JsonElement interaction)
    {
        if (interaction.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var memberUser)
            && memberUser.TryGetProperty("id", out var memberId)) return memberId.GetString();
        return interaction.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static string? ReadStringOption(JsonElement data, string optionName)
    {
        if (!data.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return null;
        foreach (var option in options.EnumerateArray())
            if (option.TryGetProperty("name", out var name) && name.GetString() == optionName
                && option.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string? ReadFocusedOption(JsonElement data)
    {
        if (!data.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return null;
        foreach (var option in options.EnumerateArray())
            if (option.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True
                && option.TryGetProperty("value", out var value)) return value.ToString();
        return null;
    }

    private static LanRemoteSession? ResolveSession(IReadOnlyList<LanRemoteSession> sessions, string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        return sessions.FirstOrDefault(value => value.Id.Equals(idOrName, StringComparison.Ordinal))
            ?? sessions.FirstOrDefault(value => value.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatSessionList(IReadOnlyList<LanRemoteSession> sessions)
    {
        if (sessions.Count == 0) return "No terminals are currently available.";
        return "**PowerShellPlus terminals**\n" + string.Join("\n", sessions.Take(20).Select(value =>
            $"• **{EscapeMarkdown(value.Name)}** · `{value.Id}` · {EscapeMarkdown(value.Pane.Profile.AgentStatusState)}"));
    }

    private static object[] BuildCommandDefinitions()
    {
        static object StringOption(string name, string description, bool required = true, bool autocomplete = false) =>
            new { type = 3, name, description, required, autocomplete };
        static object Choice(string name, string value) => new { name, value };
        return
        [
            new { name = "help", description = "Show PowerShellPlus Discord commands", type = 1 },
            new { name = "sessions", description = "List available PowerShellPlus terminals", type = 1 },
            new { name = "terminals", description = "List available PowerShellPlus terminals", type = 1 },
            new { name = "terminal", description = "Select the terminal controlled by this Discord channel", type = 1,
                options = new[] { StringOption("name", "Terminal name or ID", autocomplete: true) } },
            new { name = "send", description = "Send a message to the selected terminal", type = 1,
                options = new[] { StringOption("message", "Message or command to send") } },
            new { name = "queue", description = "Queue a message behind the current agent turn", type = 1,
                options = new[] { StringOption("message", "Message to queue") } },
            new { name = "status", description = "Show selected terminal, directory, agent, and TMUX status", type = 1 },
            new { name = "model", description = "Change the active Codex model", type = 1,
                options = new[] { StringOption("name", "Codex model name") } },
            new { name = "reasoning", description = "Change Codex reasoning effort", type = 1,
                options = new object[] { new { type = 3, name = "effort", description = "Reasoning effort", required = true,
                    choices = new[] { Choice("Minimal", "minimal"), Choice("Low", "low"), Choice("Medium", "medium"), Choice("High", "high"), Choice("Extra high", "xhigh") } } } },
            new { name = "permissions", description = "Change Codex permissions when remotely authorized", type = 1,
                options = new object[] { new { type = 3, name = "profile", description = "Permission profile", required = true,
                    choices = new[] { Choice("Read only", "read-only"), Choice("Auto", "auto"), Choice("Full access", "full-access") } } } },
            new { name = "interrupt", description = "Send Ctrl+C to the selected terminal", type = 1 }
        ];
    }

    internal static bool ContractPassesForTest()
    {
        var valid = new DiscordRemoteBotSettings
        {
            BotToken = "MTIzNDU2Nzg5MDEyMzQ1Njc4.fake_signature_for_smoke_test",
            ApplicationId = "123456789012345678",
            GuildId = "223456789012345678",
            ChannelId = "323456789012345678",
            AllowedUserIds = ["423456789012345678"]
        };
        if (!TryValidateSettings(valid, out _)) return false;
        valid.AllowedUserIds.Clear();
        if (TryValidateSettings(valid, out _)) return false;
        var commands = JsonSerializer.Serialize(BuildCommandDefinitions());
        return commands.Contains("\"name\":\"send\"", StringComparison.Ordinal)
            && commands.Contains("\"name\":\"model\"", StringComparison.Ordinal)
            && commands.Contains("\"name\":\"reasoning\"", StringComparison.Ordinal)
            && commands.Contains("\"name\":\"permissions\"", StringComparison.Ordinal)
            && commands.Contains("\"autocomplete\":true", StringComparison.Ordinal);
    }

    private void SetConnected(bool value) => Interlocked.Exchange(ref connected, value ? 1 : 0);

    private void SetStatus(string value)
    {
        Volatile.Write(ref statusText, value);
        try
        {
            if (dispatcher.CheckAccess()) StatusChanged?.Invoke(this, EventArgs.Empty);
            else _ = dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);
        }
        catch { }
    }

    private static IEnumerable<string> ChunkText(string text, int maximum)
    {
        for (var index = 0; index < text.Length; index += maximum)
            yield return text.Substring(index, Math.Min(maximum, text.Length - index));
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string EscapeMarkdown(string value) => value.Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string EscapeCode(string value) => value.Replace("`", "'", StringComparison.Ordinal);
    private static string EscapeCodeBlock(string value) => value.Replace("```", "'''", StringComparison.Ordinal);
    private static string ShortError(Exception exception)
    {
        var message = exception.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 220 ? message : message[..220];
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        SignalShutdown();
        await StopAsync();
        lifecycleGate.Dispose();
        gatewaySendGate.Dispose();
        subscriptionGate.Dispose();
    }

    private sealed record DiscordOutputFrame(string SessionId, string Data);
}
