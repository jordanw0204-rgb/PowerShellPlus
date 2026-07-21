using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

public readonly record struct RemoteCodexRecoveryState(
    bool WasActive,
    string? SessionId,
    string? WorkingDirectory,
    string? Model,
    string? SandboxMode,
    string? ApprovalPolicy,
    string? PermissionProfile,
    string? ApprovalsReviewer);

public readonly record struct RemoteCodexProbeResult(bool Succeeded, RemoteCodexRecoveryState State);

public static class RemoteCodexRecovery
{
    private const string ResultPrefix = "PSP_REMOTE_CODEX:";
    private static readonly Regex PaneIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The probe is deliberately self-contained and read-only. The pane id inherited by the
    // remote login shell lets us distinguish two Codex processes on the same host and cwd.
    private const string ProbePython = """
import base64, datetime, glob, json, os, sys
pane = sys.argv[1]
tag = ('POWERSHELLPLUS_PANE_ID=' + pane).encode()
processes = []
for proc in glob.glob('/proc/[0-9]*'):
    try:
        env = open(proc + '/environ', 'rb').read().split(b'\0')
        if tag not in env:
            continue
        cmd = open(proc + '/cmdline', 'rb').read().replace(b'\0', b' ').decode('utf-8', 'replace').lower()
        if 'codex' not in cmd or 'remote_codex' in cmd:
            continue
        pid = int(proc.rsplit('/', 1)[1])
        stat = open(proc + '/stat').read().split()
        ticks = os.sysconf(os.sysconf_names['SC_CLK_TCK'])
        boot = next(float(x.split()[1]) for x in open('/proc/stat') if x.startswith('btime '))
        started = boot + int(stat[21]) / ticks
        cwd = os.readlink(proc + '/cwd')
        processes.append((pid, started, cwd, proc))
    except Exception:
        pass
if not processes:
    print('PSP_REMOTE_CODEX:' + base64.b64encode(json.dumps({'active': False}).encode()).decode())
    raise SystemExit(0)
pid, started, cwd, proc = sorted(processes, key=lambda x: x[1])[0]
fd_files = []
for fd in glob.glob(proc + '/fd/*'):
    try:
        path = os.readlink(fd)
        if '/.codex/sessions/' in path and path.endswith('.jsonl'):
            fd_files.append(path)
    except Exception:
        pass
files = fd_files or glob.glob(os.path.expanduser('~/.codex/sessions/**/*.jsonl'), recursive=True)
best = None
for path in files:
    try:
        with open(path, errors='replace') as stream:
            first = json.loads(stream.readline())
        payload = first.get('payload', {})
        session_id = payload.get('session_id') or payload.get('id')
        session_cwd = payload.get('cwd') or ''
        timestamp = payload.get('timestamp') or first.get('timestamp')
        stamp = datetime.datetime.fromisoformat(timestamp.replace('Z', '+00:00')).timestamp()
        if not session_id or (not fd_files and session_cwd != cwd):
            continue
        distance = abs(stamp - started)
        if not fd_files and distance > 1800:
            continue
        score = (0 if path in fd_files else 1, distance, -os.path.getmtime(path))
        if best is None or score < best[0]:
            best = (score, path, session_id, session_cwd)
    except Exception:
        pass
if best is None:
    print('PSP_REMOTE_CODEX:' + base64.b64encode(json.dumps({'active': False}).encode()).decode())
    raise SystemExit(0)
_, path, session_id, session_cwd = best
model = sandbox = approval = permission = reviewer = None
try:
    with open(path, errors='replace') as stream:
        for line in stream:
            try:
                item = json.loads(line)
                if item.get('type') != 'turn_context':
                    continue
                payload = item.get('payload', {})
                model = payload.get('model') or model
                approval = payload.get('approval_policy') or approval
                policy = payload.get('sandbox_policy')
                if isinstance(policy, dict):
                    sandbox = policy.get('type') or sandbox
                elif isinstance(policy, str):
                    sandbox = policy
                profile = payload.get('permission_profile')
                if isinstance(profile, str):
                    permission = profile
                reviewer = payload.get('approvals_reviewer') or reviewer
            except Exception:
                pass
except Exception:
    pass
result = {'active': True, 'sessionId': session_id, 'workingDirectory': session_cwd,
          'model': model, 'sandboxMode': sandbox, 'approvalPolicy': approval,
          'permissionProfile': permission, 'approvalsReviewer': reviewer}
print('PSP_REMOTE_CODEX:' + base64.b64encode(json.dumps(result, separators=(',', ':')).encode()).decode())
""";

    public static RemoteCodexProbeResult Probe(string paneId, string[] connectionArguments, int timeoutMilliseconds = 8_000)
    {
        if (!IsSafePaneId(paneId)
            || !SshRecovery.TryNormalizeConnectionArguments(connectionArguments, out var normalized, out var destination))
            return default;
        try
        {
            var encodedProbe = Convert.ToBase64String(Encoding.UTF8.GetBytes(ProbePython));
            var remoteCommand = $"python3 -c \"import base64;exec(base64.b64decode('{encodedProbe}'))\" '{paneId}'";
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in normalized.Take(normalized.Length - 1)) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("-T");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ConnectTimeout=5");
            startInfo.ArgumentList.Add(destination);
            startInfo.ArgumentList.Add(remoteCommand);
            using var process = Process.Start(startInfo);
            if (process is null) return default;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(true); } catch { }
                return default;
            }
            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return default;
            return TryParseProbeOutput(output, out var state) ? new RemoteCodexProbeResult(true, state) : default;
        }
        catch { return default; }
    }

    public static bool TryParseProbeOutput(string? output, out RemoteCodexRecoveryState state)
    {
        state = default;
        var line = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value => value.StartsWith(ResultPrefix, StringComparison.Ordinal));
        if (line is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(line[ResultPrefix.Length..]));
            var root = document.RootElement;
            if (!root.TryGetProperty("active", out var activeValue) || !activeValue.GetBoolean()) return true;
            var sessionId = ReadString(root, "sessionId");
            var directory = ReadString(root, "workingDirectory");
            var model = ReadString(root, "model");
            var sandbox = ReadString(root, "sandboxMode");
            var approval = ReadString(root, "approvalPolicy");
            var permission = ReadString(root, "permissionProfile");
            var reviewer = ReadString(root, "approvalsReviewer");
            if (!CodexSessionLocator.IsSafeCodexId(sessionId) || !IsSafeRemoteDirectory(directory)) return false;
            model = CodexSessionLocator.IsSafeCodexModel(model) ? model : null;
            var safePermissions = CodexSessionLocator.IsSafeCodexPermissionState(permission, sandbox, approval, reviewer);
            state = new RemoteCodexRecoveryState(true, sessionId, directory, model,
                safePermissions && CodexSessionLocator.IsSafeCodexSandboxMode(sandbox) ? sandbox : null,
                safePermissions ? approval : null,
                safePermissions && CodexSessionLocator.IsSafeCodexPermissionProfile(permission) ? permission : null,
                safePermissions && CodexSessionLocator.IsSafeCodexApprovalsReviewer(reviewer) ? reviewer : null);
            return true;
        }
        catch { return false; }
    }

    public static string BuildRemoteCommand(string paneId, SessionRecoveryEntry? recovery)
    {
        if (!IsSafePaneId(paneId)) throw new ArgumentException("Unsafe pane id.", nameof(paneId));
        var prefix = $"export POWERSHELLPLUS_PANE_ID={QuotePosix(paneId)}; ";
        if (recovery?.RemoteCodexWasActive == true
            && CodexSessionLocator.IsSafeCodexId(recovery.RemoteCodexSessionId)
            && IsSafeRemoteDirectory(recovery.RemoteCodexWorkingDirectory))
        {
            var command = prefix + $"cd -- {QuotePosix(recovery.RemoteCodexWorkingDirectory!)} && exec codex resume {QuotePosix(recovery.RemoteCodexSessionId!)}";
            if (CodexSessionLocator.IsSafeCodexModel(recovery.RemoteCodexModel)) command += $" --model {QuotePosix(recovery.RemoteCodexModel!)}";
            if (CodexSessionLocator.IsSafeCodexPermissionState(recovery.RemoteCodexPermissionProfile, recovery.RemoteCodexSandboxMode, recovery.RemoteCodexApprovalPolicy, recovery.RemoteCodexApprovalsReviewer))
            {
                if (CodexSessionLocator.IsSafeCodexPermissionProfile(recovery.RemoteCodexPermissionProfile))
                    command += $" --config {QuotePosix($"default_permissions=\"{recovery.RemoteCodexPermissionProfile}\"")}";
                else if (CodexSessionLocator.IsSafeCodexSandboxMode(recovery.RemoteCodexSandboxMode))
                    command += $" --sandbox {QuotePosix(recovery.RemoteCodexSandboxMode!)}";
                if (CodexSessionLocator.IsSafeCodexApprovalsReviewer(recovery.RemoteCodexApprovalsReviewer))
                    command += $" --config {QuotePosix($"approvals_reviewer=\"{recovery.RemoteCodexApprovalsReviewer}\"")}";
                command += $" --ask-for-approval {QuotePosix(recovery.RemoteCodexApprovalPolicy!)}";
            }
            return command;
        }
        return prefix + "exec \"${SHELL:-/bin/sh}\" -l";
    }

    public static void Sanitize(SessionRecoveryEntry entry)
    {
        if (!entry.SshWasActive || !entry.RemoteCodexWasActive
            || !CodexSessionLocator.IsSafeCodexId(entry.RemoteCodexSessionId)
            || !IsSafeRemoteDirectory(entry.RemoteCodexWorkingDirectory))
        {
            Clear(entry);
            return;
        }
        if (!CodexSessionLocator.IsSafeCodexModel(entry.RemoteCodexModel)) entry.RemoteCodexModel = null;
        if (!CodexSessionLocator.IsSafeCodexPermissionState(entry.RemoteCodexPermissionProfile, entry.RemoteCodexSandboxMode, entry.RemoteCodexApprovalPolicy, entry.RemoteCodexApprovalsReviewer))
        {
            entry.RemoteCodexSandboxMode = null;
            entry.RemoteCodexApprovalPolicy = null;
            entry.RemoteCodexPermissionProfile = null;
            entry.RemoteCodexApprovalsReviewer = null;
        }
    }

    public static void Clear(SessionRecoveryEntry entry)
    {
        entry.RemoteCodexWasActive = false;
        entry.RemoteCodexSessionId = null;
        entry.RemoteCodexWorkingDirectory = null;
        entry.RemoteCodexModel = null;
        entry.RemoteCodexSandboxMode = null;
        entry.RemoteCodexApprovalPolicy = null;
        entry.RemoteCodexPermissionProfile = null;
        entry.RemoteCodexApprovalsReviewer = null;
    }

    private static bool IsSafePaneId(string? value) => value is not null && PaneIdPattern.IsMatch(value);
    private static bool IsSafeRemoteDirectory(string? value)
        => value is { Length: >= 1 and <= 4096 } && !value.Any(char.IsControl) && value.IndexOf('\0') < 0;
    private static string? ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
