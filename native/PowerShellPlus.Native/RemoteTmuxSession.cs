using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerShellPlus.Native;

public readonly record struct RemoteTmuxStatus(bool CommandSucceeded, bool TmuxAvailable, bool SessionExists, string Message);

/// <summary>
/// Owns the small, deterministic tmux contract used by SSH terminals.  The
/// tmux server lives on the remote machine; PowerShellPlus only attaches and
/// detaches clients, so closing the local ConPTY cannot kill the remote agent.
/// </summary>
public static class RemoteTmuxSession
{
    private const string AvailableMarker = "PSP_TMUX_AVAILABLE";
    private const string ExistsMarker = "PSP_TMUX_EXISTS";
    private const string ReadyMarker = "PSP_TMUX_READY";
    private const string MissingMarker = "PSP_TMUX_UNAVAILABLE";

    public static string GetSessionName(string paneId)
    {
        var safe = SessionRecoveryStore.SafeSessionId(paneId);
        if (safe.Length > 48) safe = safe[..48];
        return "powershellplus-" + safe;
    }

    public static bool IsSafeSessionName(string? value)
        => value is { Length: >= 16 and <= 80 }
            && value.StartsWith("powershellplus-", StringComparison.Ordinal)
            && value.Skip("powershellplus-".Length).All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    public static string BuildAttachOrCreateCommand(string paneId, string workloadCommand)
    {
        if (string.IsNullOrWhiteSpace(workloadCommand)) throw new ArgumentException("A remote workload is required.", nameof(workloadCommand));
        var safePaneId = SessionRecoveryStore.SafeSessionId(paneId);
        var sessionName = GetSessionName(paneId);
        var quotedSession = QuotePosix(sessionName);
        return $"export POWERSHELLPLUS_PANE_ID={QuotePosix(safePaneId)}; "
            + $"export POWERSHELLPLUS_TMUX_SESSION={quotedSession}; "
            + "if command -v tmux >/dev/null 2>&1; then "
            + $"if tmux has-session -t {quotedSession} 2>/dev/null; then "
            + $"tmux set-option -t {quotedSession} allow-passthrough on >/dev/null 2>&1 || true; "
            + $"__psp_tmux_pwd=$(tmux display-message -p -t {quotedSession} '#{{pane_current_path}}' 2>/dev/null); "
            + "if [ -n \"$__psp_tmux_pwd\" ]; then " + SshLaunchStore.BuildRemoteDirectoryMarker("$__psp_tmux_pwd") + "fi; "
            + $"exec tmux attach-session -t {quotedSession}; fi; "
            + $"exec tmux new-session -s {quotedSession} {QuotePosix(workloadCommand)}; "
            + "fi; "
            + "printf '[PowerShellPlus] tmux is not installed; this SSH process cannot remain live after disconnect.\n' >&2; "
            + workloadCommand;
    }

    public static string BuildEnsureDetachedCommand(string paneId, string workloadCommand)
    {
        var sessionName = GetSessionName(paneId);
        var quotedSession = QuotePosix(sessionName);
        return "if ! command -v tmux >/dev/null 2>&1; then "
            + $"printf '{MissingMarker}\n'; exit 127; fi; "
            + $"if tmux has-session -t {quotedSession} 2>/dev/null; then printf '{ExistsMarker}\n'; exit 0; fi; "
            + $"tmux new-session -d -s {quotedSession} {QuotePosix(workloadCommand)} >/dev/null 2>&1 || exit 1; "
            + "sleep 1; "
            + $"if tmux has-session -t {quotedSession} 2>/dev/null; then printf '{ReadyMarker}\n'; exit 0; fi; exit 1";
    }

    public static async Task<RemoteTmuxStatus> ProbeAsync(SessionRecoveryEntry recovery, CancellationToken cancellationToken = default)
    {
        var sessionName = ResolveSessionName(recovery);
        var quotedSession = QuotePosix(sessionName);
        var command = "if ! command -v tmux >/dev/null 2>&1; then "
            + $"printf '{MissingMarker}\n'; exit 0; fi; printf '{AvailableMarker}\n'; "
            + $"if tmux has-session -t {quotedSession} 2>/dev/null; then printf '{ExistsMarker}\n'; fi";
        var result = await RunSshCommandAsync(recovery, command, cancellationToken);
        if (!result.Started) return new RemoteTmuxStatus(false, false, false, result.Message);
        var available = result.Output.Contains(AvailableMarker, StringComparison.Ordinal);
        var unavailable = result.Output.Contains(MissingMarker, StringComparison.Ordinal);
        var exists = result.Output.Contains(ExistsMarker, StringComparison.Ordinal);
        var commandSucceeded = result.ExitCode == 0 && (available || unavailable);
        var message = available
            ? exists ? $"Remote session {sessionName} is running." : "tmux is available, but this terminal has not been moved into it yet."
            : unavailable ? "tmux is not installed on the SSH host." : result.Message;
        return new RemoteTmuxStatus(commandSucceeded, available, exists, message);
    }

    public static async Task<RemoteTmuxStatus> EnsureDetachedAsync(SessionRecoveryEntry recovery, CancellationToken cancellationToken = default)
    {
        var workload = SshRecovery.BuildRemoteWorkloadCommand(recovery);
        if (workload is null) return new RemoteTmuxStatus(false, false, false, "The saved SSH or agent state is not safe to restore.");
        var command = BuildEnsureDetachedCommand(recovery.SessionId, workload);
        var result = await RunSshCommandAsync(recovery, command, cancellationToken);
        var available = !result.Output.Contains(MissingMarker, StringComparison.Ordinal);
        var exists = result.Output.Contains(ExistsMarker, StringComparison.Ordinal)
            || result.Output.Contains(ReadyMarker, StringComparison.Ordinal);
        return new RemoteTmuxStatus(result.ExitCode == 0 && exists, available, exists, exists
            ? $"Remote session {GetSessionName(recovery.SessionId)} is running in the background."
            : result.Message);
    }

    public static async Task<RemoteTmuxStatus> KillAsync(SessionRecoveryEntry recovery, CancellationToken cancellationToken = default)
    {
        var quotedSession = QuotePosix(ResolveSessionName(recovery));
        var command = "if ! command -v tmux >/dev/null 2>&1; then "
            + $"printf '{MissingMarker}\n'; exit 0; fi; "
            + $"if tmux has-session -t {quotedSession} 2>/dev/null; then tmux kill-session -t {quotedSession} || exit 1; fi; "
            + $"printf '{ReadyMarker}\n'";
        var result = await RunSshCommandAsync(recovery, command, cancellationToken);
        var unavailable = result.Output.Contains(MissingMarker, StringComparison.Ordinal);
        var succeeded = result.ExitCode == 0 && (unavailable || result.Output.Contains(ReadyMarker, StringComparison.Ordinal));
        return new RemoteTmuxStatus(succeeded, !unavailable, false,
            succeeded ? "The remote tmux session was stopped." : result.Message);
    }

    private static string ResolveSessionName(SessionRecoveryEntry recovery)
        => IsSafeSessionName(recovery.RemoteTmuxSessionName)
            ? recovery.RemoteTmuxSessionName!
            : GetSessionName(recovery.SessionId);

    internal static async Task<SshCommandResult> RunSshCommandAsync(SessionRecoveryEntry recovery, string remoteCommand,
        CancellationToken cancellationToken)
    {
        if (!SshRecovery.TryNormalizeConnectionArguments(recovery.SshConnectionArguments, out var normalized, out var destination))
            return new SshCommandResult(false, -1, string.Empty, "The saved SSH connection is invalid.");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in normalized.Take(normalized.Length - 1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("-T");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("BatchMode=yes");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("ConnectTimeout=6");
            start.ArgumentList.Add(destination);
            start.ArgumentList.Add(remoteCommand);
            using var process = Process.Start(start);
            if (process is null) return new SshCommandResult(false, -1, string.Empty, "Windows could not start ssh.exe.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return new SshCommandResult(true, -1, string.Empty, "The remote tmux check timed out. The terminal was left open.");
            }
            var output = await outputTask;
            var error = await errorTask;
            var detail = process.ExitCode == 0 ? "SSH command completed." : string.IsNullOrWhiteSpace(error)
                ? $"SSH exited with code {process.ExitCode}." : error.Trim();
            return new SshCommandResult(true, process.ExitCode, output, detail);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new SshCommandResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    internal readonly record struct SshCommandResult(bool Started, int ExitCode, string Output, string Message);
}
