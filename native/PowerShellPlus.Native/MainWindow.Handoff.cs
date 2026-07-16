using System.Windows;
using System.Text.Json;
using System.Diagnostics;

namespace PowerShellPlus.Native;

public partial class MainWindow
{
    private sealed record SourceProcessIdentity(int ProcessId, DateTime StartedUtc);
    private readonly HashSet<string> activeSessionHandoffs = new(StringComparer.Ordinal);

    private async void DetachSessionToWindowsTerminal(SessionProfile profile, TerminalPane pane)
    {
        if (!activeSessionHandoffs.Add(profile.Id)) return;
        pane.SetHandoffPending(true);
        WindowsTerminalHandoffPlan? plan = null;
        var launchStarted = false;
        try
        {
            UpdateStatus($"Verifying {profile.Name} for Windows Terminal…");
            var codexProcess = pane.GetCodexProcessState();
            var recovery = codexProcess.IsActive
                ? await ResolveExactLiveCodexRecoveryAsync(profile, pane, codexProcess)
                : null;

            var descendants = pane.GetRootProcessId() is int rootProcessId
                ? ProcessTreeInspector.FindDescendantProcesses(rootProcessId)
                : [];
            if (!codexProcess.IsActive && descendants.Count > 0)
            {
                var names = string.Join(", ", descendants.Take(4).Select(value => $"{value.Name} ({value.ProcessId})"));
                throw new InvalidOperationException($"A child program is still running in this shell: {names}. Exit it first so the handoff cannot silently destroy live process state.");
            }

            plan = WindowsTerminalHandoff.CreatePlan(profile, terminalProfile.ProfileName, terminalProfile.CommandLine,
                pane.GetOutput(), recovery, codexProcess.IsActive);
            var confirmation = BuildHandoffConfirmation(plan, descendants);
            if (!PowerShellPlusDialog.Confirm(this, confirmation, "Move session to Windows Terminal?",
                    PowerShellPlusDialogKind.Warning, "Move session", "Keep it here", defaultToPrimary: false))
            {
                WindowsTerminalHandoff.Discard(plan);
                UpdateStatus("Windows Terminal handoff canceled");
                return;
            }

            await EnsureCodexStateStillMatchesAsync(profile, pane, plan);

            UpdateStatus($"Starting and verifying Windows Terminal for {profile.Name}…");
            var launched = await WindowsTerminalHandoff.LaunchAndWaitForStartAsync(plan);
            if (!launched.Success)
            {
                PowerShellPlusDialog.ShowMessage(this, launched.Message, "Windows Terminal handoff failed", PowerShellPlusDialogKind.Error);
                UpdateStatus("Handoff failed — source session is still running");
                return;
            }
            launchStarted = true;
            await EnsureCodexStateStillMatchesAsync(profile, pane, plan);
            if (!WindowsTerminalHandoff.PrepareReleaseSignal(plan))
            {
                WindowsTerminalHandoff.Cancel(plan, "PowerShellPlus could not prepare the release signal. The source session stayed open.");
                PowerShellPlusDialog.ShowMessage(this, "The new PowerShell started, but PowerShellPlus could not prepare the atomic release signal. The source session was left running.", "Windows Terminal handoff failed", PowerShellPlusDialogKind.Error);
                return;
            }

            var sourceProcesses = CaptureSourceProcesses(pane);
            pane.Stop();
            if (!await WaitForSourceProcessesToExitAsync(sourceProcesses, TimeSpan.FromSeconds(5)))
            {
                TerminateSourceProcesses(sourceProcesses);
                if (!await WaitForSourceProcessesToExitAsync(sourceProcesses, TimeSpan.FromSeconds(3)))
                {
                    WindowsTerminalHandoff.Cancel(plan, "The original terminal process tree did not stop, so the resumed thread was not released.");
                    PowerShellPlusDialog.ShowMessage(this, "The original process tree did not stop completely. The external shell was canceled so it cannot overlap the same Codex thread. The stopped source pane remains available.", "Windows Terminal handoff blocked", PowerShellPlusDialogKind.Error);
                    return;
                }
            }
            if (!RemoveSession(profile, true, false))
            {
                WindowsTerminalHandoff.Cancel(plan, "PowerShellPlus could not close the source pane, so the handoff was canceled.");
                PowerShellPlusDialog.ShowMessage(this, "The source pane could not be closed. The external shell was canceled to prevent two copies of the same Codex thread.", "Windows Terminal handoff blocked", PowerShellPlusDialogKind.Error);
                return;
            }
            if (!WindowsTerminalHandoff.CommitReleaseSignal(plan))
            {
                PowerShellPlusDialog.ShowMessage(this, $"The source pane closed, but the external shell could not be released automatically. The saved handoff is at:\n\n{plan.DirectoryPath}", "Windows Terminal handoff needs attention", PowerShellPlusDialogKind.Error);
                return;
            }
            UpdateStatus(plan.CodexActive
                ? $"{profile.Name} moved to Windows Terminal · resumed Codex {ShortId(plan.CodexSessionId!)}"
                : $"{profile.Name} moved to Windows Terminal");
        }
        catch (Exception exception)
        {
            if (plan is not null && launchStarted) WindowsTerminalHandoff.Cancel(plan, exception.Message);
            else if (plan is not null) WindowsTerminalHandoff.Discard(plan);
            LogNativeError("Windows Terminal handoff", exception);
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "Windows Terminal handoff unavailable", PowerShellPlusDialogKind.Warning);
            UpdateStatus(panes.ContainsKey(profile.Id)
                ? "Handoff blocked — source session remains in PowerShellPlus"
                : "Handoff failed after the source session closed");
        }
        finally
        {
            activeSessionHandoffs.Remove(profile.Id);
            if (panes.TryGetValue(profile.Id, out var livePane)) livePane.SetHandoffPending(false);
        }
    }

    private static string BuildHandoffConfirmation(WindowsTerminalHandoffPlan plan, IReadOnlyList<ConsoleDescendantProcess> descendants)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"PowerShellPlus verified a new Windows Terminal handoff for “{plan.SessionName}”.");
        builder.AppendLine();
        builder.AppendLine($"Working folder: {plan.WorkingDirectory}");
        builder.AppendLine($"PowerShell: {plan.PowerShellExecutable}");
        builder.AppendLine($"Transcript copy: {plan.TranscriptPath}");
        builder.AppendLine($"Queued commands copy: {plan.PendingCommandsPath}");
        if (plan.CodexActive)
        {
            builder.AppendLine();
            builder.AppendLine($"Codex thread: {plan.CodexSessionId}");
            builder.AppendLine($"Model: {plan.CodexModel ?? "saved thread default"}");
            builder.AppendLine($"Permissions: {plan.PermissionDescription}");
            builder.AppendLine("The exact Codex conversation will continue with codex resume after the original process stops.");
        }
        if (descendants.Count > 0)
        {
            var names = string.Join(", ", descendants.Take(5).Select(value => value.Name));
            builder.AppendLine();
            builder.AppendLine($"Active Codex process tree: {names}. Any in-progress turn or tool command will be interrupted before resume.");
        }
        builder.AppendLine();
        builder.AppendLine("PowerShell variables, functions, jobs, SSH connections, and live process memory cannot move between ConPTY hosts.");
        builder.AppendLine("The source pane will close only after the new PowerShell process proves it started.");
        builder.AppendLine();
        builder.Append("Continue and remove the source pane?");
        return builder.ToString();
    }

    private static string ShortId(string value) => value.Length <= 12 ? value : value[..8] + "…";

    private static async Task<SessionRecoveryEntry> ResolveExactLiveCodexRecoveryAsync(
        SessionProfile profile, TerminalPane pane, CodexProcessState codexProcess)
    {
        var liveMatch = await Task.Run(() => CodexActivityStore.FindActiveCliSession(codexProcess.ProcessId, codexProcess.StartedUtc));
        var launch = CodexLaunchStore.Load(profile.Id);
        var rootShellProcessId = pane.GetRootProcessId();
        var launchIsBoundToThisShell = launch?.IsActive == true && launch.ShellProcessId == rootShellProcessId
            && CodexSessionLocator.IsSafeCodexId(launch.SessionId);
        var exactSessionId = liveMatch?.SessionId ?? (launchIsBoundToThisShell ? launch!.SessionId : null);
        if (!CodexSessionLocator.IsSafeCodexId(exactSessionId))
            throw new InvalidOperationException("Codex is running, but its live process could not be bound to one exact top-level thread. Nothing was closed.");
        var verifiedSession = await Task.Run(() => CodexSessionLocator.FindSessionById(exactSessionId, requireTopLevelCli: true));
        if (verifiedSession is null)
            throw new InvalidOperationException("The captured Codex thread does not have a verified top-level CLI transcript. Nothing was closed.");
        var latestPermissions = CodexSessionLocator.FindLatestPermissions(exactSessionId);
        var permissionProfile = latestPermissions?.PermissionProfile ?? liveMatch?.PermissionProfile ?? launch?.PermissionProfile;
        var sandboxMode = latestPermissions?.SandboxMode ?? liveMatch?.SandboxMode ?? launch?.SandboxMode;
        var approvalPolicy = latestPermissions?.ApprovalPolicy ?? liveMatch?.ApprovalPolicy ?? launch?.ApprovalPolicy;
        var approvalsReviewer = latestPermissions?.ApprovalsReviewer ?? liveMatch?.ApprovalsReviewer ?? launch?.ApprovalsReviewer;
        if (!CodexSessionLocator.IsSafeCodexPermissionState(permissionProfile, sandboxMode, approvalPolicy, approvalsReviewer))
            throw new InvalidOperationException("Codex is running, but its exact permission level could not be verified. Nothing was closed.");
        return new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            WorkingDirectory = verifiedSession.WorkingDirectory,
            CodexWasActive = true,
            CodexSessionId = exactSessionId,
            CodexModel = CodexSessionLocator.FindLatestModel(exactSessionId)?.Model ?? liveMatch?.Model ?? launch?.Model,
            CodexSandboxMode = sandboxMode,
            CodexApprovalPolicy = approvalPolicy,
            CodexPermissionProfile = permissionProfile,
            CodexApprovalsReviewer = approvalsReviewer,
            CapturedUtc = DateTime.UtcNow
        };
    }

    private static async Task EnsureCodexStateStillMatchesAsync(
        SessionProfile profile, TerminalPane pane, WindowsTerminalHandoffPlan plan)
    {
        if (!plan.CodexActive) return;
        var currentCodexProcess = pane.GetCodexProcessState();
        if (!currentCodexProcess.IsActive)
            throw new InvalidOperationException("Codex exited during verification. The handoff was canceled so the source shell remains available.");
        var currentRecovery = await ResolveExactLiveCodexRecoveryAsync(profile, pane, currentCodexProcess);
        if (!WindowsTerminalHandoff.MatchesCodexState(plan, currentRecovery))
            throw new InvalidOperationException("The active Codex thread, model, or permission level changed during verification. Review the updated handoff and try again.");
    }

    private static IReadOnlyList<SourceProcessIdentity> CaptureSourceProcesses(TerminalPane pane)
    {
        if (pane.GetRootProcessId() is not int rootProcessId) return [];
        var processIds = new[] { rootProcessId }
            .Concat(ProcessTreeInspector.FindDescendantProcesses(rootProcessId).Select(value => value.ProcessId))
            .Distinct()
            .ToArray();
        var result = new List<SourceProcessIdentity>();
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                result.Add(new SourceProcessIdentity(processId, process.StartTime.ToUniversalTime()));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    private static async Task<bool> WaitForSourceProcessesToExitAsync(IReadOnlyList<SourceProcessIdentity> processes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (processes.All(ProcessIdentityHasExited)) return true;
            await Task.Delay(80);
        }
        return processes.All(ProcessIdentityHasExited);
    }

    private static bool ProcessIdentityHasExited(SourceProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.HasExited || process.StartTime.ToUniversalTime() != identity.StartedUtc;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static void TerminateSourceProcesses(IReadOnlyList<SourceProcessIdentity> processes)
    {
        foreach (var identity in processes)
        {
            if (ProcessIdentityHasExited(identity)) continue;
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                process.Kill(true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    public async Task<bool> RunHandoffSmokeTestAsync(string reportPath)
    {
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "handoff-fixture");
        try { if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true); } catch { }
        Directory.CreateDirectory(fixtureRoot);
        var sessionId = "12345678-90ab-cdef-1234-567890abcdef";
        var profile = new SessionProfile
        {
            Id = "handoff-smoke",
            Name = "Handoff smoke",
            CommandLine = "powershell.exe",
            WorkingDirectory = fixtureRoot,
            PendingCommands = ["Write-Output queued-one", "Write-Output queued-two"]
        };
        var recovery = new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            WorkingDirectory = fixtureRoot,
            CodexWasActive = true,
            CodexSessionId = sessionId,
            CodexModel = "gpt-5.6-sol",
            CodexApprovalPolicy = "never",
            CodexPermissionProfile = ":danger-full-access",
            CodexApprovalsReviewer = "user"
        };
        WindowsTerminalHandoffPlan? plan = null;
        WindowsTerminalHandoffPlan? canceledPlan = null;
        Process? descendantFixture = null;
        try
        {
            plan = WindowsTerminalHandoff.CreatePlan(profile, terminalProfile.ProfileName, terminalProfile.CommandLine,
                "safe transcript\u001b]52;unsafe\a\r\nsecond line", recovery, true, true, fixtureRoot);
            var arguments = plan.CodexArguments.ToArray();
            var exactResumeArguments = arguments.SequenceEqual(new[]
            {
                "resume", sessionId, "--model", "gpt-5.6-sol", "--config", "default_permissions=\":danger-full-access\"",
                "--config", "approvals_reviewer=\"user\"", "--ask-for-approval", "never"
            });
            var revalidationDetectsChanges = WindowsTerminalHandoff.MatchesCodexState(plan, recovery)
                && !WindowsTerminalHandoff.MatchesCodexState(plan, new SessionRecoveryEntry
                {
                    CodexWasActive = true,
                    CodexSessionId = sessionId,
                    CodexModel = "changed-model",
                    CodexApprovalPolicy = "never",
                    CodexPermissionProfile = ":danger-full-access",
                    CodexApprovalsReviewer = "user"
                });
            var terminalArguments = WindowsTerminalHandoff.BuildWindowsTerminalArgumentsForTest(plan);
            var terminalCommandIsStructured = terminalArguments.Take(3).SequenceEqual(new[] { "-w", "new", "new-tab" })
                && terminalArguments.Contains("--profile") && terminalArguments.Contains(terminalProfile.ProfileName)
                && terminalArguments.Contains("--title") && terminalArguments.Contains(profile.Name)
                && terminalArguments.Contains("--suppressApplicationTitle") && terminalArguments.Contains("-NoExit")
                && terminalArguments.Contains(plan.BootstrapPath) && terminalArguments.Contains(plan.PayloadPath);
            var transcript = File.ReadAllText(plan.TranscriptPath);
            var transcriptIsSafeAndPersisted = transcript.Contains("safe transcript", StringComparison.Ordinal)
                && transcript.Contains("second line", StringComparison.Ordinal) && !transcript.Contains('\u001b') && !transcript.Contains('\a');
            var queuePersisted = File.ReadAllLines(plan.PendingCommandsPath).Length == 2
                && File.ReadAllText(plan.PendingCommandsPath).Contains("queued-two", StringComparison.Ordinal);
            using var payload = JsonDocument.Parse(File.ReadAllText(plan.PayloadPath));
            var payloadIsDataOnly = payload.RootElement.GetProperty("CodexArguments").GetArrayLength() == arguments.Length
                && File.ReadAllText(plan.BootstrapPath).Contains("$payload.CodexArguments", StringComparison.Ordinal)
                && !File.ReadAllText(plan.BootstrapPath).Contains(sessionId, StringComparison.Ordinal);

            var launched = await WindowsTerminalHandoff.LaunchAndWaitForStartAsync(plan, false, TimeSpan.FromSeconds(8));
            await Task.Delay(180);
            var waitsBeforeSourceRelease = launched.Success && File.Exists(plan.StartedPath) && !File.Exists(plan.CompletedPath)
                && !File.Exists(plan.ReadyPath);
            var releasePrepared = WindowsTerminalHandoff.PrepareReleaseSignal(plan) && File.Exists(plan.ReadyStagingPath)
                && !File.Exists(plan.ReadyPath);
            var releaseCommitted = WindowsTerminalHandoff.CommitReleaseSignal(plan) && File.Exists(plan.ReadyPath)
                && !File.Exists(plan.ReadyStagingPath);
            var bootstrapCompleted = await WindowsTerminalHandoff.WaitForCompletionAsync(plan, TimeSpan.FromSeconds(8));
            var bootstrapForwardsArguments = false;
            if (bootstrapCompleted)
            {
                using var completed = JsonDocument.Parse(File.ReadAllText(plan.CompletedPath));
                bootstrapForwardsArguments = completed.RootElement.GetProperty("ObservedArguments").EnumerateArray()
                    .Select(value => value.GetString()).SequenceEqual(arguments);
            }

            var unsafePermissionBlocked = false;
            try
            {
                var unsafeRecovery = new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = sessionId, WorkingDirectory = fixtureRoot };
                var unsafePlan = WindowsTerminalHandoff.CreatePlan(profile, terminalProfile.ProfileName, terminalProfile.CommandLine,
                    string.Empty, unsafeRecovery, true, true, fixtureRoot);
                WindowsTerminalHandoff.Discard(unsafePlan);
            }
            catch (InvalidOperationException) { unsafePermissionBlocked = true; }

            canceledPlan = WindowsTerminalHandoff.CreatePlan(profile, terminalProfile.ProfileName, terminalProfile.CommandLine,
                "cancel fixture", null, false, true, fixtureRoot);
            var canceledLaunch = await WindowsTerminalHandoff.LaunchAndWaitForStartAsync(canceledPlan, false, TimeSpan.FromSeconds(8));
            WindowsTerminalHandoff.Cancel(canceledPlan, "intentional smoke cancellation");
            await Task.Delay(250);
            var canceledHandoffNeverReleased = canceledLaunch.Success && File.Exists(canceledPlan.CancelPath)
                && !File.Exists(canceledPlan.ReadyPath) && !File.Exists(canceledPlan.CompletedPath);

            var childMarker = Path.Combine(fixtureRoot, "child-process.txt");
            var descendantStart = new ProcessStartInfo
            {
                FileName = plan.PowerShellExecutable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            descendantStart.ArgumentList.Add("-NoProfile");
            descendantStart.ArgumentList.Add("-Command");
            descendantStart.ArgumentList.Add($"$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 20 127.0.0.1 > nul' -PassThru; $child.Id | Set-Content -LiteralPath '{childMarker.Replace("'", "''")}'; Wait-Process -Id $child.Id");
            descendantFixture = Process.Start(descendantStart);
            var childDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(childMarker) && DateTime.UtcNow < childDeadline) await Task.Delay(50);
            var childProcessId = File.Exists(childMarker) && int.TryParse(File.ReadAllText(childMarker).Trim(), out var parsedChildProcessId)
                ? parsedChildProcessId
                : 0;
            var detectsLiveChildProcess = descendantFixture is not null && childProcessId > 0
                && ProcessTreeInspector.FindDescendantProcesses(descendantFixture.Id).Any(value => value.ProcessId == childProcessId);
            if (descendantFixture is { HasExited: false }) descendantFixture.Kill(true);
            descendantFixture?.WaitForExit(3000);

            var success = exactResumeArguments && revalidationDetectsChanges && terminalCommandIsStructured && transcriptIsSafeAndPersisted && queuePersisted
                && payloadIsDataOnly && waitsBeforeSourceRelease && releasePrepared && releaseCommitted && bootstrapCompleted
                && bootstrapForwardsArguments && unsafePermissionBlocked && canceledHandoffNeverReleased && detectsLiveChildProcess;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Windows Terminal handoff used a verified two-phase release and preserved exact Codex resume state.\nExactResumeArguments={exactResumeArguments}\nRevalidationDetectsChanges={revalidationDetectsChanges}\nTerminalCommandIsStructured={terminalCommandIsStructured}\nTranscriptIsSafeAndPersisted={transcriptIsSafeAndPersisted}\nQueuePersisted={queuePersisted}\nPayloadIsDataOnly={payloadIsDataOnly}\nExternalPowerShellVerified={launched.Success}\nWaitsBeforeSourceRelease={waitsBeforeSourceRelease}\nReleasePrepared={releasePrepared}\nReleaseCommitted={releaseCommitted}\nBootstrapCompleted={bootstrapCompleted}\nBootstrapForwardsArguments={bootstrapForwardsArguments}\nUnsafePermissionBlocked={unsafePermissionBlocked}\nCanceledHandoffNeverReleased={canceledHandoffNeverReleased}\nDetectsLiveChildProcess={detectsLiveChildProcess}");
            return success;
        }
        finally
        {
            try { if (descendantFixture is { HasExited: false }) descendantFixture.Kill(true); } catch { }
            descendantFixture?.Dispose();
            if (plan is not null) WindowsTerminalHandoff.Discard(plan);
            if (canceledPlan is not null) WindowsTerminalHandoff.Discard(canceledPlan);
            try { Directory.Delete(fixtureRoot, true); } catch { }
        }
    }
}
