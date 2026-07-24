using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PowerShellPlus.Native;

public sealed class WindowsTerminalHandoffPlan
{
    public required string TransferId { get; init; }
    public required string SessionName { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string WindowsTerminalExecutable { get; init; }
    public required string WindowsTerminalProfile { get; init; }
    public required string PowerShellExecutable { get; init; }
    public required string DirectoryPath { get; init; }
    public required string PayloadPath { get; init; }
    public required string BootstrapPath { get; init; }
    public required string TranscriptPath { get; init; }
    public required string PendingCommandsPath { get; init; }
    public required string StartedPath { get; init; }
    public required string ReadyPath { get; init; }
    public required string ReadyStagingPath { get; init; }
    public required string CancelPath { get; init; }
    public required string CompletedPath { get; init; }
    public required IReadOnlyList<string> CodexArguments { get; init; }
    public required IReadOnlyList<string> SshArguments { get; init; }
    public string? CodexSessionId { get; init; }
    public string? CodexModel { get; init; }
    public string? PermissionDescription { get; init; }
    public string? SshDestination { get; init; }
    public string? RemoteSessionDescription { get; init; }
    public bool CodexActive => CodexSessionId is not null;
    public bool SshActive => SshArguments.Count > 0;
    internal bool SmokeTest { get; init; }
}

public sealed record WindowsTerminalHandoffLaunchResult(bool Success, string Message, int? ShellProcessId = null);
public sealed record ConsoleDescendantProcess(int ProcessId, string Name);

internal sealed class WindowsTerminalHandoffPayload
{
    public int Version { get; set; } = 1;
    public string SessionName { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string TranscriptPath { get; set; } = string.Empty;
    public string PendingCommandsPath { get; set; } = string.Empty;
    public string StartedPath { get; set; } = string.Empty;
    public string ReadyPath { get; set; } = string.Empty;
    public string CancelPath { get; set; } = string.Empty;
    public string CompletedPath { get; set; } = string.Empty;
    public string[] CodexArguments { get; set; } = [];
    public string[] SshArguments { get; set; } = [];
    public bool SmokeTest { get; set; }
}

public static class WindowsTerminalHandoff
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(12);
    private const int MaximumTranscriptCharacters = 500_000;

    public static string DirectoryPath => Path.Combine(WorkspaceStore.DirectoryPath, "session-handoffs");

    public static WindowsTerminalHandoffPlan CreatePlan(
        SessionProfile profile,
        string windowsTerminalProfile,
        string preferredPowerShellCommand,
        string transcript,
        SessionRecoveryEntry? recovery,
        bool codexActive,
        bool smokeTest = false,
        string? directoryOverride = null)
    {
        var wt = ResolveExecutable("wt.exe")
            ?? throw new InvalidOperationException("Windows Terminal (wt.exe) is not installed or its execution alias is unavailable.");
        var shell = ResolvePowerShellExecutable(profile.CommandLine)
            ?? ResolvePowerShellExecutable(preferredPowerShellCommand)
            ?? throw new InvalidOperationException("This session is not backed by Windows PowerShell or PowerShell 7, so it cannot be handed off safely.");
        var sshResume = SshRecovery.BuildResumePlan(recovery);
        var sshActive = sshResume is not null;
        var workingDirectory = codexActive && !sshActive && !string.IsNullOrWhiteSpace(recovery?.WorkingDirectory)
            ? recovery.WorkingDirectory
            : profile.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var codexArguments = codexActive && !sshActive ? BuildCodexArguments(recovery) : [];
        var permissionDescription = codexActive && !sshActive ? DescribePermissions(recovery!) : null;
        var transferId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SessionRecoveryStore.SafeSessionId(profile.Id)}-{Guid.NewGuid():N}";
        var root = directoryOverride ?? DirectoryPath;
        var directory = Path.Combine(root, transferId);
        Directory.CreateDirectory(directory);
        CleanupStaleTransfers(root, directory);

        var plan = new WindowsTerminalHandoffPlan
        {
            TransferId = transferId,
            SessionName = profile.Name,
            WorkingDirectory = workingDirectory,
            WindowsTerminalExecutable = wt,
            WindowsTerminalProfile = string.IsNullOrWhiteSpace(windowsTerminalProfile) ? "Windows PowerShell" : windowsTerminalProfile,
            PowerShellExecutable = shell,
            DirectoryPath = directory,
            PayloadPath = Path.Combine(directory, "handoff.json"),
            BootstrapPath = Path.Combine(directory, "bootstrap.ps1"),
            TranscriptPath = Path.Combine(directory, "transcript.txt"),
            PendingCommandsPath = Path.Combine(directory, "pending-commands.txt"),
            StartedPath = Path.Combine(directory, "started.json"),
            ReadyPath = Path.Combine(directory, "ready.signal"),
            ReadyStagingPath = Path.Combine(directory, "ready.pending"),
            CancelPath = Path.Combine(directory, "cancel.signal"),
            CompletedPath = Path.Combine(directory, "completed.json"),
            CodexArguments = codexArguments,
            SshArguments = sshResume?.Arguments ?? [],
            CodexSessionId = codexActive && !sshActive ? recovery!.CodexSessionId : null,
            CodexModel = codexActive && !sshActive && CodexSessionLocator.IsSafeCodexModel(recovery!.CodexModel) ? recovery.CodexModel : null,
            PermissionDescription = permissionDescription,
            SshDestination = sshResume?.Destination,
            RemoteSessionDescription = sshResume?.Description,
            SmokeTest = smokeTest
        };

        File.WriteAllText(plan.TranscriptPath, SanitizeTranscript(transcript), new UTF8Encoding(false));
        File.WriteAllLines(plan.PendingCommandsPath, profile.PendingCommands.Select((value, index) => $"{index + 1}. {value}"), new UTF8Encoding(false));
        File.WriteAllText(plan.BootstrapPath, BootstrapScript, new UTF8Encoding(false));
        var payload = new WindowsTerminalHandoffPayload
        {
            SessionName = plan.SessionName,
            WorkingDirectory = plan.WorkingDirectory,
            TranscriptPath = plan.TranscriptPath,
            PendingCommandsPath = plan.PendingCommandsPath,
            StartedPath = plan.StartedPath,
            ReadyPath = plan.ReadyPath,
            CancelPath = plan.CancelPath,
            CompletedPath = plan.CompletedPath,
            CodexArguments = plan.CodexArguments.ToArray(),
            SshArguments = plan.SshArguments.ToArray(),
            SmokeTest = smokeTest
        };
        File.WriteAllText(plan.PayloadPath, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
        return plan;
    }

    public static async Task<WindowsTerminalHandoffLaunchResult> LaunchAndWaitForStartAsync(
        WindowsTerminalHandoffPlan plan,
        bool useWindowsTerminal = true,
        TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(plan, useWindowsTerminal));
            if (process is null) return new WindowsTerminalHandoffLaunchResult(false, "Windows did not start the external terminal process.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new WindowsTerminalHandoffLaunchResult(false, $"Windows Terminal could not be started: {exception.Message}");
        }

        var deadline = DateTime.UtcNow + (timeout ?? DefaultLaunchTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (TryReadStartedProcess(plan.StartedPath, out var processId))
            {
                try
                {
                    using var shell = Process.GetProcessById(processId);
                    if (!shell.HasExited && IsPowerShellName(shell.ProcessName))
                        return new WindowsTerminalHandoffLaunchResult(true, "The external PowerShell process verified its startup.", processId);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            await Task.Delay(75);
        }
        Cancel(plan, "The external PowerShell process did not complete its startup handshake.");
        return new WindowsTerminalHandoffLaunchResult(false, "Windows Terminal opened, but its PowerShell process did not verify startup. The PowerShellPlus session was left untouched.");
    }

    public static bool PrepareReleaseSignal(WindowsTerminalHandoffPlan plan)
    {
        try
        {
            File.WriteAllText(plan.ReadyStagingPath, DateTime.UtcNow.ToString("O"), new UTF8Encoding(false));
            return File.Exists(plan.ReadyStagingPath);
        }
        catch { return false; }
    }

    public static bool CommitReleaseSignal(WindowsTerminalHandoffPlan plan)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Move(plan.ReadyStagingPath, plan.ReadyPath, true);
                return File.Exists(plan.ReadyPath);
            }
            catch when (attempt < 2) { Thread.Sleep(40); }
            catch { break; }
        }
        try
        {
            File.WriteAllText(plan.ReadyPath, DateTime.UtcNow.ToString("O"), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    public static void Cancel(WindowsTerminalHandoffPlan plan, string message)
    {
        try { File.WriteAllText(plan.CancelPath, message, new UTF8Encoding(false)); } catch { }
        try { if (File.Exists(plan.ReadyStagingPath)) File.Delete(plan.ReadyStagingPath); } catch { }
    }

    public static void Discard(WindowsTerminalHandoffPlan plan)
    {
        try { Directory.Delete(plan.DirectoryPath, true); } catch { }
    }

    public static async Task<bool> WaitForCompletionAsync(WindowsTerminalHandoffPlan plan, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(plan.CompletedPath)) return true;
            await Task.Delay(60);
        }
        return false;
    }

    public static IReadOnlyList<string> BuildWindowsTerminalArgumentsForTest(WindowsTerminalHandoffPlan plan) =>
        CreateStartInfo(plan, true).ArgumentList.ToArray();

    public static bool MatchesCodexState(WindowsTerminalHandoffPlan plan, SessionRecoveryEntry recovery)
    {
        try { return plan.CodexActive && plan.CodexArguments.SequenceEqual(BuildCodexArguments(recovery)); }
        catch (InvalidOperationException) { return false; }
    }

    public static bool MatchesSshState(WindowsTerminalHandoffPlan plan, SessionRecoveryEntry recovery)
    {
        var current = SshRecovery.BuildResumePlan(recovery);
        return plan.SshActive && current is not null && plan.SshArguments.SequenceEqual(current.Arguments, StringComparer.Ordinal);
    }

    private static ProcessStartInfo CreateStartInfo(WindowsTerminalHandoffPlan plan, bool useWindowsTerminal)
    {
        var start = new ProcessStartInfo
        {
            FileName = useWindowsTerminal ? plan.WindowsTerminalExecutable : plan.PowerShellExecutable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = !useWindowsTerminal
        };
        if (useWindowsTerminal)
        {
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add("new");
            start.ArgumentList.Add("new-tab");
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(plan.WindowsTerminalProfile);
            start.ArgumentList.Add("--title");
            start.ArgumentList.Add(plan.SessionName);
            start.ArgumentList.Add("--suppressApplicationTitle");
            start.ArgumentList.Add(plan.PowerShellExecutable);
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoExit");
        }
        else start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(plan.BootstrapPath);
        start.ArgumentList.Add("-PayloadPath");
        start.ArgumentList.Add(plan.PayloadPath);
        return start;
    }

    private static IReadOnlyList<string> BuildCodexArguments(SessionRecoveryEntry? recovery)
    {
        if (recovery?.CodexWasActive != true || !CodexSessionLocator.IsSafeCodexId(recovery.CodexSessionId))
            throw new InvalidOperationException("The active Codex thread could not be identified exactly. The source session was left running.");
        if (!CodexSessionLocator.IsSafeCodexPermissionState(recovery.CodexPermissionProfile, recovery.CodexSandboxMode, recovery.CodexApprovalPolicy, recovery.CodexApprovalsReviewer))
            throw new InvalidOperationException("The active Codex permission level could not be verified. The source session was left running.");

        var arguments = new List<string> { "resume", recovery.CodexSessionId! };
        if (CodexSessionLocator.IsSafeCodexModel(recovery.CodexModel))
        {
            arguments.Add("--model");
            arguments.Add(recovery.CodexModel!);
        }
        if (CodexSessionLocator.IsSafeCodexPermissionProfile(recovery.CodexPermissionProfile))
        {
            arguments.Add("--config");
            arguments.Add($"default_permissions=\"{recovery.CodexPermissionProfile}\"");
        }
        else
        {
            arguments.Add("--sandbox");
            arguments.Add(recovery.CodexSandboxMode!);
        }
        if (CodexSessionLocator.IsSafeCodexApprovalsReviewer(recovery.CodexApprovalsReviewer))
        {
            arguments.Add("--config");
            arguments.Add($"approvals_reviewer=\"{recovery.CodexApprovalsReviewer}\"");
        }
        arguments.Add("--ask-for-approval");
        arguments.Add(recovery.CodexApprovalPolicy!);
        return arguments;
    }

    private static string DescribePermissions(SessionRecoveryEntry recovery)
    {
        var mode = CodexSessionLocator.IsSafeCodexPermissionProfile(recovery.CodexPermissionProfile)
            ? recovery.CodexPermissionProfile
            : recovery.CodexSandboxMode;
        var reviewer = CodexSessionLocator.IsSafeCodexApprovalsReviewer(recovery.CodexApprovalsReviewer)
            ? $", reviewer {recovery.CodexApprovalsReviewer}"
            : string.Empty;
        return $"{mode}, approvals {recovery.CodexApprovalPolicy}{reviewer}";
    }

    private static string? ResolvePowerShellExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string executable;
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            executable = end > 1 ? expanded[1..end] : string.Empty;
        }
        else executable = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!IsPowerShellName(Path.GetFileNameWithoutExtension(executable))) return null;
        return ResolveExecutable(executable);
    }

    private static string? ResolveExecutable(string executable)
    {
        executable = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (Path.IsPathRooted(executable)) return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        var candidates = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("powershell", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(directory.Trim().Trim('"'), executable)); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsPowerShellName(string value) =>
        value.Equals("powershell", StringComparison.OrdinalIgnoreCase) || value.Equals("pwsh", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadStartedProcess(string path, out int processId)
    {
        processId = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ProcessId", out var value) && value.TryGetInt32(out processId) && processId > 0;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string SanitizeTranscript(string value)
    {
        if (value.Length > MaximumTranscriptCharacters) value = value[^MaximumTranscriptCharacters..];
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character)) result.Append(character);
        return result.ToString();
    }

    private static void CleanupStaleTransfers(string root, string currentDirectory)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-30)) Directory.Delete(directory, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private const string BootstrapScript = """
param([Parameter(Mandatory = $true)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
Set-Location -LiteralPath $payload.WorkingDirectory
$started = [ordered]@{ ProcessId = $PID; StartedUtc = [DateTime]::UtcNow.ToString('O'); SessionName = $payload.SessionName }
$started | ConvertTo-Json -Compress | Set-Content -LiteralPath $payload.StartedPath -Encoding UTF8
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (-not (Test-Path -LiteralPath $payload.ReadyPath) -and -not (Test-Path -LiteralPath $payload.CancelPath) -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 50
}
if (Test-Path -LiteralPath $payload.CancelPath) {
    Write-Warning (Get-Content -LiteralPath $payload.CancelPath -Raw)
    exit 2
}
if (-not (Test-Path -LiteralPath $payload.ReadyPath)) {
    Write-Warning 'PowerShellPlus did not release the session. The handoff was canceled.'
    exit 3
}
try {
    $codexArguments = @($payload.CodexArguments | ForEach-Object { [string]$_ })
    $sshArguments = @($payload.SshArguments | ForEach-Object { [string]$_ })
    $observedArguments = @()
    if ($payload.SmokeTest) {
        $forwardedArguments = if ($sshArguments.Count -gt 0) { $sshArguments } else { $codexArguments }
        $observedArguments = @(& { @($args | ForEach-Object { [string]$_ }) } @forwardedArguments)
        Write-Output 'POWERSHELLPLUS_HANDOFF_SMOKE_READY'
    } elseif ($sshArguments.Count -gt 0) {
        Write-Host ('PowerShellPlus reconnecting ' + $payload.SessionName + ' through SSH.') -ForegroundColor Cyan
        & ssh @sshArguments
    } elseif ($codexArguments.Count -gt 0) {
        Write-Host ('PowerShellPlus resumed session "' + $payload.SessionName + '". Transcript: ' + $payload.TranscriptPath) -ForegroundColor DarkGray
        & codex @codexArguments
    } else {
        Write-Host ('PowerShellPlus moved session "' + $payload.SessionName + '" into this terminal.') -ForegroundColor Green
        Write-Host ('Saved transcript: ' + $payload.TranscriptPath) -ForegroundColor DarkGray
        if ((Get-Item -LiteralPath $payload.PendingCommandsPath).Length -gt 0) {
            Write-Host ('Saved queued commands: ' + $payload.PendingCommandsPath) -ForegroundColor DarkGray
        }
    }
} finally {
    $completed = [ordered]@{ ProcessId = $PID; CompletedUtc = [DateTime]::UtcNow.ToString('O'); ObservedArguments = $observedArguments }
    $completed | ConvertTo-Json -Compress | Set-Content -LiteralPath $payload.CompletedPath -Encoding UTF8
}
""";
}
