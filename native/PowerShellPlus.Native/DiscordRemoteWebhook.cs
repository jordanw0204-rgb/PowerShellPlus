using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed class DiscordRemoteWebhookSettings
{
    public string WebhookUrl { get; set; } = string.Empty;
    public bool NotifyWhenSharingStarts { get; set; }
    public bool IncludePairingCode { get; set; }
}

internal static class DiscordRemoteWebhookStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PowerShellPlus.DiscordRemoteWebhook.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string FilePath => Path.Combine(WorkspaceStore.DirectoryPath, "discord-remote-webhook.json");

    internal static DiscordRemoteWebhookSettings Load(string? filePath = null)
    {
        try
        {
            var path = filePath ?? FilePath;
            if (!File.Exists(path)) return new DiscordRemoteWebhookSettings();
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot is not { Version: 1 } || string.IsNullOrWhiteSpace(snapshot.ProtectedWebhookUrl))
                return new DiscordRemoteWebhookSettings
                {
                    NotifyWhenSharingStarts = snapshot?.NotifyWhenSharingStarts == true,
                    IncludePairingCode = snapshot?.IncludePairingCode == true
                };
            var protectedBytes = Convert.FromBase64String(snapshot.ProtectedWebhookUrl);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return new DiscordRemoteWebhookSettings
            {
                WebhookUrl = Encoding.UTF8.GetString(clearBytes),
                NotifyWhenSharingStarts = snapshot.NotifyWhenSharingStarts,
                IncludePairingCode = snapshot.IncludePairingCode
            };
        }
        catch
        {
            return new DiscordRemoteWebhookSettings();
        }
    }

    internal static void Save(DiscordRemoteWebhookSettings settings, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = filePath ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedUrl = string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            var clearBytes = Encoding.UTF8.GetBytes(settings.WebhookUrl.Trim());
            protectedUrl = Convert.ToBase64String(ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(clearBytes);
        }
        var snapshot = new Snapshot
        {
            ProtectedWebhookUrl = protectedUrl,
            NotifyWhenSharingStarts = settings.NotifyWhenSharingStarts,
            IncludePairingCode = settings.IncludePairingCode
        };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, path, true);
    }

    internal static bool EncryptionContractPassesForTest(string directory)
    {
        var path = Path.Combine(directory, "discord-webhook-smoke.json");
        const string secret = "https://discord.com/api/webhooks/123456789012345678/test_token-value";
        try
        {
            Save(new DiscordRemoteWebhookSettings
            {
                WebhookUrl = secret,
                NotifyWhenSharingStarts = true,
                IncludePairingCode = false
            }, path);
            var serialized = File.ReadAllText(path);
            var restored = Load(path);
            return !serialized.Contains(secret, StringComparison.Ordinal)
                && restored.WebhookUrl == secret
                && restored.NotifyWhenSharingStarts
                && !restored.IncludePairingCode;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public string ProtectedWebhookUrl { get; set; } = string.Empty;
        public bool NotifyWhenSharingStarts { get; set; }
        public bool IncludePairingCode { get; set; }
    }
}

internal sealed class DiscordRemoteWebhookClient
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HttpClient client;

    internal DiscordRemoteWebhookClient(HttpClient? client = null) => this.client = client ?? SharedClient;

    internal static bool TryValidateWebhookUrl(string? value, out Uri webhookUri, out string error)
    {
        webhookUri = null!;
        error = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate))
        {
            error = "Paste a complete Discord webhook URL.";
            return false;
        }
        if (candidate.Scheme != Uri.UriSchemeHttps || candidate.Port != 443
            || !candidate.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment))
        {
            error = "Only HTTPS webhook URLs hosted on discord.com are accepted.";
            return false;
        }
        var segments = candidate.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var webhookIndex = Array.FindIndex(segments, value => value.Equals("webhooks", StringComparison.OrdinalIgnoreCase));
        if (webhookIndex < 1 || webhookIndex + 3 != segments.Length
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(segments[webhookIndex + 1], out _)
            || string.IsNullOrWhiteSpace(segments[webhookIndex + 2]))
        {
            error = "This is not a valid Discord incoming webhook URL.";
            return false;
        }
        webhookUri = candidate;
        return true;
    }

    internal async Task SendTestAsync(Uri webhookUri, CancellationToken cancellationToken = default)
    {
        var payload = CreatePayload("PowerShellPlus connected", "Discord notifications are ready. Remote Access is not currently being shared by this test.",
            0x89B4FA, []);
        await ExecuteAsync(webhookUri, payload, cancellationToken);
    }

    internal async Task SendSharingStartedAsync(Uri webhookUri, RemoteAccessMode mode, string address,
        string pairingCode, bool includePairingCode, CancellationToken cancellationToken = default)
    {
        var fields = new List<object>
        {
            new { name = "Mode", value = mode == RemoteAccessMode.Global ? "Global HTTPS" : "LAN", inline = true },
            new { name = "Address", value = address, inline = false }
        };
        if (includePairingCode)
            fields.Add(new { name = "Pairing code", value = $"`{pairingCode}`", inline = true });
        var description = includePairingCode
            ? "Open the address in a browser and enter the pairing code. Only share this message in a private channel."
            : "Open the address in a browser. Use the pairing code shown inside PowerShellPlus.";
        var payload = CreatePayload("PowerShellPlus Remote is ready", description,
            mode == RemoteAccessMode.Global ? 0x94E2D5 : 0x89B4FA, fields);
        await ExecuteAsync(webhookUri, payload, cancellationToken);
    }

    private async Task ExecuteAsync(Uri webhookUri, object payload, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(webhookUri);
        var existingQuery = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(existingQuery) ? "wait=true" : $"{existingQuery}&wait=true";
        using var response = await client.PostAsJsonAsync(builder.Uri, payload, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var details = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (details.Length > 240) details = details[..240];
        throw new InvalidOperationException($"Discord rejected the webhook ({(int)response.StatusCode} {response.ReasonPhrase}).{(details.Length == 0 ? string.Empty : $" {details}")}");
    }

    private static object CreatePayload(string title, string description, int color, IReadOnlyList<object> fields) => new
    {
        username = "PowerShellPlus Remote",
        allowed_mentions = new { parse = Array.Empty<string>() },
        embeds = new[]
        {
            new
            {
                title,
                description,
                color,
                fields,
                footer = new { text = "PowerShellPlus · Remote Access" },
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            }
        }
    };

    internal static bool ContractPassesForTest()
    {
        return TryValidateWebhookUrl("https://discord.com/api/webhooks/123456789012345678/token_value", out _, out _)
            && TryValidateWebhookUrl("https://discord.com/api/v10/webhooks/123456789012345678/token-value", out _, out _)
            && !TryValidateWebhookUrl("http://discord.com/api/webhooks/123/token", out _, out _)
            && !TryValidateWebhookUrl("https://discord.com.evil.example/api/webhooks/123/token", out _, out _)
            && !TryValidateWebhookUrl("https://example.com/api/webhooks/123/token", out _, out _);
    }

    internal static async Task<bool> DeliveryContractPassesForTestAsync()
    {
        var handler = new RecordingHandler();
        using var testClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var client = new DiscordRemoteWebhookClient(testClient);
        await client.SendSharingStartedAsync(
            new Uri("https://discord.com/api/webhooks/123456789012345678/token_value"),
            RemoteAccessMode.Global, "https://example.ts.net", "123456789012", includePairingCode: true);
        return handler.RequestUri is { Scheme: "https", Host: "discord.com" }
            && handler.RequestUri.Query.Contains("wait=true", StringComparison.Ordinal)
            && handler.Body.Contains("PowerShellPlus Remote is ready", StringComparison.Ordinal)
            && handler.Body.Contains("123456789012", StringComparison.Ordinal)
            && handler.Body.Contains("allowed_mentions", StringComparison.Ordinal)
            && handler.Body.Contains("\"parse\":[]", StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        internal string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }
}
