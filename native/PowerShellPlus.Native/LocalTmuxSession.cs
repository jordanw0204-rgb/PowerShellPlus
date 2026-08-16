using System.Diagnostics;
using System.Text;

namespace PowerShellPlus.Native;

public readonly record struct LocalTmuxStatus(
    bool CommandSucceeded,
    bool WslAvailable,
    bool TmuxAvailable,
    bool SessionExists,
    string? Distribution,
    string Message);

internal readonly record struct LocalTmuxPersistenceSmokeResult(bool Available, bool Passed, string Diagnostic);
internal readonly record struct LocalTmuxCommandResult(bool Started, int ExitCode, string Output, string Message);

/// <summary>
/// Hosts a configured Windows command inside tmux through WSL interoperability.
/// tmux owns the PTY lifetime; the workload remains the user's configured
/// Windows PowerShell (including its profile, Codex wrapper, and SSH wrapper).
/// </summary>
internal static class LocalTmuxSession
{
    internal const string UnavailableText = "[PowerShellPlus] Local tmux unavailable";
    private const string DistroMarker = "PSP_LOCAL_TMUX_DISTRO=";
    private const string AvailableMarker = "PSP_LOCAL_TMUX_AVAILABLE";
    private const string ExistsMarker = "PSP_LOCAL_TMUX_EXISTS";
    private const string StoppedMarker = "PSP_LOCAL_TMUX_STOPPED";
    private const string DetachedMarker = "PSP_LOCAL_TMUX_DETACHED";
    private const string ReadyMarker = "PSP_LOCAL_TMUX_READY";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    internal static string DirectoryPath => Path.Combine(SessionRecoveryStore.DirectoryPath, "local-tmux");

    public static string GetSessionName(string paneId) => RemoteTmuxSession.GetSessionName(paneId);

    public static async Task<LocalTmuxStatus> ProbeAsync(string? preferredDistribution = null,
        string? paneId = null, CancellationToken cancellationToken = default)
    {
        var sessionName = string.IsNullOrWhiteSpace(paneId) ? null : GetSessionName(paneId);
        var command = $"printf '{DistroMarker}%s\\n' \"$WSL_DISTRO_NAME\"; "
            + $"if command -v tmux >/dev/null 2>&1; then printf '{AvailableMarker}\\n'; "
            + (sessionName is null
                ? string.Empty
                : $"if tmux has-session -t {QuotePosix(sessionName)} 2>/dev/null; then printf '{ExistsMarker}\\n'; fi; ")
            + "fi";
        var result = await RunWslAsync(preferredDistribution, ["--exec", "sh", "-lc", command], cancellationToken);
        // A distribution can be renamed or unregistered independently of the
        // workspace. Recover by probing the current WSL default before asking
        // the user to repair an otherwise healthy installation.
        if (!string.IsNullOrWhiteSpace(preferredDistribution) && result.ExitCode != 0)
        {
            var fallback = await RunWslAsync(null, ["--exec", "sh", "-lc", command], cancellationToken);
            if (fallback.ExitCode == 0) result = fallback;
        }
        var distribution = ReadMarkerValue(result.Output, DistroMarker);
        var wslAvailable = result.Started && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(distribution);
        var tmuxAvailable = result.Output.Contains(AvailableMarker, StringComparison.Ordinal);
        var sessionExists = result.Output.Contains(ExistsMarker, StringComparison.Ordinal);
        var message = !result.Started
            ? "Windows Subsystem for Linux could not be started. Install WSL with `wsl --install -d Ubuntu`, then initialize Ubuntu."
            : !wslAvailable
                ? "WSL is installed, but no usable Linux distribution is registered. Run `wsl --install -d Ubuntu`, launch Ubuntu once, then try again."
                : !tmuxAvailable
                    ? $"tmux is not installed in {distribution}. In that distribution run `sudo apt-get update && sudo apt-get install -y tmux`."
                    : sessionExists
                        ? $"Local tmux session {sessionName} is running in {distribution}."
                        : $"Local tmux is ready in {distribution}.";
        return new LocalTmuxStatus(wslAvailable, wslAvailable, tmuxAvailable, sessionExists, distribution,
            string.IsNullOrWhiteSpace(result.Message) ? message : message);
    }

    public static async Task<LocalTmuxStatus> KillAsync(string paneId, string? distribution,
        CancellationToken cancellationToken = default)
    {
        var sessionName = GetSessionName(paneId);
        var command = "if ! command -v tmux >/dev/null 2>&1; then exit 127; fi; "
            + $"if tmux has-session -t {QuotePosix(sessionName)} 2>/dev/null; then tmux kill-session -t {QuotePosix(sessionName)} || exit 1; fi; "
            + $"printf '{StoppedMarker}\\n'";
        var result = await RunWslAsync(distribution, ["--exec", "sh", "-lc", command], cancellationToken);
        var succeeded = result.Started && result.ExitCode == 0 && result.Output.Contains(StoppedMarker, StringComparison.Ordinal);
        return new LocalTmuxStatus(succeeded, result.Started, succeeded, false, distribution,
            succeeded ? "The local tmux session was stopped."
                : string.IsNullOrWhiteSpace(result.Message) ? "The local tmux session could not be stopped." : result.Message);
    }

    public static async Task<LocalTmuxStatus> DetachAsync(string paneId, string? distribution,
        CancellationToken cancellationToken = default)
    {
        var sessionName = GetSessionName(paneId);
        var command = "if ! command -v tmux >/dev/null 2>&1; then exit 127; fi; "
            + $"if tmux has-session -t {QuotePosix(sessionName)} 2>/dev/null; then "
            + $"tmux detach-client -s {QuotePosix(sessionName)} 2>/dev/null || true; fi; "
            + $"printf '{DetachedMarker}\\n'";
        var result = await RunWslAsync(distribution, ["--exec", "sh", "-lc", command], cancellationToken);
        var succeeded = result.Started && result.ExitCode == 0 && result.Output.Contains(DetachedMarker, StringComparison.Ordinal);
        return new LocalTmuxStatus(succeeded, result.Started, succeeded, succeeded, distribution,
            succeeded ? "The local tmux client detached without stopping its session."
                : string.IsNullOrWhiteSpace(result.Message) ? "The local tmux client could not be detached." : result.Message);
    }

    internal static async Task<LocalTmuxCommandResult> RunCommandAsync(string? distribution, string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new LocalTmuxCommandResult(false, -1, string.Empty, "A tmux command is required.");
        var result = await RunWslAsync(distribution, ["--exec", "sh", "-lc", command], cancellationToken);
        return new LocalTmuxCommandResult(result.Started, result.ExitCode, result.Output, result.Message);
    }

    public static async Task<LocalTmuxStatus> EnsureDetachedAsync(SessionProfile profile, string workloadCommandLine,
        CancellationToken cancellationToken = default)
    {
        _ = BuildStartupCommandLine(profile, workloadCommandLine);
        var safeId = SessionRecoveryStore.SafeSessionId(profile.Id);
        var workloadShellPath = Path.Combine(DirectoryPath, safeId + "-workload.sh");
        var sessionName = GetSessionName(profile.Id);
        var command = $"printf '{DistroMarker}%s\\n' \"$WSL_DISTRO_NAME\"; "
            + "if ! command -v tmux >/dev/null 2>&1; then exit 127; fi; "
            + $"if ! tmux has-session -t {QuotePosix(sessionName)} 2>/dev/null; then "
            + $"tmux new-session -d -s {QuotePosix(sessionName)} sh {QuotePosix(ToWslPath(workloadShellPath))} || exit 1; fi; "
            + $"tmux set-option -t {QuotePosix(sessionName)} status off >/dev/null 2>&1 || true; "
            + $"tmux set-option -t {QuotePosix(sessionName)} allow-passthrough on >/dev/null 2>&1 || true; "
            + $"printf '{ReadyMarker}\\n'";
        var result = await RunWslAsync(profile.LocalTmuxDistribution, ["--exec", "sh", "-lc", command], cancellationToken);
        var distribution = ReadMarkerValue(result.Output, DistroMarker) ?? profile.LocalTmuxDistribution;
        var succeeded = result.Started && result.ExitCode == 0 && result.Output.Contains(ReadyMarker, StringComparison.Ordinal);
        if (succeeded)
        {
            var probe = await ProbeAsync(distribution, profile.Id, cancellationToken);
            succeeded = probe.SessionExists;
            if (!succeeded) return probe;
        }
        return new LocalTmuxStatus(succeeded, result.Started, succeeded, succeeded, distribution,
            succeeded ? $"Local tmux session {sessionName} is ready in {distribution}."
                : string.IsNullOrWhiteSpace(result.Message) ? "The detached local tmux workload could not be created." : result.Message);
    }

    public static string BuildStartupCommandLine(SessionProfile profile, string workloadCommandLine)
    {
        if (string.IsNullOrWhiteSpace(workloadCommandLine))
            throw new ArgumentException("A Windows terminal workload is required.", nameof(workloadCommandLine));

        Directory.CreateDirectory(DirectoryPath);
        var safeId = SessionRecoveryStore.SafeSessionId(profile.Id);
        var workloadPath = Path.Combine(DirectoryPath, safeId + "-workload.cmd");
        var workloadShellPath = Path.Combine(DirectoryPath, safeId + "-workload.sh");
        var managerPath = Path.Combine(DirectoryPath, safeId + "-manager.sh");
        var bootstrapPath = Path.Combine(DirectoryPath, safeId + "-bootstrap.ps1");
        AtomicWrite(workloadPath, "@echo off\r\n" + workloadCommandLine + "\r\nexit /b %ERRORLEVEL%\r\n", new UTF8Encoding(false));

        AtomicWrite(workloadShellPath,
            // WSL interop already serializes one argv value containing spaces
            // for the Windows process. Embedding another pair of quote
            // characters makes cmd.exe receive a literal '\"C:\\...\"'
            // command and tmux immediately renders `[exited]`.
            "#!/bin/sh\nexec cmd.exe /d /s /c " + QuotePosix(workloadPath) + "\n", new UTF8Encoding(false));

        var sessionName = GetSessionName(profile.Id);
        var workloadWsl = ToWslPath(workloadShellPath);
        var manager = "#!/bin/sh\nset -u\n"
            + $"session={QuotePosix(sessionName)}\n"
            + $"workload={QuotePosix(workloadWsl)}\n"
            + $"export POWERSHELLPLUS_PANE_ID={QuotePosix(safeId)}\n"
            + "if ! command -v tmux >/dev/null 2>&1; then\n"
            + $"  printf '{UnavailableText}: tmux is not installed in this WSL distribution.\\n' >&2\n  exit 127\nfi\n"
            + "if ! tmux has-session -t \"$session\" 2>/dev/null; then\n"
            + "  tmux new-session -d -s \"$session\" \"sh '$workload'\" || exit 1\nfi\n"
            + "tmux set-option -t \"$session\" status off >/dev/null 2>&1 || true\n"
            + "tmux set-option -t \"$session\" allow-passthrough on >/dev/null 2>&1 || true\n"
            + "exec tmux attach-session -d -t \"$session\"\n";
        AtomicWrite(managerPath, manager, new UTF8Encoding(false));

        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.LocalTmuxDistribution))
        {
            arguments.Add("--distribution");
            arguments.Add(profile.LocalTmuxDistribution!);
        }
        arguments.Add("--exec");
        arguments.Add("sh");
        arguments.Add(ToWslPath(managerPath));
        var argumentLiteral = string.Join(", ", arguments.Select(value => "'" + value.Replace("'", "''") + "'"));
        var bootstrap = "$__pspWslArguments = @(" + argumentLiteral + ")\n"
            + "& wsl.exe @__pspWslArguments\n"
            + "$__pspWslExit = $LASTEXITCODE\n"
            + "if ($__pspWslExit -ne 0) {\n"
            + $"  Write-Warning '{UnavailableText}: WSL or tmux could not start. Continuing in a standard Windows terminal.'\n"
            + $"  & '{workloadPath.Replace("'", "''")}'\n"
            + "}\n";
        AtomicWrite(bootstrapPath, bootstrap, new UTF8Encoding(true));
        return $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{bootstrapPath}\"";
    }

    internal static bool ContractPassesForTest()
    {
        var profile = new SessionProfile
        {
            Id = "local-tmux-contract",
            UseLocalTmux = true,
            LocalTmuxDistribution = "Ubuntu"
        };
        var commandLine = BuildStartupCommandLine(profile, "powershell.exe -NoExit");
        var manager = File.ReadAllText(Path.Combine(DirectoryPath, "local-tmux-contract-manager.sh"));
        var workload = File.ReadAllText(Path.Combine(DirectoryPath, "local-tmux-contract-workload.cmd"));
        var workloadShell = File.ReadAllText(Path.Combine(DirectoryPath, "local-tmux-contract-workload.sh"));
        return commandLine.Contains("-bootstrap.ps1", StringComparison.OrdinalIgnoreCase)
            && manager.Contains("tmux new-session", StringComparison.Ordinal)
            && manager.Contains("tmux attach-session -d", StringComparison.Ordinal)
            && manager.Contains("status off", StringComparison.Ordinal)
            && workload.Contains("powershell.exe -NoExit", StringComparison.Ordinal)
            && workloadShell.Contains("exec cmd.exe /d /s /c 'C:\\", StringComparison.OrdinalIgnoreCase)
            && !workloadShell.Contains("/c '\"", StringComparison.Ordinal)
            && GetSessionName(profile.Id) == "powershellplus-local-tmux-contract"
            && ToWslPath(@"C:\Users\Example\file.ps1") == "/mnt/c/Users/Example/file.ps1";
    }

    internal static async Task<LocalTmuxPersistenceSmokeResult> RunPersistenceSmokeAsync(CancellationToken cancellationToken = default)
    {
        var availability = await ProbeAsync(cancellationToken: cancellationToken);
        if (!availability.WslAvailable || !availability.TmuxAvailable || string.IsNullOrWhiteSpace(availability.Distribution))
            return new LocalTmuxPersistenceSmokeResult(false, true, "Skipped: " + availability.Message);

        var profile = new SessionProfile
        {
            Id = "local-tmux-live-" + Guid.NewGuid().ToString("N"),
            UseLocalTmux = true,
            LocalTmuxDistribution = availability.Distribution
        };
        Process? client = null;
        try
        {
            var ensured = await EnsureDetachedAsync(profile,
                "powershell.exe -NoLogo -NoProfile -NoExit -Command \"1..240 | ForEach-Object { Write-Output ('PSP_SCROLL_' + $_) }\"",
                cancellationToken);
            if (!ensured.CommandSucceeded || !ensured.SessionExists)
                return new LocalTmuxPersistenceSmokeResult(true, false, ensured.Message);
            var managerPath = Path.Combine(DirectoryPath, SessionRecoveryStore.SafeSessionId(profile.Id) + "-manager.sh");
            var start = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--distribution");
            start.ArgumentList.Add(availability.Distribution!);
            start.ArgumentList.Add("--exec");
            start.ArgumentList.Add("sh");
            start.ArgumentList.Add("-lc");
            start.ArgumentList.Add("exec script -qec " + QuotePosix("sh " + QuotePosix(ToWslPath(managerPath))) + " /dev/null");
            client = Process.Start(start);
            if (client is null) return new LocalTmuxPersistenceSmokeResult(true, false, "Could not start the isolated WSL tmux client.");

            var launchDeadline = DateTime.UtcNow.AddSeconds(10);
            LocalTmuxStatus running = default;
            do
            {
                running = await ProbeAsync(availability.Distribution, profile.Id, cancellationToken);
                if (running.SessionExists) break;
                if (client.HasExited) return new LocalTmuxPersistenceSmokeResult(true, false,
                    "The isolated tmux client exited before its session was created: "
                    + string.Join(" ", new[]
                    {
                        (await client.StandardError.ReadToEndAsync(cancellationToken)).Trim(),
                        (await client.StandardOutput.ReadToEndAsync(cancellationToken)).Trim()
                    }.Where(value => value.Length > 0)));
                await Task.Delay(150, cancellationToken);
            }
            while (DateTime.UtcNow < launchDeadline);
            if (!running.SessionExists) return new LocalTmuxPersistenceSmokeResult(true, false, running.Message);

            var scrollbackDeadline = DateTime.UtcNow.AddSeconds(8);
            RemoteTmuxScrollbackState scrollback = default;
            do
            {
                scrollback = await RemoteTmuxScrollback.ProbeLocalAsync(profile.Id, availability.Distribution, cancellationToken);
                if (scrollback.Succeeded && scrollback.HistorySize > 0) break;
                await Task.Delay(150, cancellationToken);
            }
            while (DateTime.UtcNow < scrollbackDeadline);
            if (!scrollback.Succeeded || scrollback.HistorySize <= 0)
                return new LocalTmuxPersistenceSmokeResult(true, false,
                    "The local tmux workload survived, but its history did not produce a scrollbar range.");
            var requestedScroll = Math.Min(12, scrollback.HistorySize);
            var scrolled = await RemoteTmuxScrollback.ScrollAndProbeLocalAsync(profile.Id,
                availability.Distribution, requestedScroll, cancellationToken);
            if (!scrolled.Succeeded || !scrolled.IsCopyMode || scrolled.ScrollPosition != requestedScroll)
                return new LocalTmuxPersistenceSmokeResult(true, false,
                    "The local tmux history range was detected, but scrollbar movement did not enter the requested copy-mode position.");

            // Exercise the same graceful detach used by app restart/shutdown.
            // Force-terminating wsl.exe can tear down its interop job before
            // tmux observes a client disconnect.
            var detach = await DetachAsync(profile.Id, availability.Distribution, cancellationToken);
            if (!detach.CommandSucceeded) return new LocalTmuxPersistenceSmokeResult(true, false, detach.Message);
            using (var clientExit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                clientExit.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.WaitForExitAsync(clientExit.Token); } catch (OperationCanceledException) { }
            }
            await Task.Delay(500, cancellationToken);
            var detached = await ProbeAsync(availability.Distribution, profile.Id, cancellationToken);
            var recoveredScrollback = detached.SessionExists
                ? await RemoteTmuxScrollback.ProbeLocalAsync(profile.Id, availability.Distribution, cancellationToken)
                : default;
            var passed = detached.SessionExists && recoveredScrollback.Succeeded && recoveredScrollback.HistorySize > 0;
            return new LocalTmuxPersistenceSmokeResult(true, passed,
                passed
                    ? "A Windows PowerShell workload and its tmux scrollbar history survived the WSL client disconnect."
                    : detached.SessionExists ? "The tmux session survived, but its scrollbar history could not be recovered." : detached.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            return new LocalTmuxPersistenceSmokeResult(true, false, exception.GetBaseException().Message);
        }
        finally
        {
            if (client is { HasExited: false })
            {
                try { client.Kill(false); } catch { }
            }
            client?.Dispose();
            _ = await KillAsync(profile.Id, profile.LocalTmuxDistribution, CancellationToken.None);
            DeleteLaunchArtifacts(profile.Id);
        }
    }

    public static void DeleteLaunchArtifacts(string paneId)
    {
        var safeId = SessionRecoveryStore.SafeSessionId(paneId);
        foreach (var suffix in new[] { "-workload.cmd", "-workload.sh", "-manager.sh", "-bootstrap.ps1" })
        {
            try
            {
                var path = Path.Combine(DirectoryPath, safeId + suffix);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    private static async Task<WslCommandResult> RunWslAsync(string? distribution, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrWhiteSpace(distribution))
            {
                start.ArgumentList.Add("--distribution");
                start.ArgumentList.Add(distribution);
            }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new WslCommandResult(false, -1, string.Empty, "Windows could not start wsl.exe.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return new WslCommandResult(true, -1, string.Empty, "The WSL tmux check timed out. No terminal was changed.");
            }
            var output = await outputTask;
            var error = await errorTask;
            var message = process.ExitCode == 0 ? string.Empty
                : string.IsNullOrWhiteSpace(error) ? $"wsl.exe exited with code {process.ExitCode}." : error.Trim();
            return new WslCommandResult(true, process.ExitCode, output, message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new WslCommandResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string? ReadMarkerValue(string output, string marker)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));
        return line is null ? null : line[marker.Length..].Trim();
    }

    private static string ToWslPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length < 3 || fullPath[1] != ':' || fullPath[2] != Path.DirectorySeparatorChar)
            throw new InvalidOperationException("Local tmux launch files must be stored on a Windows drive visible to WSL.");
        return "/mnt/" + char.ToLowerInvariant(fullPath[0]) + fullPath[2..].Replace('\\', '/');
    }

    private static void AtomicWrite(string path, string contents, Encoding encoding)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, encoding);
        File.Move(temporary, path, true);
    }

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private readonly record struct WslCommandResult(bool Started, int ExitCode, string Output, string Message);
}
