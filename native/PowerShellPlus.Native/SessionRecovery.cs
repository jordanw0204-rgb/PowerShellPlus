using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PowerShellPlus.Native;

public sealed class SessionRecoverySnapshot
{
    public int Version { get; set; } = 4;
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, SessionRecoveryEntry> Sessions { get; set; } = [];
}

public sealed class SessionRecoveryEntry
{
    public string SessionId { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? TranscriptFile { get; set; }
    public bool CodexWasActive { get; set; }
    public string? CodexSessionId { get; set; }
    public string? CodexModel { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
}

public readonly record struct CodexProcessState(bool IsActive, DateTime? StartedUtc);
public sealed record CodexSessionMatch(string SessionId, string WorkingDirectory, DateTime MetadataUtc, TimeSpan ProcessStartDistance, DateTime FileModifiedUtc, string? Model = null);
public sealed record CodexSessionModel(string Model, DateTime UpdatedUtc);

public sealed class CodexLaunchMarker
{
    public int Version { get; set; } = 1;
    public string PaneId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? ExplicitSessionId { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public DateTime? EndedUtc { get; set; }
    public bool IsActive => EndedUtc is null && StartedUtc > DateTime.UnixEpoch;
}

public static class CodexLaunchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? DirectoryOverride { get; set; }
    public static string DirectoryPath => DirectoryOverride ?? Path.Combine(SessionRecoveryStore.DirectoryPath, "codex-launches");

    public static CodexLaunchMarker? Load(string paneId, string? directoryPath = null)
    {
        try
        {
            var path = MarkerPath(paneId, directoryPath);
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<CodexLaunchMarker>(File.ReadAllText(path), JsonOptions);
            return marker is { Version: 1 } && marker.PaneId == paneId ? marker : null;
        }
        catch { return null; }
    }

    public static void Save(CodexLaunchMarker marker, string? directoryPath = null)
    {
        var directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var path = MarkerPath(marker.PaneId, directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static void Confirm(CodexLaunchMarker marker, CodexSessionMatch match, string? directoryPath = null)
    {
        marker.SessionId = match.SessionId;
        marker.WorkingDirectory = match.WorkingDirectory;
        marker.Model = match.Model ?? marker.Model;
        Save(marker, directoryPath);
    }

    public static string BuildPowerShellWrapper(string paneId, string? directoryPath = null)
    {
        var directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var markerPath = MarkerPath(paneId, directory);
        var escapedPaneId = EscapePowerShell(paneId);
        var escapedMarkerPath = EscapePowerShell(markerPath);
        return "$global:__PowerShellPlusCodexCommand = (Get-Command codex -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1).Source; "
            + "if ($global:__PowerShellPlusCodexCommand) { function global:codex { "
            + "$__pspArgs = @($args); $__pspStarted = [DateTime]::UtcNow; $__pspCwd = (Get-Location).ProviderPath; $__pspExplicit = $null; $__pspModel = $null; "
            + "if ($__pspArgs.Count -ge 2 -and [string]$__pspArgs[0] -eq 'resume' -and [string]$__pspArgs[1] -match '^[A-Za-z0-9_-]{8,128}$') { $__pspExplicit = [string]$__pspArgs[1] }; "
            + "for ($__pspIndex = 0; $__pspIndex -lt $__pspArgs.Count - 1; $__pspIndex++) { if ([string]$__pspArgs[$__pspIndex] -in @('-m', '--model')) { $__pspModel = [string]$__pspArgs[$__pspIndex + 1] } }; "
            + "$__pspMarker = [ordered]@{ Version = 1; PaneId = '" + escapedPaneId + "'; StartedUtc = $__pspStarted.ToString('O'); WorkingDirectory = $__pspCwd; ExplicitSessionId = $__pspExplicit; SessionId = $__pspExplicit; Model = $__pspModel; EndedUtc = $null }; "
            + "$__pspMarker | ConvertTo-Json -Compress | Set-Content -LiteralPath '" + escapedMarkerPath + "' -Encoding UTF8; "
            + "try { & $global:__PowerShellPlusCodexCommand @__pspArgs } finally { $__pspMarker.EndedUtc = [DateTime]::UtcNow.ToString('O'); $__pspMarker | ConvertTo-Json -Compress | Set-Content -LiteralPath '" + escapedMarkerPath + "' -Encoding UTF8 } "
            + "} }";
    }

    private static string MarkerPath(string paneId, string? directoryPath = null)
        => Path.Combine(directoryPath ?? DirectoryPath, SessionRecoveryStore.SafeSessionId(paneId) + ".json");

    private static string EscapePowerShell(string value) => value.Replace("'", "''");
}

public static class SessionRecoveryStore
{
    private const int MaximumTranscriptCharacters = 500_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DirectoryPath => Path.Combine(WorkspaceStore.DirectoryPath, "session-recovery");
    public static string SnapshotPath => Path.Combine(DirectoryPath, "recovery.json");

    public static SessionRecoverySnapshot Load(string? directoryPath = null)
    {
        var snapshotPath = Path.Combine(directoryPath ?? DirectoryPath, "recovery.json");
        try
        {
            if (!File.Exists(snapshotPath)) return new SessionRecoverySnapshot();
            var value = JsonSerializer.Deserialize<SessionRecoverySnapshot>(File.ReadAllText(snapshotPath), JsonOptions);
            if (value is not null && value.Version is >= 1 and <= 4)
            {
                value.Sessions ??= [];
                if (value.Version == 1)
                {
                    // Version 1 matched Codex by the pane's configured startup
                    // directory, which may differ from the directory where the
                    // user actually launched Codex. Never trust that old ID.
                    foreach (var entry in value.Sessions.Values) entry.CodexSessionId = null;
                    value.Version = 4;
                }
                if (value.Version is 2 or 3) value.Version = 4;
                return value;
            }
        }
        catch { }
        return new SessionRecoverySnapshot();
    }

    public static void Save(SessionRecoverySnapshot snapshot, string? directoryPath = null)
    {
        var directory = directoryPath ?? DirectoryPath;
        var snapshotPath = Path.Combine(directory, "recovery.json");
        Directory.CreateDirectory(directory);
        snapshot.CapturedUtc = DateTime.UtcNow;
        var temporary = snapshotPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, snapshotPath, true);
    }

    public static string? SaveTranscript(string sessionId, string output, string? directoryPath = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var safeId = SafeSessionId(sessionId);
        var fileName = safeId + ".txt";
        var value = output.Length <= MaximumTranscriptCharacters ? output : output[^MaximumTranscriptCharacters..];
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, true);
        return fileName;
    }

    public static string ReadTranscript(SessionRecoveryEntry? entry, string? directoryPath = null)
    {
        try
        {
            if (entry?.TranscriptFile is not { Length: > 0 } fileName || Path.GetFileName(fileName) != fileName) return string.Empty;
            var path = Path.Combine(directoryPath ?? DirectoryPath, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch { return string.Empty; }
    }

    public static void DeleteSession(string sessionId)
    {
        try
        {
            var path = Path.Combine(DirectoryPath, SafeSessionId(sessionId) + ".txt");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static void DeleteAllTranscripts()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.txt", SearchOption.TopDirectoryOnly)) File.Delete(path);
        }
        catch { }
    }

    internal static string SafeSessionId(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return safe.Length == 0 ? "session" : safe[..Math.Min(100, safe.Length)];
    }
}

public static class CodexSessionLocator
{
    private const long ModelScanOverlapBytes = 64 * 1024;
    private static readonly object ModelCacheLock = new();
    private static readonly Dictionary<string, ModelFileCursor> ModelFileCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindSessionId(string workingDirectory, DateTime? processStartedUtc, string? sessionsRoot = null)
        => FindBestSession(processStartedUtc, workingDirectory, null, sessionsRoot)?.SessionId;

    public static CodexSessionMatch? FindBestSession(DateTime? processStartedUtc, string? preferredWorkingDirectory = null, ISet<string>? excludedSessionIds = null, string? sessionsRoot = null)
    {
        if (processStartedUtc is null) return null;
        var root = sessionsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return null;
        try
        {
            var expectedDirectory = string.IsNullOrWhiteSpace(preferredWorkingDirectory) ? null : NormalizePath(preferredWorkingDirectory);
            var candidates = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(500);
            CodexSessionMatch? best = null;
            foreach (var file in candidates)
            {
                try
                {
                    using var reader = new StreamReader(file.FullName);
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var rootElement = document.RootElement;
                    if (!rootElement.TryGetProperty("type", out var type) || type.GetString() != "session_meta" || !rootElement.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("cwd", out var cwd)) continue;
                    var actualDirectory = cwd.GetString() ?? string.Empty;
                    if (expectedDirectory is not null && NormalizePath(actualDirectory) != expectedDirectory) continue;
                    if (!payload.TryGetProperty("timestamp", out var timestampValue) || !DateTime.TryParse(timestampValue.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp)) continue;
                    var id = payload.TryGetProperty("session_id", out var sessionId) ? sessionId.GetString() : null;
                    id ??= payload.TryGetProperty("id", out var rolloutId) ? rolloutId.GetString() : null;
                    if (!IsSafeCodexId(id) || excludedSessionIds?.Contains(id!) == true) continue;
                    var distance = (timestamp.ToUniversalTime() - processStartedUtc.Value).Duration();
                    if (distance > TimeSpan.FromMinutes(30)) continue;
                    var match = new CodexSessionMatch(id!, actualDirectory, timestamp.ToUniversalTime(), distance, file.LastWriteTimeUtc);
                    if (best is null || distance < best.ProcessStartDistance || distance == best.ProcessStartDistance && file.LastWriteTimeUtc > best.FileModifiedUtc)
                        best = match;
                }
                catch { }
            }
            if (best is null) return null;
            var model = FindLatestModel(best.SessionId, root);
            return best with { Model = model?.Model };
        }
        catch { return null; }
    }

    public static bool IsSafeCodexId(string? value) => value is { Length: >= 8 and <= 128 } && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    public static bool IsSafeCodexModel(string? value) => value is { Length: >= 1 and <= 128 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':');

    public static CodexSessionModel? FindLatestModel(string? sessionId, string? sessionsRoot = null)
    {
        if (!IsSafeCodexId(sessionId)) return null;
        var root = sessionsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return null;
        try
        {
            var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(500)
                .ToList();
            lock (ModelCacheLock)
            {
                CodexSessionModel? latest = null;
                foreach (var file in files)
                {
                    try
                    {
                        ModelFileCache.TryGetValue(file.FullName, out var cursor);
                        if (cursor is null || !string.Equals(cursor.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            using var metadataReader = new StreamReader(file.FullName);
                            var firstLine = metadataReader.ReadLine();
                            if (string.IsNullOrWhiteSpace(firstLine)) continue;
                            using var metadata = JsonDocument.Parse(firstLine);
                            if (!metadata.RootElement.TryGetProperty("payload", out var payload)) continue;
                            var id = payload.TryGetProperty("session_id", out var sessionIdValue) ? sessionIdValue.GetString() : null;
                            if (!string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                            cursor = new ModelFileCursor(sessionId!);
                            ModelFileCache[file.FullName] = cursor;
                        }

                        file.Refresh();
                        if (file.Length != cursor.Length)
                        {
                            var scanStart = file.Length < cursor.Length ? 0 : Math.Max(0, cursor.Length - ModelScanOverlapBytes);
                            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            stream.Seek(scanStart, SeekOrigin.Begin);
                            using var reader = new StreamReader(stream);
                            if (scanStart > 0) _ = reader.ReadLine();
                            var fileLatest = cursor.Latest;
                            string? line;
                            while ((line = reader.ReadLine()) is not null) ConsiderModelRecord(line, ref fileLatest);
                            cursor.Latest = fileLatest;
                            cursor.Length = stream.Length;
                        }
                        if (cursor.Latest is not null && (latest is null || cursor.Latest.UpdatedUtc >= latest.UpdatedUtc)) latest = cursor.Latest;
                    }
                    catch { }
                }
                return latest;
            }
        }
        catch { return null; }
    }

    private static void ConsiderModelRecord(string line, ref CodexSessionModel? latest)
    {
        if (!line.Contains("model", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return;
            string? model = null;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "turn_context")
                model = payload.TryGetProperty("model", out var turnModel) ? turnModel.GetString() : null;
            else if (payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "thread_settings_applied"
                && payload.TryGetProperty("thread_settings", out var settings))
                model = settings.TryGetProperty("model", out var settingsModel) ? settingsModel.GetString() : null;
            if (!IsSafeCodexModel(model)) return;
            var updated = root.TryGetProperty("timestamp", out var timestampValue)
                && DateTime.TryParse(timestampValue.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp)
                    ? timestamp.ToUniversalTime()
                    : DateTime.MinValue;
            if (latest is null || updated >= latest.UpdatedUtc) latest = new CodexSessionModel(model!, updated);
        }
        catch { }
    }

    private sealed class ModelFileCursor(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public long Length { get; set; }
        public CodexSessionModel? Latest { get; set; }
    }

    private static string NormalizePath(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return value.Trim().TrimEnd('\\', '/').ToUpperInvariant(); }
    }
}

public static class ProcessTreeInspector
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static CodexProcessState FindCodexProcess(int rootProcessId)
    {
        if (rootProcessId <= 0) return default;
        var processes = SnapshotProcesses();
        var descendants = new HashSet<int> { rootProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var process in processes)
                if (descendants.Contains(process.ParentId) && descendants.Add(process.Id)) changed = true;
        }

        DateTime? earliest = null;
        foreach (var process in processes.Where(process => descendants.Contains(process.Id) && IsCodexExecutable(process.Name)))
        {
            try
            {
                using var running = Process.GetProcessById(process.Id);
                var started = running.StartTime.ToUniversalTime();
                if (earliest is null || started < earliest) earliest = started;
            }
            catch { earliest ??= DateTime.UtcNow; }
        }
        return new CodexProcessState(earliest is not null, earliest);
    }

    private static bool IsCodexExecutable(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value);
        return name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("codex-cli", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ProcessEntry> SnapshotProcesses()
    {
        var result = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile ?? string.Empty));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private readonly record struct ProcessEntry(int Id, int ParentId, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
