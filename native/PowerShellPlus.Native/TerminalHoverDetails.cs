namespace PowerShellPlus.Native;

internal sealed record TerminalDetailRow(string Label, string Value);
internal sealed record TerminalHoverDetails(string Title, IReadOnlyList<TerminalDetailRow> Rows);

internal static class TerminalHoverDetailsBuilder
{
    public static TerminalHoverDetails Build(SessionProfile profile, int? rootProcessId, AgentKind agentKind,
        AgentActivityState agentState, bool localCodexActive, bool sshActive, string[]? sshArguments,
        SessionRecoveryEntry? recovery, CodexLaunchMarker? codexLaunch)
    {
        var detachedRemote = profile.IsRemoteDetached && recovery?.RemoteTmuxManaged == true;
        var effectiveSshActive = sshActive || detachedRemote;
        var rows = new List<TerminalDetailRow>
        {
            new("Status", detachedRemote
                ? "Detached · running remotely"
                : rootProcessId is int pid ? $"Running · PID {pid}" : "Starting or stopped"),
            new("Agent", DescribeAgent(agentKind, agentState, localCodexActive, effectiveSshActive, recovery)),
        };

        if (detachedRemote && !string.IsNullOrWhiteSpace(recovery?.RemoteTmuxSessionName))
            rows.Add(new("Remote session", recovery.RemoteTmuxSessionName!));

        var remoteCodex = effectiveSshActive && recovery?.RemoteCodexWasActive == true;
        var codexModel = remoteCodex ? recovery?.RemoteCodexModel : recovery?.CodexModel ?? codexLaunch?.Model;
        var codexThread = remoteCodex ? recovery?.RemoteCodexSessionId : recovery?.CodexSessionId ?? codexLaunch?.SessionId;
        var permissionProfile = remoteCodex ? recovery?.RemoteCodexPermissionProfile : recovery?.CodexPermissionProfile ?? codexLaunch?.PermissionProfile;
        var sandbox = remoteCodex ? recovery?.RemoteCodexSandboxMode : recovery?.CodexSandboxMode ?? codexLaunch?.SandboxMode;
        var approval = remoteCodex ? recovery?.RemoteCodexApprovalPolicy : recovery?.CodexApprovalPolicy ?? codexLaunch?.ApprovalPolicy;
        var reviewer = remoteCodex ? recovery?.RemoteCodexApprovalsReviewer : recovery?.CodexApprovalsReviewer ?? codexLaunch?.ApprovalsReviewer;
        if (agentKind == AgentKind.Codex || localCodexActive || remoteCodex)
        {
            if (!string.IsNullOrWhiteSpace(codexModel)) rows.Add(new("Model", codexModel!));
            if (!string.IsNullOrWhiteSpace(codexThread)) rows.Add(new("Codex thread", codexThread!));
            var effectiveSandbox = CodexResumeArguments.ResolveSandboxMode(permissionProfile, sandbox);
            if (effectiveSandbox is not null && approval is not null)
                rows.Add(new("Permissions", $"{effectiveSandbox} · approvals {approval}"));
            if (!string.IsNullOrWhiteSpace(reviewer)) rows.Add(new("Reviewer", reviewer!));
        }

        if (effectiveSshActive && SshRecovery.TryNormalizeConnectionArguments(sshArguments ?? recovery?.SshConnectionArguments ?? [], out var normalized, out var destination))
        {
            rows.Add(new("SSH destination", destination));
            if (FindIdentityFile(normalized) is { } identity) rows.Add(new("SSH key", identity));
            var remoteDirectory = profile.LiveWorkingDirectoryIsSsh ? profile.LiveWorkingDirectory
                : remoteCodex ? recovery?.RemoteCodexWorkingDirectory : null;
            if (!string.IsNullOrWhiteSpace(remoteDirectory)) rows.Add(new("Remote folder", remoteDirectory!));
        }

        rows.Add(new("Shell", DescribeShell(profile.CommandLine)));
        rows.Add(new(profile.LiveWorkingDirectoryIsSsh ? "SSH folder" : "Working folder", profile.Subtitle));
        rows.Add(new("Queue", $"{profile.PendingCommands?.Count ?? 0} command{(profile.PendingCommands?.Count == 1 ? string.Empty : "s")}"));
        if (!string.IsNullOrWhiteSpace(profile.CommandDraft)) rows.Add(new("Draft", "Saved"));
        return new TerminalHoverDetails(profile.Name, rows);
    }

    private static string DescribeAgent(AgentKind kind, AgentActivityState state, bool localCodexActive, bool sshActive,
        SessionRecoveryEntry? recovery)
    {
        var name = kind switch
        {
            AgentKind.Codex => "Codex",
            AgentKind.Hermes => "Hermes",
            _ when localCodexActive || sshActive && recovery?.RemoteCodexWasActive == true => "Codex",
            _ when sshActive && recovery?.HermesWasActive == true => "Hermes",
            _ => "Terminal"
        };
        var status = state switch
        {
            AgentActivityState.Starting => "starting",
            AgentActivityState.Working => "working",
            AgentActivityState.Waiting => "waiting for you",
            AgentActivityState.Stopped => "stopped",
            AgentActivityState.Error => "error",
            _ => "idle"
        };
        return $"{name} · {status}";
    }

    private static string DescribeShell(string commandLine)
    {
        var value = Environment.ExpandEnvironmentVariables(commandLine ?? string.Empty).Trim();
        if (value.Length == 0) return "Unknown";
        var executable = value[0] == '"'
            ? value[1..].Split('"', 2)[0]
            : value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return Path.GetFileName(executable);
    }

    private static string? FindIdentityFile(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("-i", StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
            if (arguments[index].StartsWith("-i", StringComparison.OrdinalIgnoreCase) && arguments[index].Length > 2)
                return arguments[index][2..];
        }
        return null;
    }

    internal static bool WorksForTest()
    {
        var profile = new SessionProfile
        {
            Name = "VPS Codex",
            CommandLine = "powershell.exe",
            WorkingDirectory = @"D:\Dev",
            PendingCommands = ["git status"]
        };
        var recovery = new SessionRecoveryEntry
        {
            SshWasActive = true,
            SshConnectionArguments = ["-i", @"C:\Users\Example\.ssh\vps_key", "ubuntu@15.204.82.129"],
            RemoteCodexWasActive = true,
            RemoteCodexSessionId = "11111111-2222-3333-4444-555555555555",
            RemoteCodexModel = "gpt-5.6-sol",
            RemoteCodexSandboxMode = "danger-full-access",
            RemoteCodexApprovalPolicy = "never"
        };
        var details = Build(profile, 42, AgentKind.Codex, AgentActivityState.Working, false, true,
            recovery.SshConnectionArguments, recovery, null);
        profile.SetRemoteDetached(true);
        recovery.RemoteTmuxManaged = true;
        recovery.RemoteTmuxSessionName = RemoteTmuxSession.GetSessionName(profile.Id);
        var detached = Build(profile, null, AgentKind.Codex, AgentActivityState.Idle, false, false,
            null, recovery, null);
        return details.Rows.Any(value => value.Label == "Agent" && value.Value == "Codex · working")
            && details.Rows.Any(value => value.Label == "SSH destination" && value.Value == "ubuntu@15.204.82.129")
            && details.Rows.Any(value => value.Label == "SSH key" && value.Value.EndsWith("vps_key", StringComparison.Ordinal))
            && details.Rows.Any(value => value.Label == "Model" && value.Value == "gpt-5.6-sol")
            && details.Rows.Any(value => value.Label == "Queue" && value.Value == "1 command")
            && detached.Rows.Any(value => value.Label == "Status" && value.Value == "Detached · running remotely")
            && detached.Rows.Any(value => value.Label == "Remote session" && value.Value == recovery.RemoteTmuxSessionName)
            && detached.Rows.Any(value => value.Label == "SSH destination" && value.Value == "ubuntu@15.204.82.129");
    }
}
