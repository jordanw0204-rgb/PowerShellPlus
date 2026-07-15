using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed class LanRemotePairedDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string LastAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;

    public LanRemotePairedDevice Clone() => new()
    {
        Id = Id,
        Name = Name,
        SecretHash = SecretHash,
        CreatedUtc = CreatedUtc,
        LastSeenUtc = LastSeenUtc,
        ExpiresUtc = ExpiresUtc,
        LastAddress = LastAddress,
        UserAgent = UserAgent
    };
}

internal sealed record LanRemotePairedDeviceView(
    string Id,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset ExpiresUtc,
    string LastAddress,
    bool IsConnected)
{
    public string Status => IsConnected ? "Connected now" : $"Last seen {LastSeenUtc.ToLocalTime():g}";
    public string Details => string.IsNullOrWhiteSpace(LastAddress) ? Status : $"{Status} · {LastAddress}";
}

internal static class LanRemotePairingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string FilePath => Path.Combine(WorkspaceStore.DirectoryPath, "lan-remote-devices.json");

    public static IReadOnlyList<LanRemotePairedDevice> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath), JsonOptions);
            if (snapshot is not { Version: 1 } || snapshot.Devices is null) return [];
            return snapshot.Devices.Where(IsValid).Select(value => value.Clone()).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<LanRemotePairedDevice> devices)
    {
        Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
        var snapshot = new Snapshot { Devices = devices.Where(IsValid).Select(value => value.Clone()).ToList() };
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, FilePath, true);
    }

    public static string HashSecret(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool SecretMatches(string expectedHash, string secret)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValid(LanRemotePairedDevice value)
        => Guid.TryParseExact(value.Id, "N", out _)
            && !string.IsNullOrWhiteSpace(value.Name)
            && value.Name.Length <= 60
            && value.SecretHash.Length == 64
            && value.CreatedUtc > DateTimeOffset.UnixEpoch
            && value.ExpiresUtc > value.CreatedUtc;

    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<LanRemotePairedDevice> Devices { get; set; } = [];
    }
}
