using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

internal sealed class PowerShellProfileHealthState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, PowerShellProfileHealthEntry> Shells { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PowerShellProfileHealthEntry
{
    public string Signature { get; set; } = string.Empty;
    public string HelperName { get; set; } = string.Empty;
    public DateTime RecordedUtc { get; set; }
}

internal static class PowerShellProfileHealthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(WorkspaceStore.DirectoryPath, "powershell-profile-health.json");

    public static bool ShouldSkip(string commandLine)
    {
        var shell = ShellName(commandLine);
        if (shell is null) return false;
        try
        {
            var state = Load();
            return state.Shells.TryGetValue(shell, out var entry)
                && entry.RecordedUtc > DateTime.UtcNow.AddDays(-30)
                && string.Equals(entry.Signature, ComputeSignature(shell), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static void RecordFailure(string commandLine, string helperName)
    {
        var shell = ShellName(commandLine);
        if (shell is null) return;
        try
        {
            var state = Load();
            state.Shells[shell] = new PowerShellProfileHealthEntry
            {
                Signature = ComputeSignature(shell),
                HelperName = helperName,
                RecordedUtc = DateTime.UtcNow
            };
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
        }
        catch { }
    }

    internal static bool VerifyPersistenceForTest()
    {
        try
        {
            RecordFailure("powershell.exe", "oh-my-posh");
            return ShouldSkip("powershell.exe");
        }
        finally { try { File.Delete(FilePath); } catch { } }
    }

    private static PowerShellProfileHealthState Load()
    {
        if (!File.Exists(FilePath)) return new PowerShellProfileHealthState();
        try
        {
            var state = JsonSerializer.Deserialize<PowerShellProfileHealthState>(File.ReadAllText(FilePath), JsonOptions);
            if (state?.Version == 1)
            {
                state.Shells ??= new Dictionary<string, PowerShellProfileHealthEntry>(StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch { }
        return new PowerShellProfileHealthState();
    }

    private static string ComputeSignature(string shell)
    {
        var builder = new StringBuilder(shell);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profileFolder = shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? "PowerShell" : "WindowsPowerShell";
        AppendFileStamp(builder, Path.Combine(documents, profileFolder, "profile.ps1"));
        AppendFileStamp(builder, Path.Combine(documents, profileFolder, "Microsoft.PowerShell_profile.ps1"));
        AppendExecutableStamp(builder, "oh-my-posh.exe");
        AppendExecutableStamp(builder, "starship.exe");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendExecutableStamp(StringBuilder builder, string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(directory.Trim().Trim('"'), executable);
                if (!File.Exists(path)) continue;
                AppendFileStamp(builder, path);
                return;
            }
            catch { }
        }
        builder.Append('|').Append(executable).Append(":missing");
    }

    private static void AppendFileStamp(StringBuilder builder, string path)
    {
        try
        {
            var file = new FileInfo(path);
            builder.Append('|').Append(path).Append(':').Append(file.Exists ? file.Length : -1).Append(':')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        }
        catch { builder.Append('|').Append(path).Append(":unavailable"); }
    }

    private static string? ShellName(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var command = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var executable = command.StartsWith('"')
            ? command[1..].Split('"', 2)[0]
            : command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? name.ToLowerInvariant()
            : null;
    }
}
