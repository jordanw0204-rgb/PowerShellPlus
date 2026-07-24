using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PowerShellPlus.Native;

public partial class MainWindow : Window
{
    private enum EditorMode { Terminal, WorkspaceSession, Snippet, Automation }
    private sealed record RecoveryPaneCapture(string SessionId, string WorkingDirectory, string Output, int? RootProcessId);
    private const double WorkspaceSidebarWidth = 278;
    private readonly WindowsTerminalProfile terminalProfile;
    private readonly WorkspaceState state;
    private readonly Dictionary<string, TerminalPane> panes = [];
    private readonly ObservableCollection<SessionProfile> activeSessionTerminals = [];
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer automationTimer;
    private readonly DispatcherTimer recoveryTimer;
    private readonly DispatcherTimer workspaceSessionHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly bool automationMode;
    private readonly SessionRecoverySnapshot loadedRecovery;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private bool explicitShutdown;
    private bool shutdownComplete;
    private bool trayNoticeShown;
    private EditorMode editorMode;
    private object? editingValue;
    private object? selectedEditableValue;
    private TerminalPane? activePane;
    private TerminalSession? activeWorkspaceSession;
    private string? activeLayoutSizeKey;
    private bool workspaceSessionSelectionSync;
    private TerminalSession? workspaceSessionHoverCandidate;
    private TerminalSession? workspaceSessionHoverOrigin;
    private bool workspaceSessionHoverPreviewActive;
    private bool automationCheckRunning;
    private WindowsTerminalDragMonitor? windowsTerminalDragMonitor;
    private bool windowsTerminalImportRunning;
    private bool windowsTerminalDropVisible;
    private bool topmostBeforeWindowsTerminalDrop;
    private readonly object recoveryCaptureSync = new();
    private int recoveryCaptureInProgress;

    public MainWindow(bool automationMode = false)
    {
        this.automationMode = automationMode;
        terminalProfile = WindowsTerminalProfile.Load();
        state = WorkspaceStore.Load(terminalProfile);
        loadedRecovery = automationMode || !state.Settings.RestoreSessionsAfterRestart ? new SessionRecoverySnapshot() : SessionRecoveryStore.Load();
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart) ReconcileCodexRecovery();
        InitializeComponent();
        WorkspaceSessionList.ItemsSource = state.TerminalSessions;
        WorkspaceSessionTabs.ItemsSource = state.TerminalSessions;
        SessionList.ItemsSource = activeSessionTerminals;
        workspaceSessionHoverTimer.Tick += WorkspaceSessionHoverTimerTick;
        SnippetList.ItemsSource = state.Snippets;
        AutomationList.ItemsSource = state.Automations;
        InitializeAutomationTimeUi();
        PopulateSettingsUi();
        ApplyWorkspaceSidebarState(false);

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        automationTimer.Tick += async (_, _) => { RefreshAutomationCountdowns(); await CheckAutomationsAsync(); };
        automationTimer.Start();
        recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        recoveryTimer.Tick += async (_, _) => await CaptureRecoverySnapshotAsync();
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart) recoveryTimer.Start();

        foreach (var profile in state.Sessions) CreatePane(profile);
        var initialSession = state.TerminalSessions.FirstOrDefault(value => value.Id == state.ActiveTerminalSessionId)
            ?? state.TerminalSessions.First();
        SelectWorkspaceSession(initialSession.Id, false);
        UpdateStatus("Native Windows Terminal renderer ready");
        Closing += WindowClosing;
        SourceInitialized += (_, _) => InitializeWindowsTerminalImport();
        if (!automationMode) InitializeTrayIcon();
    }

    public void RestoreFromTray() => RestoreWindow(true);

    private void RestoreWindow(bool showInTaskbar)
    {
        if (shutdownComplete) return;
        ShowInTaskbar = showInTaskbar;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
        UpdateStatus("Live terminal sessions restored");
    }

    public void PrepareForShutdown() => explicitShutdown = true;

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!automationMode && !explicitShutdown && state.Settings.KeepSessionsRunningInTray)
        {
            CaptureRecoverySnapshot();
            e.Cancel = true;
            HideToTray();
            return;
        }
        CompleteShutdown();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (trayIcon is null || trayNoticeShown) return;
        trayNoticeShown = true;
        trayIcon.BalloonTipTitle = "PowerShellPlus is still running";
        trayIcon.BalloonTipText = "Your live PowerShell and Codex sessions are being kept open. Double-click the tray icon to return.";
        trayIcon.ShowBalloonTip(3500);
    }

    private void CompleteShutdown()
    {
        if (shutdownComplete) return;
        automationTimer.Stop();
        recoveryTimer.Stop();
        workspaceSessionHoverTimer.Stop();
        saveTimer.Stop();
        windowsTerminalDragMonitor?.Dispose();
        windowsTerminalDragMonitor = null;
        StopLanRemoteForShutdown();
        if (!automationMode) CaptureRecoverySnapshot();
        shutdownComplete = true;
        SaveNow();
        foreach (var pane in panes.Values) pane.Stop();
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open PowerShellPlus", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit and close sessions", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            explicitShutdown = true;
            Close();
        }));
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }
        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "PowerShellPlus — live sessions running",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
    }

    private void InitializeWindowsTerminalImport()
    {
        if (automationMode || windowsTerminalDragMonitor is not null) return;
        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            windowsTerminalDragMonitor = new WindowsTerminalDragMonitor(windowHandle, Dispatcher);
            windowsTerminalDragMonitor.HoverChanged += WindowsTerminalHoverChanged;
            windowsTerminalDragMonitor.Dropped += WindowsTerminalDropped;
        }
        catch (Exception exception)
        {
            LogNativeError("Windows Terminal drag monitor", exception);
            UpdateStatus("Windows Terminal drag import is unavailable");
        }
    }

    private void WindowsTerminalHoverChanged(IntPtr sourceWindow, bool isOverTarget, bool isArmed)
    {
        if (windowsTerminalImportRunning) return;
        if (!isOverTarget)
        {
            HideWindowsTerminalDropOverlay();
            return;
        }
        if (!windowsTerminalDropVisible)
        {
            windowsTerminalDropVisible = true;
            topmostBeforeWindowsTerminalDrop = Topmost;
            Topmost = true;
            TerminalHost.Visibility = Visibility.Hidden;
            WindowsTerminalDropOverlay.Visibility = Visibility.Visible;
        }
        WindowsTerminalDropTitle.Text = isArmed ? "Release to import Windows Terminal" : "Hold to import Windows Terminal";
        WindowsTerminalDropDetail.Text = isArmed
            ? "Release the window now. You will review tab and Codex matches before anything closes."
            : "Keep the window here for a moment, then release it.";
    }

    private void HideWindowsTerminalDropOverlay()
    {
        if (!windowsTerminalDropVisible) return;
        windowsTerminalDropVisible = false;
        WindowsTerminalDropOverlay.Visibility = Visibility.Collapsed;
        Topmost = topmostBeforeWindowsTerminalDrop;
        TerminalHost.Visibility = EditorOverlay.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    private async void WindowsTerminalDropped(IntPtr sourceWindow)
    {
        if (windowsTerminalImportRunning) return;
        windowsTerminalImportRunning = true;
        HideWindowsTerminalDropOverlay();
        try
        {
            UpdateStatus("Reading Windows Terminal tabs and scrollback…");
            var capture = await WindowsTerminalImportService.CaptureAsync(sourceWindow);
            CaptureRecoverySnapshot();
            var existingCodexIds = SessionRecoveryStore.Load().Sessions.Values
                .Where(value => CodexSessionLocator.IsSafeCodexId(value.CodexSessionId))
                .Select(value => value.CodexSessionId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in state.Sessions)
            {
                var marker = CodexLaunchStore.Load(profile.Id);
                if (CodexSessionLocator.IsSafeCodexId(marker?.SessionId)) existingCodexIds.Add(marker!.SessionId!);
            }
            var ownedCodexProcesses = panes.Values.Select(value => value.GetCodexProcessState()).Where(value => value.IsActive).ToList();
            var candidates = await Task.Run(() =>
            {
                foreach (var process in ownedCodexProcesses)
                {
                    var match = CodexActivityStore.FindActiveCliSession(process.ProcessId, process.StartedUtc, existingCodexIds);
                    if (match is not null) existingCodexIds.Add(match.SessionId);
                }
                return CodexActivityStore.FindAllActiveCliSessions(existingCodexIds);
            });
            var plan = WindowsTerminalImportPlanner.Create(capture, candidates);
            foreach (var row in plan.Rows.Where(value => value.SelectedSshChoice?.Connection is not null))
            {
                var choice = row.SelectedSshChoice!;
                if (choice.RemoteCodexProbe.Succeeded) continue;
                var connection = choice.Connection!;
                choice.RemoteCodexProbe = await Task.Run(() => RemoteCodexRecovery.ProbeImported(
                    connection.ConnectionArguments, row.Tab.RemoteWorkingDirectory, connection.ClientPort));
                row.RefreshProbeStatus();
            }

            TerminalHost.Visibility = Visibility.Hidden;
            var dialog = new WindowsTerminalImportDialog(plan) { Owner = this };
            var accepted = dialog.ShowDialog() == true;
            TerminalHost.Visibility = EditorOverlay.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            if (!accepted)
            {
                UpdateStatus("Windows Terminal import cancelled — source window unchanged");
                return;
            }

            var staged = new List<(WindowsTerminalImportRow Row, SessionProfile Profile, string? TranscriptFile)>();
            foreach (var row in plan.Rows)
            {
                var selected = row.SelectedChoice?.Session;
                var selectedSsh = row.SelectedSshChoice?.Connection;
                if (selected is not null && (!CodexSessionLocator.IsSafeCodexId(selected.SessionId)
                    || !CodexSessionLocator.IsSafeCodexPermissionState(selected.PermissionProfile, selected.SandboxMode, selected.ApprovalPolicy, selected.ApprovalsReviewer)
                    || !CodexSessionLocator.IsSafeCodexApprovalsReviewer(selected.ApprovalsReviewer)))
                    throw new InvalidOperationException($"Codex permissions for {row.Title} are not safe to restore.");
                if (selectedSsh is not null && !SshRecovery.TryNormalizeConnectionArguments(selectedSsh.ConnectionArguments, out _, out _))
                    throw new InvalidOperationException($"SSH connection for {row.Title} is not safe to restore.");
                var directory = selectedSsh is null ? selected?.WorkingDirectory ?? row.Tab.WorkingDirectory : DefaultSessionDirectory;
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) directory = DefaultSessionDirectory;
                var profile = new SessionProfile
                {
                    Name = row.Title,
                    CommandLine = DefaultSessionCommandLine,
                    WorkingDirectory = directory,
                    AutoStart = true
                };
                var transcriptFile = SessionRecoveryStore.SaveTranscript(profile.Id, row.Tab.Transcript);
                staged.Add((row, profile, transcriptFile));
            }

            UpdateStatus("Close the Windows Terminal confirmation, if shown, to finish importing…");
            if (!WindowsTerminalImportService.RequestClose(sourceWindow)
                || !await WindowsTerminalImportService.WaitForClosedAsync(sourceWindow, TimeSpan.FromSeconds(45)))
            {
                foreach (var item in staged) SessionRecoveryStore.DeleteSession(item.Profile.Id);
                PowerShellPlusDialog.ShowMessage(this, "The source Windows Terminal window stayed open, so nothing was imported. Close-confirmation may have been cancelled or the source may be elevated.", "Import cancelled safely", PowerShellPlusDialogKind.Information);
                UpdateStatus("Windows Terminal stayed open — import cancelled safely");
                return;
            }

            await Task.Delay(350);
            var sourceCodexIds = staged.Select(value => value.Row.SelectedChoice?.Session?.SessionId)
                .Where(CodexSessionLocator.IsSafeCodexId).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var codexExitDeadline = DateTime.UtcNow.AddSeconds(5);
            while (sourceCodexIds.Count > 0 && DateTime.UtcNow < codexExitDeadline)
            {
                var stillActive = await Task.Run(() => CodexActivityStore.FindAllActiveCliSessions()
                    .Any(value => sourceCodexIds.Contains(value.SessionId)));
                if (!stillActive) break;
                await Task.Delay(250);
            }
            var importedCodex = 0;
            var ready = new List<(SessionProfile Profile, SessionRecoveryEntry Recovery, string Transcript)>();
            foreach (var item in staged)
            {
                var original = item.Row.SelectedChoice?.Session;
                CodexSessionMatch? exact = null;
                if (original is not null)
                {
                    var latest = await Task.Run(() => CodexSessionLocator.FindSessionById(original.SessionId, requireTopLevelCli: true));
                    exact = latest is null ? original : latest with
                    {
                        Model = latest.Model ?? original.Model,
                        SandboxMode = latest.SandboxMode ?? original.SandboxMode,
                        ApprovalPolicy = latest.ApprovalPolicy ?? original.ApprovalPolicy,
                        PermissionProfile = latest.PermissionProfile ?? original.PermissionProfile,
                        ApprovalsReviewer = latest.ApprovalsReviewer ?? original.ApprovalsReviewer
                    };
                }
                var recovery = WindowsTerminalImportPlanner.CreateRecoveryEntry(item.Row, item.Profile.Id, item.TranscriptFile, exact);
                if (original is not null && !recovery.CodexWasActive)
                    throw new InvalidOperationException($"The exact Codex permission level for {item.Row.Title} could not be restored.");
                if (recovery.CodexWasActive) importedCodex++;
                if (recovery.RemoteCodexWasActive) importedCodex++;
                if (!string.IsNullOrWhiteSpace(recovery.WorkingDirectory) && Directory.Exists(recovery.WorkingDirectory))
                    item.Profile.WorkingDirectory = recovery.WorkingDirectory;
                ready.Add((item.Profile, recovery, item.Row.Tab.Transcript));
            }
            foreach (var item in ready)
            {
                loadedRecovery.Sessions[item.Profile.Id] = item.Recovery;
                AddTerminalToActiveSession(item.Profile);
            }
            SessionRecoveryStore.Save(loadedRecovery);
            SaveNow();
            foreach (var item in ready) CreatePane(item.Profile, item.Transcript);
            if (ready.FirstOrDefault().Profile is { } firstImported) SelectPane(firstImported.Id, false);
            ApplyLayout();
            UpdateStatus($"Imported {staged.Count} Windows Terminal tab{(staged.Count == 1 ? string.Empty : "s")}; resumed {importedCodex} Codex session{(importedCodex == 1 ? string.Empty : "s")} with saved permissions");
        }
        catch (Exception exception)
        {
            LogNativeError("Windows Terminal import", exception);
            TerminalHost.Visibility = EditorOverlay.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "Windows Terminal import failed", PowerShellPlusDialogKind.Error);
            UpdateStatus("Windows Terminal import failed — source was not changed unless its close was already confirmed");
        }
        finally
        {
            windowsTerminalImportRunning = false;
        }
    }

    private static void LogNativeError(string area, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] {area}: {exception}\n");
        }
        catch { }
    }

    private void CaptureRecoverySnapshot()
    {
        if (automationMode || !state.Settings.RestoreSessionsAfterRestart || shutdownComplete) return;
        CaptureRecoverySnapshotCore(CollectRecoveryPaneCaptures(), state.Settings.SaveTerminalTranscripts);
    }

    private async Task CaptureRecoverySnapshotAsync()
    {
        if (automationMode || windowsTerminalImportRunning || !state.Settings.RestoreSessionsAfterRestart || shutdownComplete) return;
        if (System.Threading.Interlocked.CompareExchange(ref recoveryCaptureInProgress, 1, 0) != 0) return;
        try
        {
            var captures = CollectRecoveryPaneCaptures();
            var saveTerminalTranscripts = state.Settings.SaveTerminalTranscripts;
            await Task.Run(() => CaptureRecoverySnapshotCore(captures, saveTerminalTranscripts));
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref recoveryCaptureInProgress, 0);
        }
    }

    private List<RecoveryPaneCapture> CollectRecoveryPaneCaptures() => panes.Values
        .Select(pane => new RecoveryPaneCapture(pane.Profile.Id, pane.Profile.WorkingDirectory, pane.GetOutput(), pane.GetRootProcessId()))
        .ToList();

    private void CaptureRecoverySnapshotCore(IReadOnlyList<RecoveryPaneCapture> captures, bool saveTerminalTranscripts)
    {
        lock (recoveryCaptureSync)
        {
            try
            {
                var previous = SessionRecoveryStore.Load();
                var snapshot = new SessionRecoverySnapshot();
                var usedCodexSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var capture in captures)
                {
                    previous.Sessions.TryGetValue(capture.SessionId, out var oldEntry);
                    var codex = capture.RootProcessId is int rootProcessId ? ProcessTreeInspector.FindCodexProcess(rootProcessId) : default;
                    var launch = CodexLaunchStore.Load(capture.SessionId);
                    if (!codex.IsActive && launch?.IsActive == true && launch.ShellProcessId is > 0)
                        codex = ProcessTreeInspector.FindCodexProcess(launch.ShellProcessId.Value);
                    var codexIsActive = codex.IsActive || launch?.IsActive == true;
                    var codexMatch = codex.IsActive
                        ? CodexActivityStore.FindActiveCliSession(codex.ProcessId, codex.StartedUtc, usedCodexSessionIds)
                        : null;
                    codexMatch ??= launch?.IsActive == true
                        ? CodexActivityStore.FindActiveCliSessionNearLaunch(launch.StartedUtc, usedCodexSessionIds)
                        : null;
                    codexMatch ??= launch?.IsActive == true && !CodexSessionLocator.IsSafeCodexId(launch.SessionId)
                        ? CodexSessionLocator.FindBestSession(launch.StartedUtc, launch.WorkingDirectory, usedCodexSessionIds)
                        : null;
                    codexMatch ??= codex.IsActive ? CodexSessionLocator.FindBestSession(codex.StartedUtc, null, usedCodexSessionIds) : null;
                    var codexSessionId = codexMatch?.SessionId;
                    var codexDirectory = codexMatch?.WorkingDirectory;
                    var codexModel = codexMatch?.Model;
                    var codexSandboxMode = codexMatch?.SandboxMode;
                    var codexApprovalPolicy = codexMatch?.ApprovalPolicy;
                    var codexPermissionProfile = codexMatch?.PermissionProfile;
                    var codexApprovalsReviewer = codexMatch?.ApprovalsReviewer;
                    if (codexSessionId is null && launch?.IsActive == true && CodexSessionLocator.IsSafeCodexId(launch.SessionId))
                    {
                        codexSessionId = launch.SessionId;
                        codexDirectory = launch.WorkingDirectory;
                        codexModel = launch.Model;
                        codexSandboxMode = launch.SandboxMode;
                        codexApprovalPolicy = launch.ApprovalPolicy;
                        codexPermissionProfile = launch.PermissionProfile;
                        codexApprovalsReviewer = launch.ApprovalsReviewer;
                    }
                    if (codexSessionId is null && codexIsActive && launch is null && oldEntry?.CodexWasActive == true && CodexSessionLocator.IsSafeCodexId(oldEntry.CodexSessionId))
                    {
                        codexSessionId = oldEntry.CodexSessionId;
                        codexDirectory = oldEntry.WorkingDirectory;
                        codexModel = oldEntry.CodexModel;
                        codexSandboxMode = oldEntry.CodexSandboxMode;
                        codexApprovalPolicy = oldEntry.CodexApprovalPolicy;
                        codexPermissionProfile = oldEntry.CodexPermissionProfile;
                        codexApprovalsReviewer = oldEntry.CodexApprovalsReviewer;
                    }
                    if (codexMatch is not null && launch?.IsActive == true) CodexLaunchStore.Confirm(launch, codexMatch);
                    if (codexSessionId is not null)
                    {
                        usedCodexSessionIds.Add(codexSessionId);
                        codexModel = (codexMatch is null ? CodexSessionLocator.FindLatestModel(codexSessionId)?.Model : codexMatch.Model)
                            ?? codexModel ?? oldEntry?.CodexModel;
                        var latestPermissions = codexMatch is not null && CodexSessionLocator.IsSafeCodexPermissionState(codexMatch.PermissionProfile, codexMatch.SandboxMode, codexMatch.ApprovalPolicy, codexMatch.ApprovalsReviewer)
                            ? new CodexSessionPermissions(codexMatch.SandboxMode, codexMatch.ApprovalPolicy!, codexMatch.FileModifiedUtc, codexMatch.PermissionProfile, codexMatch.ApprovalsReviewer)
                            : CodexSessionLocator.FindLatestPermissions(codexSessionId);
                        if (latestPermissions is not null)
                        {
                            codexSandboxMode = latestPermissions.SandboxMode;
                            codexApprovalPolicy = latestPermissions.ApprovalPolicy;
                            codexPermissionProfile = latestPermissions.PermissionProfile;
                            codexApprovalsReviewer = latestPermissions.ApprovalsReviewer;
                        }
                        else if (!CodexSessionLocator.IsSafeCodexPermissionState(codexPermissionProfile, codexSandboxMode, codexApprovalPolicy, codexApprovalsReviewer)
                            && CodexSessionLocator.IsSafeCodexPermissionState(oldEntry?.CodexPermissionProfile, oldEntry?.CodexSandboxMode, oldEntry?.CodexApprovalPolicy, oldEntry?.CodexApprovalsReviewer))
                        {
                            codexSandboxMode = oldEntry!.CodexSandboxMode;
                            codexApprovalPolicy = oldEntry.CodexApprovalPolicy;
                            codexPermissionProfile = oldEntry.CodexPermissionProfile;
                            codexApprovalsReviewer = oldEntry.CodexApprovalsReviewer;
                        }
                    }
                    var sshLaunch = SshLaunchStore.Load(capture.SessionId);
                    var sshProcess = capture.RootProcessId is int sshRootProcessId ? ProcessTreeInspector.FindSshProcess(sshRootProcessId) : default;
                    if (!sshProcess.IsActive && sshLaunch?.IsActive == true && sshLaunch.ShellProcessId is > 0)
                        sshProcess = ProcessTreeInspector.FindSshProcess(sshLaunch.ShellProcessId.Value);
                    var sshIsActive = sshLaunch?.IsActive == true && sshProcess.IsActive;
                    var keepPendingSshRecovery = SshRecovery.ShouldKeepPendingRecovery(oldEntry, sshLaunch, sshProcess.IsActive);
                    var sshRestorable = sshIsActive || keepPendingSshRecovery;
                    var sshArguments = sshIsActive ? sshLaunch!.ConnectionArguments
                        : keepPendingSshRecovery ? oldEntry!.SshConnectionArguments : [];
                    var samePreviousSsh = sshRestorable && oldEntry?.SshWasActive == true
                        && oldEntry.SshConnectionArguments.SequenceEqual(sshArguments, StringComparer.Ordinal);
                    var previousHermes = samePreviousSsh
                        ? new HermesRecoveryState(oldEntry!.HermesWasActive, oldEntry.HermesSessionId, oldEntry.HermesModel, oldEntry.HermesUseTui)
                        : default;
                    var hermes = sshIsActive ? HermesRecovery.Detect(capture.Output, previousHermes)
                        : keepPendingSshRecovery ? previousHermes : default;
                    var previousRemoteCodex = samePreviousSsh
                        ? new RemoteCodexRecoveryState(oldEntry!.RemoteCodexWasActive, oldEntry.RemoteCodexSessionId,
                            oldEntry.RemoteCodexWorkingDirectory, oldEntry.RemoteCodexModel, oldEntry.RemoteCodexSandboxMode,
                            oldEntry.RemoteCodexApprovalPolicy, oldEntry.RemoteCodexPermissionProfile, oldEntry.RemoteCodexApprovalsReviewer)
                        : default;
                    var remoteCodexProbe = sshIsActive && !hermes.WasActive
                        ? RemoteCodexRecovery.Probe(capture.SessionId, sshArguments)
                        : default;
                    var remoteCodex = remoteCodexProbe.Succeeded ? remoteCodexProbe.State
                        : samePreviousSsh ? previousRemoteCodex : default;
                    var preserveTranscript = SshRecovery.ShouldPreserveTranscript(oldEntry, sshLaunch, sshProcess.IsActive, capture.Output);
                    var transcriptFile = saveTerminalTranscripts
                        ? preserveTranscript ? oldEntry?.TranscriptFile
                            : SessionRecoveryStore.SaveTranscript(capture.SessionId, capture.Output) ?? oldEntry?.TranscriptFile
                        : null;
                    var hasSafePermissionState = CodexSessionLocator.IsSafeCodexPermissionState(codexPermissionProfile, codexSandboxMode, codexApprovalPolicy, codexApprovalsReviewer);
                    snapshot.Sessions[capture.SessionId] = new SessionRecoveryEntry
                    {
                        SessionId = capture.SessionId,
                        WorkingDirectory = codexDirectory ?? (codexIsActive && launch is not null ? launch.WorkingDirectory
                            : sshIsActive ? sshLaunch!.WorkingDirectory
                            : keepPendingSshRecovery ? oldEntry!.WorkingDirectory : capture.WorkingDirectory),
                        TranscriptFile = transcriptFile,
                        CodexWasActive = codexIsActive,
                        CodexSessionId = codexSessionId,
                        CodexModel = CodexSessionLocator.IsSafeCodexModel(codexModel) ? codexModel : null,
                        CodexSandboxMode = hasSafePermissionState && CodexSessionLocator.IsSafeCodexSandboxMode(codexSandboxMode) ? codexSandboxMode : null,
                        CodexApprovalPolicy = hasSafePermissionState ? codexApprovalPolicy : null,
                        CodexPermissionProfile = hasSafePermissionState && CodexSessionLocator.IsSafeCodexPermissionProfile(codexPermissionProfile) ? codexPermissionProfile : null,
                        CodexApprovalsReviewer = hasSafePermissionState && CodexSessionLocator.IsSafeCodexApprovalsReviewer(codexApprovalsReviewer) ? codexApprovalsReviewer : null,
                        SshWasActive = sshRestorable,
                        SshConnectionArguments = sshArguments,
                        HermesWasActive = hermes.WasActive,
                        HermesSessionId = hermes.SessionId,
                        HermesModel = hermes.Model,
                        HermesUseTui = hermes.UseTui,
                        RemoteCodexWasActive = remoteCodex.WasActive,
                        RemoteCodexSessionId = remoteCodex.SessionId,
                        RemoteCodexWorkingDirectory = remoteCodex.WorkingDirectory,
                        RemoteCodexModel = remoteCodex.Model,
                        RemoteCodexSandboxMode = remoteCodex.SandboxMode,
                        RemoteCodexApprovalPolicy = remoteCodex.ApprovalPolicy,
                        RemoteCodexPermissionProfile = remoteCodex.PermissionProfile,
                        RemoteCodexApprovalsReviewer = remoteCodex.ApprovalsReviewer,
                        CapturedUtc = DateTime.UtcNow
                    };
                }
                SessionRecoveryStore.Save(snapshot);
            }
            catch (Exception exception)
            {
                LogNativeError("Recovery snapshot", exception);
            }
        }
    }

    private void ReconcileCodexRecovery()
    {
        var changed = false;
        var usedCodexSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in state.Sessions)
        {
            loadedRecovery.Sessions.TryGetValue(profile.Id, out var entry);
            if (entry?.CodexWasActive == true && CodexSessionLocator.IsSafeCodexId(entry.CodexSessionId))
            {
                usedCodexSessionIds.Add(entry.CodexSessionId!);
                var latestModel = CodexSessionLocator.FindLatestModel(entry.CodexSessionId);
                if (latestModel is not null && !string.Equals(entry.CodexModel, latestModel.Model, StringComparison.Ordinal))
                {
                    entry.CodexModel = latestModel.Model;
                    changed = true;
                }
                var latestPermissions = CodexSessionLocator.FindLatestPermissions(entry.CodexSessionId);
                if (latestPermissions is not null && (!string.Equals(entry.CodexSandboxMode, latestPermissions.SandboxMode, StringComparison.Ordinal)
                    || !string.Equals(entry.CodexApprovalPolicy, latestPermissions.ApprovalPolicy, StringComparison.Ordinal)
                    || !string.Equals(entry.CodexPermissionProfile, latestPermissions.PermissionProfile, StringComparison.Ordinal)
                    || !string.Equals(entry.CodexApprovalsReviewer, latestPermissions.ApprovalsReviewer, StringComparison.Ordinal)))
                {
                    entry.CodexSandboxMode = latestPermissions.SandboxMode;
                    entry.CodexApprovalPolicy = latestPermissions.ApprovalPolicy;
                    entry.CodexPermissionProfile = latestPermissions.PermissionProfile;
                    entry.CodexApprovalsReviewer = latestPermissions.ApprovalsReviewer;
                    changed = true;
                }
            }

            var launch = CodexLaunchStore.Load(profile.Id);
            if (launch?.IsActive != true) continue;

            var match = !CodexSessionLocator.IsSafeCodexId(launch.SessionId)
                ? CodexSessionLocator.FindBestSession(launch.StartedUtc, launch.WorkingDirectory, usedCodexSessionIds)
                : null;
            if (match is not null) CodexLaunchStore.Confirm(launch, match);
            var sessionId = match?.SessionId ?? launch.SessionId;
            if (!CodexSessionLocator.IsSafeCodexId(sessionId)) continue;

            if (entry is not null && CodexSessionLocator.IsSafeCodexId(entry.CodexSessionId))
                usedCodexSessionIds.Remove(entry.CodexSessionId!);
            usedCodexSessionIds.Add(sessionId!);
            entry ??= new SessionRecoveryEntry { SessionId = profile.Id };
            entry.CodexWasActive = true;
            entry.CodexSessionId = sessionId;
            entry.CodexModel = CodexSessionLocator.FindLatestModel(sessionId)?.Model ?? match?.Model ?? launch.Model ?? entry.CodexModel;
            var permissions = CodexSessionLocator.FindLatestPermissions(sessionId);
            if (permissions is not null)
            {
                entry.CodexSandboxMode = permissions.SandboxMode;
                entry.CodexApprovalPolicy = permissions.ApprovalPolicy;
                entry.CodexPermissionProfile = permissions.PermissionProfile;
                entry.CodexApprovalsReviewer = permissions.ApprovalsReviewer;
            }
            else if (CodexSessionLocator.IsSafeCodexPermissionState(match?.PermissionProfile, match?.SandboxMode, match?.ApprovalPolicy, match?.ApprovalsReviewer))
            {
                entry.CodexSandboxMode = match!.SandboxMode;
                entry.CodexApprovalPolicy = match.ApprovalPolicy;
                entry.CodexPermissionProfile = match.PermissionProfile;
                entry.CodexApprovalsReviewer = match.ApprovalsReviewer;
            }
            else if (CodexSessionLocator.IsSafeCodexPermissionState(launch.PermissionProfile, launch.SandboxMode, launch.ApprovalPolicy, launch.ApprovalsReviewer))
            {
                entry.CodexSandboxMode = launch.SandboxMode;
                entry.CodexApprovalPolicy = launch.ApprovalPolicy;
                entry.CodexPermissionProfile = launch.PermissionProfile;
                entry.CodexApprovalsReviewer = launch.ApprovalsReviewer;
            }
            entry.WorkingDirectory = match?.WorkingDirectory ?? launch.WorkingDirectory;
            entry.CapturedUtc = DateTime.UtcNow;
            loadedRecovery.Sessions[profile.Id] = entry;
            changed = true;
        }
        if (changed) SessionRecoveryStore.Save(loadedRecovery);
    }

    private TerminalAppearance EffectiveAppearance()
    {
        var settings = state.Settings;
        var fontFace = string.IsNullOrWhiteSpace(settings.FontFace) ? terminalProfile.FontFace : settings.FontFace.Trim();
        var fontSize = Math.Clamp(settings.FontSize ?? terminalProfile.FontSize, 8, 32);
        var theme = terminalProfile.Theme;
        theme.CursorStyle = (settings.CursorStyle, settings.CursorBlink) switch
        {
            ("Block", true) => Microsoft.Terminal.Wpf.CursorStyle.BlinkingBlock,
            ("Block", false) => Microsoft.Terminal.Wpf.CursorStyle.SteadyBlock,
            ("Underline", true) => Microsoft.Terminal.Wpf.CursorStyle.BlinkingUnderline,
            ("Underline", false) => Microsoft.Terminal.Wpf.CursorStyle.SteadyUnderline,
            (_, false) => Microsoft.Terminal.Wpf.CursorStyle.SteadyBar,
            _ => Microsoft.Terminal.Wpf.CursorStyle.BlinkingBar
        };
        return new TerminalAppearance(terminalProfile.ProfileName, fontFace, fontSize, theme);
    }

    private string DefaultSessionCommandLine => string.IsNullOrWhiteSpace(state.Settings.DefaultCommandLine) ? terminalProfile.CommandLine : state.Settings.DefaultCommandLine.Trim();
    private string DefaultSessionDirectory => !string.IsNullOrWhiteSpace(state.Settings.DefaultWorkingDirectory) && Directory.Exists(state.Settings.DefaultWorkingDirectory)
        ? state.Settings.DefaultWorkingDirectory
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private ModifierKeys SendToAllModifier => state.Settings.SendToAllModifier switch
    {
        "Ctrl" => ModifierKeys.Control,
        "Alt" => ModifierKeys.Alt,
        _ => ModifierKeys.Shift
    };

    private async Task<bool> SendCommandToAllAsync(string command)
    {
        var targets = panes.Values.ToList();
        if (targets.Count == 0) return false;
        var results = await Task.WhenAll(targets.Select(pane => pane.SendCommandAsync(command)));
        var accepted = results.Count(value => value);
        UpdateStatus(accepted == targets.Count
            ? $"Command sent to all {accepted} terminals"
            : $"Command reached {accepted} of {targets.Count} terminals");
        return accepted == targets.Count;
    }

    private void CreatePane(SessionProfile profile, string? recoveredOutputOverride = null)
    {
        loadedRecovery.Sessions.TryGetValue(profile.Id, out var recovery);
        var previousOutput = recoveredOutputOverride ?? (state.Settings.SaveTerminalTranscripts ? SessionRecoveryStore.ReadTranscript(recovery) : string.Empty);
        var pane = new TerminalPane(profile, EffectiveAppearance(), recovery, previousOutput,
            () => state.Snippets, ScheduleSave, SendCommandToAllAsync,
            () => state.Settings.SendToAllModifierEnabled, () => SendToAllModifier);
        // A native terminal click already gives its HWND keyboard focus. Only
        // update application selection here so WPF does not steal that focus.
        pane.Activated += (_, _) => SelectPane(profile.Id, false);
        pane.CloseRequested += (_, _) => RemoveSession(profile);
        pane.EditRequested += (_, _) => OpenSessionEditor(profile);
        pane.DetachRequested += (_, _) => DetachSessionToWindowsTerminal(profile, pane);
        panes[profile.Id] = pane;
    }

    private void SelectPane(string sessionId, bool focus = true)
    {
        if (!panes.TryGetValue(sessionId, out var pane)) return;
        var owner = state.TerminalSessions.FirstOrDefault(value => value.TerminalIds.Contains(sessionId, StringComparer.Ordinal));
        if (owner is not null && owner != activeWorkspaceSession) SelectWorkspaceSession(owner.Id, false);
        activePane = pane;
        state.ActiveSessionId = sessionId;
        if (activeWorkspaceSession is not null) activeWorkspaceSession.ActiveTerminalId = sessionId;
        foreach (var value in panes.Values) value.SetActive(value == pane);
        SessionList.SelectedItem = pane.Profile;
        if (activeWorkspaceSession?.Layout == "Focus") ApplyLayout();
        if (focus) pane.Focus();
        ScheduleSave();
    }

    private void SelectWorkspaceSession(string sessionId, bool focus = true)
    {
        var selected = state.TerminalSessions.FirstOrDefault(value => value.Id == sessionId);
        if (selected is null) return;
        CaptureLayoutSizing();
        activeLayoutSizeKey = null;
        DisplayWorkspaceSession(selected, true, focus);
    }

    private void DisplayWorkspaceSession(TerminalSession selected, bool commit, bool focus)
    {
        activeWorkspaceSession = selected;
        if (commit) state.ActiveTerminalSessionId = selected.Id;
        activeSessionTerminals.Clear();
        foreach (var terminalId in selected.TerminalIds)
            if (state.Sessions.FirstOrDefault(value => value.Id == terminalId) is { } profile)
                activeSessionTerminals.Add(profile);

        if (commit)
        {
            workspaceSessionSelectionSync = true;
            WorkspaceSessionList.SelectedItem = selected;
            WorkspaceSessionTabs.SelectedItem = selected;
            workspaceSessionSelectionSync = false;
        }

        var terminalIdToActivate = selected.ActiveTerminalId;
        if (terminalIdToActivate is null || !selected.TerminalIds.Contains(terminalIdToActivate, StringComparer.Ordinal))
            terminalIdToActivate = selected.TerminalIds.FirstOrDefault();
        activePane = terminalIdToActivate is not null && panes.TryGetValue(terminalIdToActivate, out var pane) ? pane : null;
        if (commit)
        {
            selected.ActiveTerminalId = activePane?.Profile.Id;
            state.ActiveSessionId = selected.ActiveTerminalId;
        }
        foreach (var value in panes.Values) value.SetActive(value == activePane);
        SessionList.SelectedItem = activePane?.Profile;
        ApplyLayout(commit);
        if (focus) activePane?.Focus();
        UpdateStatus(commit ? $"{selected.Name} · {selected.Subtitle}" : $"Previewing {selected.Name} · move away to return");
        if (commit) ScheduleSave();
    }

    private void ApplyWorkspaceSidebarState(bool persist)
    {
        var expanded = state.WorkspaceSidebarExpanded;
        WorkspaceSidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceSidebarColumn.Width = new GridLength(expanded ? WorkspaceSidebarWidth : 0);
        WorkspaceSidebarToggle.Content = expanded ? "‹" : "›";
        WorkspaceSidebarToggle.ToolTip = expanded ? "Collapse workspace sidebar" : "Expand workspace sidebar";
        TerminalHost.InvalidateMeasure();
        TerminalHost.InvalidateArrange();
        Dispatcher.BeginInvoke(() => TerminalHost.UpdateLayout(), DispatcherPriority.Render);
        if (!persist) return;
        ScheduleSave();
        UpdateStatus(expanded ? "Workspace sidebar expanded" : "Workspace sidebar collapsed — terminals resized");
    }

    private void SetWorkspaceSidebarExpanded(bool expanded, bool persist)
    {
        state.WorkspaceSidebarExpanded = expanded;
        ApplyWorkspaceSidebarState(persist);
    }

    private void ApplyLayout(bool persist = true)
    {
        CaptureLayoutSizing();
        TerminalHost.Children.Clear(); TerminalHost.RowDefinitions.Clear(); TerminalHost.ColumnDefinitions.Clear();
        var workspaceSession = activeWorkspaceSession;
        var ordered = workspaceSession?.TerminalIds.Where(value => panes.ContainsKey(value)).Select(value => panes[value]).ToList() ?? [];
        activeLayoutSizeKey = null;
        if (ordered.Count == 0) { UpdateCounts(); return; }
        foreach (var pane in ordered) pane.Visibility = Visibility.Visible;

        if (workspaceSession?.Layout == "Focus")
        {
            TerminalHost.RowDefinitions.Add(new RowDefinition()); TerminalHost.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (var pane in ordered) if (pane != activePane) pane.Visibility = Visibility.Collapsed;
            if (activePane is not null) TerminalHost.Children.Add(activePane);
        }
        else
        {
            int columns, rows;
            if (workspaceSession?.Layout == "Rows") { columns = 1; rows = ordered.Count; }
            else if (workspaceSession?.Layout == "Columns") { columns = ordered.Count; rows = 1; }
            else { columns = (int)Math.Ceiling(Math.Sqrt(ordered.Count)); rows = (int)Math.Ceiling((double)ordered.Count / columns); }
            activeLayoutSizeKey = $"{workspaceSession?.Layout ?? "Grid"}:{ordered.Count}:{rows}x{columns}";
            PaneLayoutSizing? savedSizing = null;
            workspaceSession?.LayoutSizes.TryGetValue(activeLayoutSizeKey, out savedSizing);
            for (var index = 0; index < columns; index++)
            {
                var weight = savedSizing?.Columns.Count == columns ? Math.Max(1, savedSizing.Columns[index]) : 1;
                TerminalHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star), MinWidth = 180 });
                if (index < columns - 1) TerminalHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            }
            for (var index = 0; index < rows; index++)
            {
                var weight = savedSizing?.Rows.Count == rows ? Math.Max(1, savedSizing.Rows[index]) : 1;
                TerminalHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(weight, GridUnitType.Star), MinHeight = 120 });
                if (index < rows - 1) TerminalHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            }
            for (var index = 0; index < ordered.Count; index++)
            {
                var pane = ordered[index]; Grid.SetColumn(pane, (index % columns) * 2); Grid.SetRow(pane, (index / columns) * 2); TerminalHost.Children.Add(pane);
            }
            for (var column = 0; column < columns - 1; column++)
            {
                var splitter = CreateGridSplitter(GridResizeDirection.Columns);
                Grid.SetColumn(splitter, column * 2 + 1); Grid.SetRowSpan(splitter, TerminalHost.RowDefinitions.Count);
                TerminalHost.Children.Add(splitter);
            }
            for (var row = 0; row < rows - 1; row++)
            {
                var splitter = CreateGridSplitter(GridResizeDirection.Rows);
                Grid.SetRow(splitter, row * 2 + 1); Grid.SetColumnSpan(splitter, TerminalHost.ColumnDefinitions.Count);
                TerminalHost.Children.Add(splitter);
            }
        }
        UpdateCounts();
        if (persist) ScheduleSave();
    }

    private GridSplitter CreateGridSplitter(GridResizeDirection direction)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = direction,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(49, 50, 68)),
            ShowsPreview = false,
            Cursor = direction == GridResizeDirection.Columns ? Cursors.SizeWE : Cursors.SizeNS
        };
        splitter.DragCompleted += (_, _) => { CaptureLayoutSizing(); ScheduleSave(); UpdateStatus("Pane sizes saved"); };
        Panel.SetZIndex(splitter, 10);
        return splitter;
    }

    private void CaptureLayoutSizing()
    {
        if (activeLayoutSizeKey is null || TerminalHost.RowDefinitions.Count == 0 || TerminalHost.ColumnDefinitions.Count == 0) return;
        if (activeWorkspaceSession is null) return;
        activeWorkspaceSession.LayoutSizes[activeLayoutSizeKey] = new PaneLayoutSizing
        {
            Rows = TerminalHost.RowDefinitions.Where((_, index) => index % 2 == 0).Select(value => Math.Max(1, value.ActualHeight)).ToList(),
            Columns = TerminalHost.ColumnDefinitions.Where((_, index) => index % 2 == 0).Select(value => Math.Max(1, value.ActualWidth)).ToList()
        };
    }

    private void SetLayout(string layout) { if (activeWorkspaceSession is null) return; CaptureLayoutSizing(); activeWorkspaceSession.Layout = layout; ApplyLayout(); UpdateStatus($"{layout} layout in {activeWorkspaceSession.Name} - drag the dividers to resize terminals"); }

    private void OpenSessionEditor(SessionProfile? profile)
    {
        editorMode = EditorMode.Terminal; editingValue = profile;
        EditorTitle.Text = profile is null ? "New native terminal" : "Edit terminal";
        SessionNameEdit.Text = profile?.Name ?? terminalProfile.ProfileName;
        SessionCommandEdit.Text = profile?.CommandLine ?? DefaultSessionCommandLine;
        SessionDirectoryEdit.Text = profile?.WorkingDirectory ?? DefaultSessionDirectory;
        SessionAutoStartEdit.IsChecked = profile?.AutoStart ?? true;
        ShowEditor(SessionEditor);
    }

    private void OpenSnippetEditor(CommandSnippet? snippet)
    {
        editorMode = EditorMode.Snippet; editingValue = snippet; EditorTitle.Text = snippet is null ? "Save command" : "Edit command";
        SnippetNameEdit.Text = snippet?.Name ?? string.Empty; SnippetCategoryEdit.Text = snippet?.Category ?? "General"; SnippetCommandEdit.Text = snippet?.Command ?? string.Empty; SnippetQuickAccessEdit.IsChecked = snippet?.ShowInQuickAccess ?? false;
        ShowEditor(SnippetEditor);
    }

    private void OpenAutomationEditor(AutomationRule? rule)
    {
        editorMode = EditorMode.Automation; editingValue = rule; EditorTitle.Text = rule is null ? "New automation" : "Edit automation";
        AutomationNameEdit.Text = rule?.Name ?? string.Empty; AutomationCommandEdit.Text = rule?.Command ?? string.Empty;
        var targets = new ObservableCollection<SessionProfile>(state.Sessions); targets.Insert(0, new SessionProfile { Id = "*", Name = "All terminals", CommandLine = string.Empty });
        AutomationTargetEdit.ItemsSource = targets; AutomationTargetEdit.SelectedValue = rule?.TargetSessionId ?? "*";
        AutomationTypeEdit.SelectedIndex = rule?.ScheduleType switch { "Daily" => 1, "Once" => 2, _ => 0 };
        AutomationValueEdit.Text = (rule?.IntervalMinutes ?? 60).ToString(CultureInfo.InvariantCulture);
        var exactTime = TimeSpan.TryParseExact(rule?.DailyTime ?? "09:00", @"hh\:mm", CultureInfo.InvariantCulture, out var parsedTime) ? parsedTime : TimeSpan.FromHours(9);
        var hour = exactTime.Hours % 12; if (hour == 0) hour = 12;
        AutomationHourEdit.SelectedItem = hour;
        AutomationMinuteEdit.SelectedItem = exactTime.Minutes.ToString("00", CultureInfo.InvariantCulture);
        AutomationAmPmEdit.SelectedIndex = exactTime.Hours >= 12 ? 1 : 0;
        AutomationDateEdit.SelectedDate = DateTime.TryParseExact(rule?.ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTime.Today;
        AutomationEnabledEdit.IsChecked = rule?.Enabled ?? true;
        ShowEditor(AutomationEditor); UpdateAutomationScheduleEditor();
    }

    private void InitializeAutomationTimeUi()
    {
        AutomationHourEdit.ItemsSource = Enumerable.Range(1, 12).ToList();
        AutomationMinuteEdit.ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("00", CultureInfo.InvariantCulture)).ToList();
    }

    private void ShowEditor(FrameworkElement panel)
    {
        SessionEditor.Visibility = Visibility.Collapsed; WorkspaceSessionEditor.Visibility = Visibility.Collapsed; SnippetEditor.Visibility = Visibility.Collapsed; AutomationEditor.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible; EditorOverlay.Visibility = Visibility.Visible;
        // The terminal panes are native HwndHost windows that always paint above WPF
        // content (airspace), so they must be hidden while the editor overlay is open.
        TerminalHost.Visibility = Visibility.Hidden;
    }

    private void HideEditor()
    {
        EditorOverlay.Visibility = Visibility.Collapsed;
        TerminalHost.Visibility = Visibility.Visible;
    }

    private async Task<bool> ApplyTerminalEditAsync(SessionProfile profile, string name, string commandLine, string workingDirectory, bool autoStart)
    {
        var restartRequired = !string.Equals(profile.CommandLine, commandLine, StringComparison.Ordinal)
            || !PathsEqual(profile.WorkingDirectory, workingDirectory);
        profile.Name = name;
        profile.CommandLine = commandLine;
        profile.WorkingDirectory = workingDirectory;
        profile.AutoStart = autoStart;
        if (!panes.TryGetValue(profile.Id, out var pane)) return restartRequired;
        if (restartRequired)
        {
            pane.ApplyProfile(profile);
            await pane.RestartAsync();
        }
        else pane.RefreshProfileDisplay(profile);
        SessionList.Items.Refresh();
        return restartRequired;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }

    private async void SaveEditorClick(object sender, RoutedEventArgs e)
    {
        if (editorMode == EditorMode.Terminal)
        {
            if (string.IsNullOrWhiteSpace(SessionNameEdit.Text) || string.IsNullOrWhiteSpace(SessionCommandEdit.Text) || !Directory.Exists(SessionDirectoryEdit.Text)) { UpdateStatus("Session fields are incomplete"); return; }
            if (editingValue is SessionProfile existing)
            {
                await ApplyTerminalEditAsync(existing, SessionNameEdit.Text.Trim(), SessionCommandEdit.Text.Trim(), SessionDirectoryEdit.Text.Trim(), SessionAutoStartEdit.IsChecked == true);
            }
            else
            {
                var created = new SessionProfile { Name = SessionNameEdit.Text.Trim(), CommandLine = SessionCommandEdit.Text.Trim(), WorkingDirectory = SessionDirectoryEdit.Text.Trim(), AutoStart = SessionAutoStartEdit.IsChecked == true };
                AddTerminalToActiveSession(created); CreatePane(created); SelectPane(created.Id, false); ApplyLayout();
            }
        }
        else if (editorMode == EditorMode.WorkspaceSession)
        {
            if (editingValue is not TerminalSession session || string.IsNullOrWhiteSpace(WorkspaceSessionNameEdit.Text)) return;
            session.Name = WorkspaceSessionNameEdit.Text.Trim();
            RefreshWorkspaceSessionViews();
        }
        else if (editorMode == EditorMode.Snippet)
        {
            if (string.IsNullOrWhiteSpace(SnippetNameEdit.Text) || string.IsNullOrWhiteSpace(SnippetCommandEdit.Text)) return;
            var value = editingValue as CommandSnippet ?? new CommandSnippet(); value.Name = SnippetNameEdit.Text.Trim(); value.Category = string.IsNullOrWhiteSpace(SnippetCategoryEdit.Text) ? "General" : SnippetCategoryEdit.Text.Trim(); value.Command = SnippetCommandEdit.Text.Trim(); value.ShowInQuickAccess = SnippetQuickAccessEdit.IsChecked == true;
            if (editingValue is null) state.Snippets.Add(value); else SnippetList.Items.Refresh();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(AutomationNameEdit.Text) || string.IsNullOrWhiteSpace(AutomationCommandEdit.Text)) return;
            var value = editingValue as AutomationRule ?? new AutomationRule();
            var previousScheduleType = value.ScheduleType; var previousTime = value.DailyTime;
            value.Name = AutomationNameEdit.Text.Trim(); value.Command = AutomationCommandEdit.Text.Trim(); value.TargetSessionId = AutomationTargetEdit.SelectedValue?.ToString() ?? "*"; value.ScheduleType = AutomationTypeEdit.SelectedIndex switch { 1 => "Daily", 2 => "Once", _ => "Interval" }; value.Enabled = AutomationEnabledEdit.IsChecked == true;
            if (value.ScheduleType == "Interval")
            {
                if (!int.TryParse(AutomationValueEdit.Text, out var minutes)) { UpdateStatus("Enter a valid interval in minutes"); return; }
                value.IntervalMinutes = Math.Max(1, minutes);
                if (previousScheduleType != "Interval") value.LastRunUtc = DateTime.UtcNow;
            }
            else
            {
                if (AutomationHourEdit.SelectedItem is not int hour || AutomationMinuteEdit.SelectedItem is not string minuteText || !int.TryParse(minuteText, out var minute)) { UpdateStatus("Choose an exact time"); return; }
                var hour24 = hour % 12 + (AutomationAmPmEdit.SelectedIndex == 1 ? 12 : 0);
                value.DailyTime = $"{hour24:00}:{minute:00}";
                if (value.ScheduleType == "Daily" && (editingValue is null || previousScheduleType != "Daily" || previousTime != value.DailyTime)) value.LastRunUtc = DateTime.UtcNow.AddDays(-1);
                if (value.ScheduleType == "Once")
                {
                    if (AutomationDateEdit.SelectedDate is not DateTime selectedDate) { UpdateStatus("Choose a run date"); return; }
                    value.ScheduledDate = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    value.HasRun = false;
                }
            }
            if (editingValue is null) state.Automations.Add(value); else value.NotifyDisplayChanged();
        }
        HideEditor(); ScheduleSave(); UpdateCounts();
    }

    private bool RemoveSession(SessionProfile profile, bool alreadyConfirmed = false, bool stopPane = true)
    {
        if (!panes.TryGetValue(profile.Id, out var pane)) return false;
        if (!alreadyConfirmed && state.Settings.ConfirmBeforeRemove && !PowerShellPlusDialog.Confirm(this,
                $"Remove {profile.Name}?\n\nThe live terminal process will be closed and this terminal will be removed from its session.",
                "Remove terminal?", PowerShellPlusDialogKind.Question,
                "Remove", "Cancel", defaultToPrimary: true, primaryIsDangerous: true)) return false;
        if (stopPane) pane.Stop();
        TerminalHost.Children.Remove(pane); panes.Remove(profile.Id); state.Sessions.Remove(profile); SessionRecoveryStore.DeleteSession(profile.Id);
        foreach (var session in state.TerminalSessions)
        {
            session.TerminalIds.Remove(profile.Id);
            if (session.ActiveTerminalId == profile.Id) session.ActiveTerminalId = session.TerminalIds.FirstOrDefault();
        }
        RefreshActiveTerminalList();
        activePane = activeWorkspaceSession?.ActiveTerminalId is { } nextId && panes.TryGetValue(nextId, out var nextPane) ? nextPane : null;
        if (activePane is not null) SelectPane(activePane.Profile.Id, false); else ApplyLayout();
        RefreshWorkspaceSessionViews(); ScheduleSave();
        return true;
    }

    private void AddTerminalToActiveSession(SessionProfile profile)
    {
        state.Sessions.Add(profile);
        activeWorkspaceSession ??= state.TerminalSessions.First();
        if (!activeWorkspaceSession.TerminalIds.Contains(profile.Id, StringComparer.Ordinal))
            activeWorkspaceSession.TerminalIds.Add(profile.Id);
        activeWorkspaceSession.ActiveTerminalId = profile.Id;
        activeSessionTerminals.Add(profile);
        RefreshWorkspaceSessionViews();
    }

    private void RefreshActiveTerminalList()
    {
        activeSessionTerminals.Clear();
        if (activeWorkspaceSession is null) return;
        foreach (var terminalId in activeWorkspaceSession.TerminalIds)
            if (state.Sessions.FirstOrDefault(value => value.Id == terminalId) is { } profile)
                activeSessionTerminals.Add(profile);
    }

    private void RefreshWorkspaceSessionViews()
    {
        WorkspaceSessionList.Items.Refresh();
        WorkspaceSessionTabs.Items.Refresh();
        SessionList.Items.Refresh();
        UpdateCounts();
    }

    private void RunSnippet(bool all) { if (SnippetList.SelectedItem is CommandSnippet value) { if (all) foreach (var pane in panes.Values) pane.SendCommand(value.Command); else activePane?.SendCommand(value.Command); UpdateStatus($"Ran {value.Name}"); } }
    private List<TerminalPane> AutomationTargets(AutomationRule rule)
    {
        if (rule.TargetSessionId == "*") return panes.Values.ToList();
        return panes.TryGetValue(rule.TargetSessionId, out var pane) ? [pane] : [];
    }

    private async Task<int> RunAutomationAsync(AutomationRule rule, bool recordRun)
    {
        var targets = AutomationTargets(rule);
        var results = await Task.WhenAll(targets.Select(target => target.SendCommandAsync(rule.Command)));
        var accepted = results.Count(value => value);
        if (recordRun)
        {
            rule.LastRunUtc = DateTime.UtcNow;
            if (rule.ScheduleType == "Once") { rule.HasRun = true; rule.Enabled = false; }
            rule.NotifyDisplayChanged();
            ScheduleSave();
        }
        UpdateStatus($"{(recordRun ? "Ran" : "Tested")} {rule.Name} in {accepted} terminal(s){(recordRun ? string.Empty : " - schedule unchanged")}");
        return accepted;
    }

    private async Task CheckAutomationsAsync()
    {
        if (automationCheckRunning) return;
        automationCheckRunning = true;
        try
        {
            var utcNow = DateTime.UtcNow;
            var localNow = DateTime.Now;
            foreach (var rule in state.Automations.Where(value => value.IsDue(utcNow, localNow)).ToList()) await RunAutomationAsync(rule, true);
        }
        finally { automationCheckRunning = false; }
    }

    private void RefreshAutomationCountdowns()
    {
        foreach (var rule in state.Automations) rule.NotifyCountdownChanged();
    }

    private void ScheduleSave() { saveTimer.Stop(); saveTimer.Start(); }
    private void SaveNow() { try { WorkspaceStore.Save(state); } catch (Exception exception) { UpdateStatus(exception.Message); } }
    private void UpdateStatus(string text) { StatusText.Text = text; UpdateCounts(); }
    private void UpdateCounts() => CountText.Text = $"{state.TerminalSessions.Count} session{(state.TerminalSessions.Count == 1 ? string.Empty : "s")} · {panes.Count} native terminal{(panes.Count == 1 ? string.Empty : "s")} · {terminalProfile.SchemeName}";

    public async Task<bool> RunUiSnapshotAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        if (state.Automations.Count == 0)
        {
            state.Automations.Add(new AutomationRule
            {
                Name = "Daily workspace check",
                Command = "Get-Date",
                ScheduleType = "Daily",
                DailyTime = DateTime.Now.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture),
                LastRunUtc = DateTime.UtcNow.AddDays(-1)
            });
        }
        void Render(FrameworkElement visual, string name)
        {
            visual.UpdateLayout();
            var width = (int)Math.Ceiling(visual.ActualWidth);
            var height = (int)Math.Ceiling(visual.ActualHeight);
            if (width == 0 || height == 0) return;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outputDirectory, name));
            encoder.Save(stream);
        }
        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.Background); await Task.Delay(250); }
        async Task RenderCardMenuAsync(ListBox list, string name)
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem item)
                throw new InvalidOperationException($"{list.Name} did not create its first card.");
            var button = FindVisualDescendant<Button>(item);
            if (button?.Tag is not FrameworkElement card || card.ContextMenu is not ContextMenu menu)
                throw new InvalidOperationException($"{list.Name} card is missing its actions menu.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            if (!menu.IsOpen) throw new InvalidOperationException($"{list.Name} actions menu did not open.");
            if (menu.Items.OfType<MenuItem>().Any(action => !ReferenceEquals(action.DataContext, card.DataContext)))
                throw new InvalidOperationException($"{list.Name} menu actions are not bound to the clicked card.");
            Render(menu, name);
            menu.IsOpen = false;
            await Settle();
        }

        await Task.Delay(1200);
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        var toolbarCenter = TerminalToolbar.TranslatePoint(new Point(TerminalToolbar.ActualWidth / 2, 0), root).X;
        if (Math.Abs(toolbarCenter - root.ActualWidth / 2) > 1)
            throw new InvalidOperationException($"Layout controls must remain centered in the title bar. ToolbarCenter={toolbarCenter:F1}, WindowCenter={root.ActualWidth / 2:F1}");
        var openTerminalRight = TitleBarOpenWindowsTerminalButton.TranslatePoint(new Point(TitleBarOpenWindowsTerminalButton.ActualWidth, 0), root).X;
        var minimizeLeft = MinimizeButton.TranslatePoint(new Point(0, 0), root).X;
        if (openTerminalRight > minimizeLeft + 1)
            throw new InvalidOperationException($"Windows Terminal action must sit immediately before minimize. OpenRight={openTerminalRight:F1}, MinimizeLeft={minimizeLeft:F1}");
        Render(root, "ui-main.png");
        WindowsTerminalHoverChanged(IntPtr.Zero, true, true);
        await Settle();
        Render(root, "ui-windows-terminal-drop.png");
        HideWindowsTerminalDropOverlay();
        await Settle();
        var importSnapshotDirectory = Path.GetFullPath(outputDirectory);
        var importSnapshotSession = new CodexSessionMatch("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", importSnapshotDirectory, DateTime.UtcNow, TimeSpan.Zero, DateTime.UtcNow,
            "gpt-5.6-sol", "workspace-write", "on-request", ":workspace", "user");
        var importSnapshotPlan = WindowsTerminalImportPlanner.Create(new WindowsTerminalWindowCapture(IntPtr.Zero, "Windows Terminal", [
            WindowsTerminalImportPlanner.CreateTabCapture(0, "PowerShellPlus", $"OpenAI Codex (fixture){Environment.NewLine}directory: {importSnapshotDirectory}"),
            WindowsTerminalImportPlanner.CreateTabCapture(1, "Windows PowerShell", $"PS {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}>")
        ]), [importSnapshotSession]);
        var importDialog = new WindowsTerminalImportDialog(importSnapshotPlan) { Owner = this, ShowActivated = false };
        importDialog.Show();
        await Settle();
        Render((FrameworkElement)importDialog.Content, "ui-windows-terminal-import.png");
        importDialog.Close();
        await Settle();
        var promptDialog = PowerShellPlusDialog.CreateSnapshotDialog();
        promptDialog.Owner = this;
        promptDialog.ShowActivated = false;
        promptDialog.Show();
        await Settle();
        Render((FrameworkElement)promptDialog.Content, "ui-themed-prompt.png");
        promptDialog.Close();
        await Settle();
        await using (var remoteSnapshotServer = new LanRemoteServer(Dispatcher, GetLanRemoteSessions))
        {
            var remoteDialog = new LanRemoteDialog(remoteSnapshotServer, _ => Task.CompletedTask, () => Task.CompletedTask)
            {
                Owner = this,
                ShowActivated = false
            };
            remoteDialog.Show();
            await Settle();
            Render((FrameworkElement)remoteDialog.Content, "ui-remote-access-dialog.png");
            remoteDialog.Close();
            await Settle();
        }
        if (panes.Values.FirstOrDefault() is { } recoveryPane)
        {
            recoveryPane.SetPreviousOutputForTest("PS C:\\Projects\\PowerShellPlus> codex\nPrevious session output remains available after a real app or Windows restart.\nPS C:\\Projects\\PowerShellPlus>");
            await Settle();
            Render(recoveryPane, "ui-recovery-overlay.png");
            recoveryPane.HidePreviousOutputForTest();
        }
        await RenderCardMenuAsync(SessionList, "ui-sessions-actions.png");

        ShowSection(CommandsPanel);
        await Settle();
        await RenderCardMenuAsync(SnippetList, "ui-commands-actions.png");
        if (state.Snippets.FirstOrDefault() is { } commandForEditor)
        {
            OpenSnippetEditor(commandForEditor);
            await Settle();
            Render((FrameworkElement)Content, "ui-command-editor.png");
            HideEditor();
        }

        ShowSection(AutomationPanel);
        await Settle();
        Render((FrameworkElement)Content, "ui-automation-countdown.png");
        await RenderCardMenuAsync(AutomationList, "ui-automation-actions.png");

        ShowSection(SettingsPanel);
        await Settle();
        Render((FrameworkElement)Content, "ui-settings.png");

        OpenAutomationEditor(null);
        await Settle();
        Render((FrameworkElement)Content, "ui-automation-interval.png");

        AutomationTypeEdit.SelectedIndex = 2;
        await Settle();
        Render((FrameworkElement)Content, "ui-automation-one-time.png");
        Render(EditorCard, "ui-automation-one-time-card.png");

        AutomationDateEdit.ApplyTemplate();
        if (AutomationDateEdit.Template.FindName("PART_Button", AutomationDateEdit) is not Button dateButton)
            throw new InvalidOperationException("DatePicker template is missing PART_Button.");
        dateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        if (!AutomationDateEdit.IsDropDownOpen)
            throw new InvalidOperationException("Clicking the run date field did not keep the calendar open.");
        ApplyAutomationCalendarTheme();
        await Settle();
        if (AutomationDateEdit.Template.FindName("PART_Popup", AutomationDateEdit) is System.Windows.Controls.Primitives.Popup calendarPopup && calendarPopup.Child is FrameworkElement calendar)
            Render(calendar, "ui-calendar.png");
        AutomationDateEdit.IsDropDownOpen = false;

        AutomationTargetEdit.IsDropDownOpen = true;
        await Settle();
        if (AutomationTargetEdit.Template.FindName("PART_Popup", AutomationTargetEdit) is System.Windows.Controls.Primitives.Popup popup && popup.Child is FrameworkElement dropdown)
            Render(dropdown, "ui-dropdown.png");
        AutomationTargetEdit.IsDropDownOpen = false;
        HideEditor();
        return true;
    }

    public async Task<bool> RunSmokeTestAsync(string reportPath)
    {
        await Task.Delay(1700); var pane = activePane ?? panes.Values.FirstOrDefault();
        if (pane is null) { File.WriteAllText(reportPath, "FAIL No terminal pane was created."); return false; }
        if (!await pane.SendCommandAsync("Write-Output ('PSPLUS_NATIVE=' + (-not [Console]::IsInputRedirected) + ',' + (-not [Console]::IsOutputRedirected))"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, "FAIL Native terminal never became ready for input.");
            return false;
        }
        var deadline = DateTime.UtcNow.AddSeconds(12); string output = string.Empty;
        while (DateTime.UtcNow < deadline) { await Task.Delay(150); output = pane.GetOutput(); if (output.Contains("PSPLUS_NATIVE=True,True", StringComparison.Ordinal)) break; }
        var success = output.Contains("PSPLUS_NATIVE=True,True", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Microsoft TerminalControl hosts interactive ConPTY input/output.\nProfile={terminalProfile.ProfileName}\nFont={terminalProfile.FontFace}\nScheme={terminalProfile.SchemeName}\n\n{output}");
        return success;
    }

    public async Task<bool> RunWindowsTerminalCaptureSmokeTestAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        try
        {
            await Task.Delay(500);
            var windowHandle = WindowsTerminalImportService.FindWindowsTerminalWindows().FirstOrDefault();
            if (windowHandle == IntPtr.Zero)
            {
                File.WriteAllText(reportPath, "FAIL No running Windows Terminal window was available for the non-destructive capture smoke test.");
                return false;
            }
            var capture = await WindowsTerminalImportService.CaptureAsync(windowHandle);
            var candidates = await Task.Run(() => CodexActivityStore.FindAllActiveCliSessions());
            var plan = WindowsTerminalImportPlanner.Create(capture, candidates);
            var tabNamesCaptured = capture.Tabs.Count > 0 && capture.Tabs.All(value => !string.IsNullOrWhiteSpace(value.Title));
            var rowsCreatedPerTab = plan.Rows.Count == capture.Tabs.Count;
            var transcriptCaptured = capture.Tabs.Any(value => !string.IsNullOrWhiteSpace(value.Transcript));
            var codexDetected = capture.Tabs.Any(value => value.LooksLikeCodex);
            var exactCodexPermissionsAvailable = !codexDetected || candidates.Any(value => CodexSessionLocator.IsSafeCodexPermissionState(value.PermissionProfile, value.SandboxMode, value.ApprovalPolicy, value.ApprovalsReviewer)
                && CodexSessionLocator.IsSafeCodexApprovalsReviewer(value.ApprovalsReviewer));
            var codexThreadAutoMatched = !codexDetected || plan.Rows.Any(value => value.Tab.LooksLikeCodex && value.SelectedChoice?.Session is not null);
            var success = tabNamesCaptured && rowsCreatedPerTab && transcriptCaptured && exactCodexPermissionsAvailable && codexThreadAutoMatched;
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Windows Terminal UI Automation exposed tab names and terminal scrollback without closing the source window.\nWindowTitle={capture.WindowTitle}\nTabs={capture.Tabs.Count}\nTabNamesCaptured={tabNamesCaptured}\nRowsCreatedPerTab={rowsCreatedPerTab}\nTranscriptCaptured={transcriptCaptured}\nActiveCodexCandidates={candidates.Count}\nExactCodexPermissionsAvailable={exactCodexPermissionsAvailable}\nCodexThreadAutoMatched={codexThreadAutoMatched}\n{string.Join(Environment.NewLine, capture.Tabs.Select(value => $"Tab[{value.Index}]={value.Title}; Characters={value.Transcript.Length}; Directory={value.WorkingDirectory ?? "unknown"}; Codex={value.LooksLikeCodex}"))}\n{string.Join(Environment.NewLine, candidates.Select(value => $"Codex={value.SessionId}; Directory={value.WorkingDirectory}; Model={value.Model}; PermissionProfile={value.PermissionProfile}; Sandbox={value.SandboxMode}; Approval={value.ApprovalPolicy}; Reviewer={value.ApprovalsReviewer}"))}");
            return success;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, $"FAIL Windows Terminal capture threw an exception.\n{exception}");
            return false;
        }
    }

    public async Task<bool> RunCodexSmokeTestAsync(string reportPath)
    {
        await Task.Delay(1700); var pane = activePane ?? panes.Values.FirstOrDefault();
        if (pane is null || !await pane.SendCommandAsync("codex"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, "FAIL Native terminal never became ready for Codex input.");
            return false;
        }
        var deadline = DateTime.UtcNow.AddSeconds(14); string output = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200); output = pane.GetOutput();
            if (output.Contains("stdin is not a terminal", StringComparison.OrdinalIgnoreCase)) break;
            if (output.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase)) break;
        }
        if (output.Contains("Update available!", StringComparison.OrdinalIgnoreCase)
            && output.Contains("Press enter to continue", StringComparison.OrdinalIgnoreCase)
            && await pane.SendCommandAsync("2"))
        {
            deadline = DateTime.UtcNow.AddSeconds(14);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200); output = pane.GetOutput();
                if (output.Contains("stdin is not a terminal", StringComparison.OrdinalIgnoreCase)) break;
                if (output.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        await Task.Delay(350);
        var codexDetected = pane.GetCodexProcessState().IsActive;
        var launchMarker = CodexLaunchStore.Load(pane.Profile.Id);
        var launchRecorded = launchMarker?.IsActive == true && launchMarker.StartedUtc > DateTime.UtcNow.AddMinutes(-2);
        var shellProcessRecorded = launchMarker?.ShellProcessId is > 0;
        var markerProcessState = shellProcessRecorded ? ProcessTreeInspector.FindCodexProcess(launchMarker!.ShellProcessId!.Value) : default;
        var markerProcessDetected = markerProcessState.IsActive && markerProcessState.ProcessId is > 0;
        var exactSession = launchRecorded ? CodexSessionLocator.FindBestSession(launchMarker!.StartedUtc, launchMarker.WorkingDirectory) : null;
        var exactSessionBound = exactSession is not null;
        if (exactSession is not null) CodexLaunchStore.Confirm(launchMarker!, exactSession);
        // Codex does not create rollout metadata until the first user message,
        // so a launch-only smoke can verify the pane marker but may not yet
        // have a durable thread ID to bind.
        var success = output.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase) && !output.Contains("stdin is not a terminal", StringComparison.OrdinalIgnoreCase) && codexDetected && launchRecorded && shellProcessRecorded && markerProcessDetected;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Bare Codex launched inside Microsoft TerminalControl and its pane-scoped recovery marker was recorded.\nCodexDetected={codexDetected}\nLaunchRecorded={launchRecorded}\nShellProcessRecorded={shellProcessRecorded}\nMarkerProcessDetected={markerProcessDetected}\nExactSessionBoundAfterFirstMessage={exactSessionBound}\n\n{output}");
        pane.Stop();
        return success;
    }

    public async Task<bool> RunPersistenceSmokeTestAsync(string reportPath)
    {
        await Task.Delay(1800);
        var pane = activePane ?? panes.Values.FirstOrDefault();
        if (pane is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, "FAIL No terminal pane was available for persistence testing.");
            return false;
        }

        var rootReadyDeadline = DateTime.UtcNow.AddSeconds(8);
        var rootBefore = pane.GetRootProcessId();
        while (rootBefore is null && DateTime.UtcNow < rootReadyDeadline)
        {
            await Task.Delay(100);
            rootBefore = pane.GetRootProcessId();
        }
        var workspaceTestIsolated = automationMode && WorkspaceStore.DirectoryOverride is not null
            && !Path.GetFullPath(WorkspaceStore.DirectoryPath).Equals(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerShellPlus")), StringComparison.OrdinalIgnoreCase);
        var profile = new SessionProfile { CommandLine = "powershell.exe", WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var normalScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = false }));
        const string savedModel = "gpt-5.3-codex-spark";
        const string savedSandboxMode = "danger-full-access";
        const string savedApprovalPolicy = "never";
        const string savedPermissionProfile = ":danger-full-access";
        const string savedApprovalsReviewer = "user";
        var codexScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = savedModel, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy }));
        var profilePermissionScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = savedModel, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy, CodexPermissionProfile = savedPermissionProfile, CodexApprovalsReviewer = savedApprovalsReviewer }));
        var pickerScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true }));
        var unsafeModelScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = "gpt'; Write-Output unsafe; #" }));
        var unsafePermissionsScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexSandboxMode = "danger-full-access'; Write-Output unsafe; #", CodexApprovalPolicy = savedApprovalPolicy }));
        var normalDoesNotResumeCodex = !normalScript.Contains("codex resume", StringComparison.OrdinalIgnoreCase);
        var codexResumesExactSession = codexScript.Contains("codex resume '11111111-2222-3333-4444-555555555555'", StringComparison.OrdinalIgnoreCase);
        var codexResumesSavedModel = codexScript.Contains($"--model '{savedModel}'", StringComparison.Ordinal);
        var codexResumesSavedPermissions = codexScript.Contains($"--sandbox '{savedSandboxMode}' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal);
        var profilePermissionResumeStart = profilePermissionScript.LastIndexOf("; & codex resume", StringComparison.OrdinalIgnoreCase);
        var profilePermissionResumeCommand = profilePermissionResumeStart >= 0 ? profilePermissionScript[profilePermissionResumeStart..] : profilePermissionScript;
        var codexResumesSavedPermissionProfile = profilePermissionResumeCommand.Contains($"--config 'default_permissions=\"{savedPermissionProfile}\"' --config 'approvals_reviewer=\"{savedApprovalsReviewer}\"' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal)
            && !profilePermissionResumeCommand.Contains("--sandbox", StringComparison.OrdinalIgnoreCase);
        var unsafeModelRejected = !unsafeModelScript.Contains("codex resume '11111111-2222-3333-4444-555555555555' --model", StringComparison.OrdinalIgnoreCase)
            && !unsafeModelScript.Contains("Write-Output unsafe", StringComparison.Ordinal);
        var unsafeResumeStart = unsafePermissionsScript.LastIndexOf("; & codex resume", StringComparison.OrdinalIgnoreCase);
        var unsafeResumeCommand = unsafeResumeStart >= 0 ? unsafePermissionsScript[unsafeResumeStart..] : unsafePermissionsScript;
        var unsafePermissionsRejected = !unsafeResumeCommand.Contains("--sandbox", StringComparison.OrdinalIgnoreCase)
            && !unsafeResumeCommand.Contains("--ask-for-approval", StringComparison.OrdinalIgnoreCase)
            && !unsafeResumeCommand.Contains("Write-Output unsafe", StringComparison.Ordinal);
        var ambiguousCodexUsesPicker = pickerScript.Contains("codex resume --all", StringComparison.OrdinalIgnoreCase) && !pickerScript.Contains("--last", StringComparison.OrdinalIgnoreCase);
        var powershellWrapperInstalled = normalScript.Contains("function global:codex", StringComparison.OrdinalIgnoreCase)
            && normalScript.Contains(profile.Id, StringComparison.Ordinal);
        var sshWrapperInstalled = normalScript.Contains("function global:ssh", StringComparison.OrdinalIgnoreCase)
            && normalScript.Contains("ConnectionArguments", StringComparison.Ordinal);
        var sshKeyPath = Path.Combine(Path.GetDirectoryName(reportPath)!, "vps recovery key");
        var safeSshAccepted = SshRecovery.TryNormalizeConnectionArguments(["-p", "2222", "-i", sshKeyPath, "deploy@vps.example"], out var safeSshArguments, out var safeSshDestination)
            && safeSshDestination == "deploy@vps.example" && safeSshArguments.SequenceEqual(["-p", "2222", "-i", sshKeyPath, "deploy@vps.example"]);
        var expandedHomeIdentity = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "hermes_ovh_access");
        var quotedHomeIdentityAccepted = SshRecovery.TryNormalizeConnectionArguments(["-i", expandedHomeIdentity, "ubuntu@15.204.82.129"], out var quotedHomeArguments, out var quotedHomeDestination)
            && quotedHomeDestination == "ubuntu@15.204.82.129"
            && quotedHomeArguments.SequenceEqual(["-i", expandedHomeIdentity, "ubuntu@15.204.82.129"]);
        var safeSshReliabilityOptionsAccepted = SshRecovery.TryNormalizeConnectionArguments([
            "-o", "ConnectionAttempts=2", "-o", "ConnectTimeout=12", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "deploy@vps.example"
        ], out _, out var reliabilityDestination) && reliabilityDestination == "deploy@vps.example";
        var unsafeSshRejected = !SshRecovery.TryNormalizeConnectionArguments(["-o", "ProxyCommand=Write-Output unsafe", "deploy@vps.example"], out _, out _)
            && !SshRecovery.TryNormalizeConnectionArguments(["-o", "ConnectTimeout=999", "deploy@vps.example"], out _, out _)
            && !SshRecovery.TryNormalizeConnectionArguments(["ssh://deploy:password@vps.example"], out _, out _)
            && !SshRecovery.TryNormalizeConnectionArguments(["deploy@vps.example", "cat", "/etc/shadow"], out _, out _);
        const string hermesSessionId = "20260717_123456_a1b2c3";
        const string hermesModel = "gpt-5.6-sol";
        var hermesDetection = HermesRecovery.Detect($"deploy@vps:~$ hermes --tui{Environment.NewLine}Hermes Agent{Environment.NewLine}● {hermesModel} · 69 tools · provider: openai-codex{Environment.NewLine}↻ Resumed session {hermesSessionId}");
        var hermesExactSessionDetected = hermesDetection.WasActive && hermesDetection.UseTui
            && hermesDetection.SessionId == hermesSessionId && hermesDetection.Model == hermesModel;
        const string changedHermesModel = "gpt-5.3-codex-spark";
        var changedHermesDetection = HermesRecovery.Detect($"Hermes Agent{Environment.NewLine}● {hermesModel} · 69 tools{Environment.NewLine}❯ /model {changedHermesModel}{Environment.NewLine}⚕ {changedHermesModel} │ 17% │ 13s", hermesDetection);
        var hermesModelChangeDetected = changedHermesDetection.Model == changedHermesModel;
        var unsafeHermesModelRejected = !HermesRecovery.IsSafeModel("gpt'; Write-Output unsafe; #");
        var exitedHermesNotRestored = !HermesRecovery.Detect($"deploy@vps:~$ hermes{Environment.NewLine}Hermes Agent{Environment.NewLine}Resume this session with:{Environment.NewLine}  hermes --resume {hermesSessionId}").WasActive;
        var sshHermesRecovery = new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = ["-p", "2222", "-i", sshKeyPath, "deploy@vps.example"],
            HermesWasActive = true,
            HermesSessionId = hermesSessionId,
            HermesModel = hermesModel,
            HermesUseTui = true,
            WorkingDirectory = profile.WorkingDirectory
        };
        var sshHermesScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, sshHermesRecovery));
        var sshHermesExactResume = sshHermesScript.Contains("${SHELL:-/bin/sh}", StringComparison.Ordinal)
            && sshHermesScript.Contains(hermesSessionId, StringComparison.Ordinal)
            && sshHermesScript.Contains(hermesModel, StringComparison.Ordinal)
            && sshHermesScript.Contains("hermes", StringComparison.Ordinal);
        var sshRecoveryIsBoundedAndVisible = sshHermesScript.Contains("[PowerShellPlus] Restoring SSH and Hermes session", StringComparison.Ordinal)
            && sshHermesScript.Contains("$global:__PowerShellPlusSshRecoveryActive = $true", StringComparison.Ordinal)
            && sshHermesScript.Contains("saved session was kept", StringComparison.Ordinal)
            && sshHermesScript.Contains("PowerShell prompt remains interactive", StringComparison.Ordinal);
        var sshHermesFallbackScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = ["deploy@vps.example"],
            HermesWasActive = true,
            HermesModel = changedHermesModel
        }));
        var sshHermesFallbackResume = sshHermesFallbackScript.Contains("${SHELL:-/bin/sh}", StringComparison.Ordinal)
            && sshHermesFallbackScript.Contains(changedHermesModel, StringComparison.Ordinal)
            && sshHermesFallbackScript.Contains("--continue", StringComparison.Ordinal);
        var unsafeHermesModelScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = ["deploy@vps.example"],
            HermesWasActive = true,
            HermesSessionId = hermesSessionId,
            HermesModel = "gpt'; Write-Output unsafe; #"
        }));
        var unsafeHermesResumeStart = unsafeHermesModelScript.LastIndexOf("& ssh", StringComparison.OrdinalIgnoreCase);
        var unsafeHermesResumeCommand = unsafeHermesResumeStart >= 0 ? unsafeHermesModelScript[unsafeHermesResumeStart..] : unsafeHermesModelScript;
        var unsafeHermesModelNotInjected = !unsafeHermesResumeCommand.Contains("Write-Output unsafe", StringComparison.Ordinal)
            && !unsafeHermesResumeCommand.Contains("'--model'", StringComparison.Ordinal);
        const string remoteCodexId = "019f842c-c1b5-7ce2-a0f3-f1719eea9753";
        const string remoteCodexDirectory = "/home/ubuntu/project with spaces";
        var remoteProbeJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            active = true,
            sessionId = remoteCodexId,
            workingDirectory = remoteCodexDirectory,
            model = savedModel,
            sandboxMode = savedSandboxMode,
            approvalPolicy = savedApprovalPolicy,
            permissionProfile = (string?)null,
            approvalsReviewer = savedApprovalsReviewer
        });
        var remoteProbeOutput = "noise\nPSP_REMOTE_CODEX:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(remoteProbeJson));
        var remoteProbeParsed = RemoteCodexRecovery.TryParseProbeOutput(remoteProbeOutput, out var remoteProbeState)
            && remoteProbeState.WasActive && remoteProbeState.SessionId == remoteCodexId
            && remoteProbeState.WorkingDirectory == remoteCodexDirectory && remoteProbeState.Model == savedModel;
        var remoteCodexRecovery = new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = ["-i", expandedHomeIdentity, "ubuntu@15.204.82.129"],
            RemoteCodexWasActive = true,
            RemoteCodexSessionId = remoteCodexId,
            RemoteCodexWorkingDirectory = remoteCodexDirectory,
            RemoteCodexModel = savedModel,
            RemoteCodexSandboxMode = savedSandboxMode,
            RemoteCodexApprovalPolicy = savedApprovalPolicy,
            RemoteCodexApprovalsReviewer = savedApprovalsReviewer
        };
        var remoteCodexScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, remoteCodexRecovery));
        var remoteCodexCommand = RemoteCodexRecovery.BuildRemoteCommand(profile.Id, remoteCodexRecovery);
        var remoteCodexExactResume = remoteCodexScript.Contains("[PowerShellPlus] Restoring SSH and Codex session", StringComparison.Ordinal)
            && remoteCodexCommand.Contains("${SHELL:-/bin/sh}", StringComparison.Ordinal)
            && remoteCodexCommand.Contains("codex resume", StringComparison.Ordinal)
            && remoteCodexCommand.Contains(remoteCodexId, StringComparison.Ordinal)
            && remoteCodexCommand.Contains("cd --", StringComparison.Ordinal)
            && remoteCodexCommand.Contains(remoteCodexDirectory, StringComparison.Ordinal)
            && remoteCodexCommand.Contains("--model", StringComparison.Ordinal)
            && remoteCodexCommand.Contains(savedModel, StringComparison.Ordinal)
            && !remoteCodexCommand.Contains("--last", StringComparison.OrdinalIgnoreCase);
        var unsafeRemoteProbeRejected = !RemoteCodexRecovery.TryParseProbeOutput(
            "PSP_REMOTE_CODEX:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"active\":true,\"sessionId\":\"bad'; touch /tmp/pwned\",\"workingDirectory\":\"/home/ubuntu\"}")), out _);
        var sshLoginOnlyScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry
        {
            SshWasActive = true,
            SshConnectionArguments = ["deploy@vps.example"]
        }));
        var sshLoginOnlyRestored = sshLoginOnlyScript.Contains("'-tt' 'deploy@vps.example'", StringComparison.Ordinal)
            && !sshLoginOnlyScript.Contains("'hermes'", StringComparison.Ordinal);
        var unsafeSshScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry
        {
            SshWasActive = true,
            SshConnectionArguments = ["-o", "ProxyCommand=Write-Output unsafe", "deploy@vps.example"],
            HermesWasActive = true
        }));
        var unsafeSshResumeRejected = !unsafeSshScript.Contains("ProxyCommand", StringComparison.OrdinalIgnoreCase)
            && !unsafeSshScript.Contains("Write-Output unsafe", StringComparison.OrdinalIgnoreCase);
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "codex-recovery-fixture");
        Directory.CreateDirectory(fixtureRoot);
        var fixtureId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var fixtureStarted = DateTime.UtcNow;
        var actualCodexDirectory = Path.GetDirectoryName(reportPath)!;
        var fixture = new { timestamp = fixtureStarted.ToString("O"), type = "session_meta", payload = new { session_id = fixtureId, id = Guid.NewGuid().ToString(), timestamp = fixtureStarted.ToString("O"), cwd = actualCodexDirectory } };
        var earlierTurn = new { timestamp = fixtureStarted.AddSeconds(1).ToString("O"), type = "turn_context", payload = new { model = "gpt-5.2-codex", approval_policy = "on-request", sandbox_policy = new { type = "workspace-write", network_access = false } } };
        var modelChange = new { timestamp = fixtureStarted.AddSeconds(2).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { model = savedModel } } };
        var permissionsChange = new { timestamp = fixtureStarted.AddSeconds(3).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { active_permission_profile = new { id = ":danger-full-access" }, approval_policy = savedApprovalPolicy, approvals_reviewer = savedApprovalsReviewer } } };
        var unknownPermissionsChange = new { timestamp = fixtureStarted.AddSeconds(4).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { active_permission_profile = new { id = ":unknown-profile" }, approval_policy = savedApprovalPolicy } } };
        var unsafeModelChange = new { timestamp = fixtureStarted.AddSeconds(5).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { model = "gpt'; Write-Output unsafe; #" } } };
        File.WriteAllLines(Path.Combine(fixtureRoot, "rollout-test.jsonl"), [
            System.Text.Json.JsonSerializer.Serialize(fixture),
            System.Text.Json.JsonSerializer.Serialize(earlierTurn),
            System.Text.Json.JsonSerializer.Serialize(modelChange),
            System.Text.Json.JsonSerializer.Serialize(permissionsChange),
            System.Text.Json.JsonSerializer.Serialize(unknownPermissionsChange),
            System.Text.Json.JsonSerializer.Serialize(unsafeModelChange)
        ]);
        File.WriteAllText(Path.Combine(fixtureRoot, "rollout-partially-written.jsonl"), "{not-complete-json");
        var mappedSession = CodexSessionLocator.FindBestSession(fixtureStarted, null, null, fixtureRoot);
        var codexSessionMapped = mappedSession?.SessionId == fixtureId && string.Equals(mappedSession.WorkingDirectory, actualCodexDirectory, StringComparison.OrdinalIgnoreCase);
        var latestModelMapped = mappedSession?.Model == savedModel && CodexSessionLocator.FindLatestModel(fixtureId, fixtureRoot)?.Model == savedModel;
        var latestPermissions = CodexSessionLocator.FindLatestPermissions(fixtureId, fixtureRoot);
        var latestPermissionsMapped = mappedSession?.SandboxMode == savedSandboxMode && mappedSession.ApprovalPolicy == savedApprovalPolicy
            && mappedSession.PermissionProfile == savedPermissionProfile
            && mappedSession.ApprovalsReviewer == savedApprovalsReviewer
            && latestPermissions?.SandboxMode == savedSandboxMode && latestPermissions.ApprovalPolicy == savedApprovalPolicy
            && latestPermissions.PermissionProfile == savedPermissionProfile && latestPermissions.ApprovalsReviewer == savedApprovalsReviewer;
        var partialRolloutIgnored = codexSessionMapped && latestModelMapped && latestPermissionsMapped;
        var changedDirectoryRestored = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = fixtureId, WorkingDirectory = actualCodexDirectory }))
            .Contains($"Set-Location -LiteralPath '{actualCodexDirectory.Replace("'", "''")}'", StringComparison.OrdinalIgnoreCase);
        const int fixtureProcessId = 42420;
        const string launcherThreadId = "11111111-aaaa-bbbb-cccc-111111111111";
        const string resumedThreadId = "22222222-aaaa-bbbb-cccc-222222222222";
        const string subagentThreadId = "33333333-aaaa-bbbb-cccc-333333333333";
        var resumedMetadataTime = fixtureStarted.AddDays(-7);
        var launcherMetadata = new { timestamp = fixtureStarted.ToString("O"), type = "session_meta", payload = new { session_id = launcherThreadId, timestamp = fixtureStarted.ToString("O"), cwd = actualCodexDirectory, source = "cli" } };
        var resumedMetadata = new { timestamp = resumedMetadataTime.ToString("O"), type = "session_meta", payload = new { session_id = resumedThreadId, timestamp = resumedMetadataTime.ToString("O"), cwd = actualCodexDirectory, source = "cli" } };
        var resumedModel = new { timestamp = fixtureStarted.AddSeconds(20).ToString("O"), type = "turn_context", payload = new { model = savedModel, approval_policy = savedApprovalPolicy, sandbox_policy = new { type = savedSandboxMode } } };
        var subagentMetadata = new { timestamp = fixtureStarted.AddSeconds(21).ToString("O"), type = "session_meta", payload = new { session_id = subagentThreadId, timestamp = fixtureStarted.AddSeconds(21).ToString("O"), cwd = actualCodexDirectory, source = new { subagent = new { thread_spawn = new { parent_thread_id = resumedThreadId } } } } };
        File.WriteAllText(Path.Combine(fixtureRoot, "rollout-launcher.jsonl"), System.Text.Json.JsonSerializer.Serialize(launcherMetadata));
        var resumedRolloutPath = Path.Combine(fixtureRoot, "rollout-resumed.jsonl");
        File.WriteAllLines(resumedRolloutPath, [System.Text.Json.JsonSerializer.Serialize(resumedMetadata), System.Text.Json.JsonSerializer.Serialize(resumedModel)]);
        File.WriteAllText(Path.Combine(fixtureRoot, "rollout-subagent.jsonl"), System.Text.Json.JsonSerializer.Serialize(subagentMetadata));
        var logsFixturePath = Path.Combine(fixtureRoot, "logs-fixture.sqlite");
        var fixtureEpoch = new DateTimeOffset(fixtureStarted).ToUnixTimeSeconds();
        var activityFixtureCreated = CodexActivityStore.CreateFixtureForTest(logsFixturePath, fixtureProcessId, [
            (launcherThreadId, fixtureEpoch + 1),
            (resumedThreadId, fixtureEpoch + 20),
            (subagentThreadId, fixtureEpoch + 21)
        ]);
        CodexSessionMatch? activeResumedSession;
        using (var liveRolloutWriter = new FileStream(resumedRolloutPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            activeResumedSession = activityFixtureCreated
                ? CodexActivityStore.FindActiveCliSession(fixtureProcessId, fixtureStarted, null, logsFixturePath, fixtureRoot)
                : null;
        }
        var inTuiResumeRebound = activeResumedSession?.SessionId == resumedThreadId && activeResumedSession.Model == savedModel
            && activeResumedSession.SandboxMode == savedSandboxMode && activeResumedSession.ApprovalPolicy == savedApprovalPolicy;
        var liveRolloutSharedRead = inTuiResumeRebound;
        var launchTimeFallbackRebound = false;
        Process? fallbackProbe = null;
        try
        {
            var fakeCodexPath = Path.Combine(fixtureRoot, "codex-recovery-probe.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeCodexPath, true);
            var probeLaunchStarted = DateTime.UtcNow;
            fallbackProbe = Process.Start(new ProcessStartInfo
            {
                FileName = fakeCodexPath,
                Arguments = "/d /c ping.exe 127.0.0.1 -n 30 > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (fallbackProbe is not null)
            {
                var probeStarted = fallbackProbe.StartTime.ToUniversalTime();
                var probeEpoch = new DateTimeOffset(probeStarted).ToUnixTimeSeconds();
                var fallbackLogsPath = Path.Combine(fixtureRoot, "logs-launch-fallback.sqlite");
                var fallbackFixtureCreated = CodexActivityStore.CreateFixtureForTest(fallbackLogsPath, fallbackProbe.Id, [
                    (launcherThreadId, probeEpoch + 1),
                    (resumedThreadId, probeEpoch + 2),
                    (subagentThreadId, probeEpoch + 3)
                ]);
                var launchMatchedSession = fallbackFixtureCreated
                    ? CodexActivityStore.FindActiveCliSessionNearLaunch(probeLaunchStarted, null, fallbackLogsPath, fixtureRoot)
                    : null;
                launchTimeFallbackRebound = launchMatchedSession?.SessionId == resumedThreadId && launchMatchedSession.Model == savedModel
                    && launchMatchedSession.SandboxMode == savedSandboxMode && launchMatchedSession.ApprovalPolicy == savedApprovalPolicy;
            }
        }
        catch { launchTimeFallbackRebound = false; }
        finally
        {
            try
            {
                if (fallbackProbe is { HasExited: false }) fallbackProbe.Kill(true);
                fallbackProbe?.WaitForExit(3000);
            }
            catch { }
            fallbackProbe?.Dispose();
        }
        try { Directory.Delete(fixtureRoot, true); } catch { }
        var launchRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "codex-launch-fixture");
        var launchMarker = new CodexLaunchMarker { PaneId = profile.Id, StartedUtc = fixtureStarted, ShellProcessId = fixtureProcessId, WorkingDirectory = actualCodexDirectory };
        CodexLaunchStore.Save(launchMarker, launchRoot);
        if (mappedSession is not null) CodexLaunchStore.Confirm(launchMarker, mappedSession, launchRoot);
        var confirmedLaunch = CodexLaunchStore.Load(profile.Id, launchRoot);
        var exactLaunchBindingPersisted = confirmedLaunch?.IsActive == true && confirmedLaunch.ShellProcessId == fixtureProcessId && confirmedLaunch.SessionId == fixtureId && confirmedLaunch.WorkingDirectory == actualCodexDirectory && confirmedLaunch.Model == savedModel
            && confirmedLaunch.SandboxMode == savedSandboxMode && confirmedLaunch.ApprovalPolicy == savedApprovalPolicy && confirmedLaunch.PermissionProfile == savedPermissionProfile
            && confirmedLaunch.ApprovalsReviewer == savedApprovalsReviewer;
        if (confirmedLaunch is not null)
        {
            confirmedLaunch.EndedUtc = DateTime.UtcNow;
            CodexLaunchStore.Save(confirmedLaunch, launchRoot);
        }
        var normalCodexExitRecorded = CodexLaunchStore.Load(profile.Id, launchRoot)?.IsActive == false;
        var wrapperScript = CodexLaunchStore.BuildPowerShellWrapper(profile.Id, launchRoot);
        var wrapperRecordsPaneAndLifecycle = wrapperScript.Contains(profile.Id, StringComparison.Ordinal)
            && wrapperScript.Contains("StartedUtc", StringComparison.Ordinal)
            && wrapperScript.Contains("ShellProcessId = $PID", StringComparison.Ordinal)
            && wrapperScript.Contains("Model", StringComparison.Ordinal)
            && wrapperScript.Contains("SandboxMode", StringComparison.Ordinal)
            && wrapperScript.Contains("ApprovalPolicy", StringComparison.Ordinal)
            && wrapperScript.Contains("PermissionProfile", StringComparison.Ordinal)
            && wrapperScript.Contains("ApprovalsReviewer", StringComparison.Ordinal)
            && wrapperScript.Contains("EndedUtc", StringComparison.Ordinal);
        try { Directory.Delete(launchRoot, true); } catch { }
        var sshLaunchRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "ssh-launch-fixture");
        var sshLaunchMarker = new SshLaunchMarker
        {
            PaneId = profile.Id,
            StartedUtc = fixtureStarted,
            ShellProcessId = fixtureProcessId,
            WorkingDirectory = actualCodexDirectory,
            ConnectionArguments = safeSshArguments
        };
        SshLaunchStore.Save(sshLaunchMarker, sshLaunchRoot);
        var loadedSshLaunch = SshLaunchStore.Load(profile.Id, sshLaunchRoot);
        var sshLaunchBindingPersisted = loadedSshLaunch?.IsActive == true
            && loadedSshLaunch.ShellProcessId == fixtureProcessId
            && loadedSshLaunch.ConnectionArguments.SequenceEqual(safeSshArguments);
        if (loadedSshLaunch is not null)
        {
            loadedSshLaunch.EndedUtc = DateTime.UtcNow;
            SshLaunchStore.Save(loadedSshLaunch, sshLaunchRoot);
        }
        var normalSshExitRecorded = SshLaunchStore.Load(profile.Id, sshLaunchRoot)?.IsActive == false;
        var sshWrapperScript = SshLaunchStore.BuildPowerShellWrapper(profile.Id, sshLaunchRoot);
        var sshWrapperRecordsSafeConnectionOnly = sshWrapperScript.Contains("function global:ssh", StringComparison.OrdinalIgnoreCase)
            && sshWrapperScript.Contains("ConnectionArguments", StringComparison.Ordinal)
            && sshWrapperScript.Contains("RecoveryAttempt", StringComparison.Ordinal)
            && sshWrapperScript.Contains("ExitCode", StringComparison.Ordinal)
            && sshWrapperScript.Contains("EndedUtc", StringComparison.Ordinal)
            && !sshWrapperScript.Contains("ProxyCommand", StringComparison.OrdinalIgnoreCase)
            && !sshWrapperScript.Contains("Password", StringComparison.OrdinalIgnoreCase);
        try { Directory.Delete(sshLaunchRoot, true); } catch { }
        var sshWrapperRuntimeRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "ssh-wrapper-runtime-fixture");
        var sshWrapperExecutesSafely = false;
        const string sshWrapperDiagnostic = "validated";
        try
        {
            Directory.CreateDirectory(sshWrapperRuntimeRoot);
            var fakeBin = Path.Combine(sshWrapperRuntimeRoot, "bin");
            var runtimeMarkers = Path.Combine(sshWrapperRuntimeRoot, "markers");
            Directory.CreateDirectory(fakeBin);
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(fakeBin, "ssh.exe"), true);
            bool RunSshWrapper(string paneId, string invocation)
            {
                var runtimeScript = SshLaunchStore.BuildPowerShellWrapper(paneId, runtimeMarkers) + "; " + invocation;
                var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(runtimeScript));
                using var runtimeProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoLogo -NoProfile -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Environment = { ["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }
                });
                if (runtimeProcess is null) return false;
                var runtimeOutput = runtimeProcess.StandardOutput.ReadToEndAsync();
                var runtimeError = runtimeProcess.StandardError.ReadToEndAsync();
                var exited = runtimeProcess.WaitForExit(10_000);
                _ = runtimeOutput.GetAwaiter().GetResult();
                _ = runtimeError.GetAwaiter().GetResult();
                return exited;
            }
            var validWrapperExited = RunSshWrapper("runtime-pane", "$global:__PowerShellPlusSshRecoveryActive = $true; ssh '-o' 'ConnectTimeout=1' 'deploy@vps.example'");
            var runtimeMarker = SshLaunchStore.Load("runtime-pane", runtimeMarkers);
            var validWrapperMarker = validWrapperExited && runtimeMarker?.IsActive == false
                && runtimeMarker.RecoveryAttempt && runtimeMarker.ExitCode is not null
                && runtimeMarker.ConnectionArguments.SequenceEqual(["deploy@vps.example"]);
            var unsafeWrapperExited = RunSshWrapper("unsafe-runtime-pane", "ssh 'ssh://deploy:password@vps.example'");
            var unsafeMarkerPath = Path.Combine(runtimeMarkers, SessionRecoveryStore.SafeSessionId("unsafe-runtime-pane") + ".json");
            sshWrapperExecutesSafely = validWrapperMarker && unsafeWrapperExited && !File.Exists(unsafeMarkerPath);
        }
        catch { sshWrapperExecutesSafely = false; }
        finally { try { Directory.Delete(sshWrapperRuntimeRoot, true); } catch { } }
        var sshBannerTimeoutFallsBackInteractive = false;
        const string sshBannerDiagnostic = "bounded fallback validated";
        var previousSshLaunchOverride = SshLaunchStore.DirectoryOverride;
        var timeoutFixtureRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "ssh-timeout-fixture");
        System.Net.Sockets.TcpListener? stalledSshServer = null;
        System.Net.Sockets.TcpClient? stalledSshClient = null;
        try
        {
            Directory.CreateDirectory(timeoutFixtureRoot);
            SshLaunchStore.DirectoryOverride = timeoutFixtureRoot;
            stalledSshServer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            stalledSshServer.Start();
            var timeoutPort = ((System.Net.IPEndPoint)stalledSshServer.LocalEndpoint).Port;
            var acceptTask = stalledSshServer.AcceptTcpClientAsync();
            var timeoutRecovery = new SessionRecoveryEntry
            {
                SessionId = profile.Id,
                SshWasActive = true,
                SshConnectionArguments = ["-p", timeoutPort.ToString(CultureInfo.InvariantCulture), "-o", "ConnectionAttempts=1", "-o", "ConnectTimeout=1", "127.0.0.1"]
            };
            var timeoutScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, timeoutRecovery))
                + "; Write-Output 'PSPLUS_SSH_RECOVERY_FALLBACK_OK'";
            var encodedTimeoutScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(timeoutScript));
            using var timeoutProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -EncodedCommand {encodedTimeoutScript}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (timeoutProcess is not null)
            {
                var outputTask = timeoutProcess.StandardOutput.ReadToEndAsync();
                var errorTask = timeoutProcess.StandardError.ReadToEndAsync();
                stalledSshClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
                await timeoutProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var timeoutOutput = await outputTask + await errorTask;
                var timeoutMarker = SshLaunchStore.Load(profile.Id, timeoutFixtureRoot);
                sshBannerTimeoutFallsBackInteractive = timeoutProcess.ExitCode == 0
                    && timeoutOutput.Contains("[PowerShellPlus] Restoring SSH session", StringComparison.Ordinal)
                    && timeoutOutput.Contains("Automatic recovery could not connect", StringComparison.Ordinal)
                    && timeoutOutput.Contains("PSPLUS_SSH_RECOVERY_FALLBACK_OK", StringComparison.Ordinal)
                    && timeoutMarker?.IsFailedRecovery == true;
            }
        }
        catch { sshBannerTimeoutFallsBackInteractive = false; }
        finally
        {
            stalledSshClient?.Dispose();
            stalledSshServer?.Stop();
            SshLaunchStore.DirectoryOverride = previousSshLaunchOverride;
            try { Directory.Delete(timeoutFixtureRoot, true); } catch { }
        }
        var failedRecoveryMarker = new SshLaunchMarker
        {
            PaneId = profile.Id,
            StartedUtc = fixtureStarted,
            WorkingDirectory = actualCodexDirectory,
            ConnectionArguments = safeSshArguments,
            RecoveryAttempt = true,
            ExitCode = 255,
            EndedUtc = DateTime.UtcNow
        };
        var failedRecoveryStateRetained = SshRecovery.ShouldKeepPendingRecovery(sshHermesRecovery, failedRecoveryMarker, false)
            && SshRecovery.ShouldPreserveTranscript(sshHermesRecovery, failedRecoveryMarker, false, "powershell.exe")
            && !SshRecovery.ShouldKeepPendingRecovery(sshHermesRecovery, new SshLaunchMarker
            {
                PaneId = profile.Id,
                StartedUtc = fixtureStarted,
                WorkingDirectory = actualCodexDirectory,
                ConnectionArguments = safeSshArguments,
                RecoveryAttempt = true,
                ExitCode = 0,
                EndedUtc = DateTime.UtcNow
            }, false);
        var recoveryRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "session-recovery-fixture");
        var transcriptFile = SessionRecoveryStore.SaveTranscript("test-session", "previous terminal output", recoveryRoot);
        var recoveryFixture = new SessionRecoverySnapshot();
        recoveryFixture.Sessions["test-session"] = new SessionRecoveryEntry
        {
            SessionId = "test-session", TranscriptFile = transcriptFile, CodexWasActive = true, CodexSessionId = fixtureId,
            CodexModel = savedModel, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy,
            CodexPermissionProfile = savedPermissionProfile, CodexApprovalsReviewer = savedApprovalsReviewer,
            SshWasActive = true, SshConnectionArguments = safeSshArguments, HermesWasActive = true,
            HermesSessionId = hermesSessionId, HermesModel = hermesModel, HermesUseTui = true,
            RemoteCodexWasActive = true, RemoteCodexSessionId = remoteCodexId,
            RemoteCodexWorkingDirectory = remoteCodexDirectory, RemoteCodexModel = savedModel,
            RemoteCodexSandboxMode = savedSandboxMode, RemoteCodexApprovalPolicy = savedApprovalPolicy,
            RemoteCodexApprovalsReviewer = savedApprovalsReviewer
        };
        SessionRecoveryStore.Save(recoveryFixture, recoveryRoot);
        var reloadedFixture = SessionRecoveryStore.Load(recoveryRoot);
        var recoveryRoundTrip = reloadedFixture.Sessions.TryGetValue("test-session", out var reloadedEntry)
            && reloadedEntry.CodexWasActive && reloadedEntry.CodexSessionId == fixtureId && reloadedEntry.CodexModel == savedModel
            && reloadedEntry.CodexSandboxMode == savedSandboxMode && reloadedEntry.CodexApprovalPolicy == savedApprovalPolicy
            && reloadedEntry.CodexPermissionProfile == savedPermissionProfile
            && reloadedEntry.CodexApprovalsReviewer == savedApprovalsReviewer
            && reloadedEntry.SshWasActive && reloadedEntry.SshConnectionArguments.SequenceEqual(safeSshArguments)
            && reloadedEntry.HermesWasActive && reloadedEntry.HermesSessionId == hermesSessionId
            && reloadedEntry.HermesModel == hermesModel && reloadedEntry.HermesUseTui
            && reloadedEntry.RemoteCodexWasActive && reloadedEntry.RemoteCodexSessionId == remoteCodexId
            && reloadedEntry.RemoteCodexWorkingDirectory == remoteCodexDirectory && reloadedEntry.RemoteCodexModel == savedModel
            && reloadedEntry.RemoteCodexSandboxMode == savedSandboxMode && reloadedEntry.RemoteCodexApprovalPolicy == savedApprovalPolicy
            && reloadedEntry.RemoteCodexApprovalsReviewer == savedApprovalsReviewer
            && SessionRecoveryStore.ReadTranscript(reloadedEntry, recoveryRoot) == "previous terminal output";
        try { Directory.Delete(recoveryRoot, true); } catch { }
        var legacyRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "legacy-recovery-fixture");
        var legacyFixture = new SessionRecoverySnapshot { Version = 1 };
        legacyFixture.Sessions["legacy-session"] = new SessionRecoveryEntry { SessionId = "legacy-session", CodexWasActive = true, CodexSessionId = "99999999-8888-7777-6666-555555555555", WorkingDirectory = profile.WorkingDirectory };
        SessionRecoveryStore.Save(legacyFixture, legacyRoot);
        var migratedLegacy = SessionRecoveryStore.Load(legacyRoot);
        var unsafeLegacyIdDiscarded = migratedLegacy.Version == 10 && migratedLegacy.Sessions["legacy-session"].CodexSessionId is null;
        try { Directory.Delete(legacyRoot, true); } catch { }

        var importedCodexTranscript = $"OpenAI Codex (fixture){Environment.NewLine}model: {savedModel}{Environment.NewLine}directory: {actualCodexDirectory}{Environment.NewLine}";
        var importedCodexTab = WindowsTerminalImportPlanner.CreateTabCapture(0, "⠧ PowerShellPlus", importedCodexTranscript);
        var importedPowerShellTab = WindowsTerminalImportPlanner.CreateTabCapture(1, "Windows PowerShell", $"PS {profile.WorkingDirectory}>");
        var importedCandidate = new CodexSessionMatch(fixtureId, actualCodexDirectory, fixtureStarted, TimeSpan.Zero, fixtureStarted, savedModel, savedSandboxMode, savedApprovalPolicy, savedPermissionProfile, savedApprovalsReviewer);
        var importedWindow = new WindowsTerminalWindowCapture(IntPtr.Zero, "Windows Terminal", [importedCodexTab, importedPowerShellTab]);
        var importedPlan = WindowsTerminalImportPlanner.Create(importedWindow, [importedCandidate]);
        var importPreservesStableTabNames = importedPlan.Rows[0].Title == "PowerShellPlus" && importedPlan.Rows[1].Title == "Windows PowerShell";
        var importExtractsWorkingDirectories = string.Equals(importedPlan.Rows[0].Tab.WorkingDirectory, actualCodexDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(importedPlan.Rows[1].Tab.WorkingDirectory, profile.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        var importAutoMatchesExactCodexThread = importedPlan.Rows[0].SelectedChoice?.Session?.SessionId == fixtureId
            && importedPlan.Rows[1].SelectedChoice?.Session is null;
        var importedRecovery = WindowsTerminalImportPlanner.CreateRecoveryEntry(importedPlan.Rows[0], "imported-session", "imported-session.txt");
        var importCarriesExactCodexPermissions = importedRecovery.CodexWasActive && importedRecovery.CodexSessionId == fixtureId
            && importedRecovery.CodexModel == savedModel && importedRecovery.CodexSandboxMode == savedSandboxMode
            && importedRecovery.CodexApprovalPolicy == savedApprovalPolicy && importedRecovery.CodexPermissionProfile == savedPermissionProfile
            && importedRecovery.CodexApprovalsReviewer == savedApprovalsReviewer;
        var importedResumeScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, importedRecovery));
        var importedResumeStart = importedResumeScript.LastIndexOf("; & codex resume", StringComparison.OrdinalIgnoreCase);
        var importedResumeCommand = importedResumeStart >= 0 ? importedResumeScript[importedResumeStart..] : importedResumeScript;
        var importResumeCommandIsExact = importedResumeCommand.Contains($"codex resume '{fixtureId}' --model '{savedModel}' --config 'default_permissions=\"{savedPermissionProfile}\"' --config 'approvals_reviewer=\"{savedApprovalsReviewer}\"' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal)
            && !importedResumeCommand.Contains("--sandbox", StringComparison.OrdinalIgnoreCase);
        var secondCandidate = importedCandidate with { SessionId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff", FileModifiedUtc = fixtureStarted.AddSeconds(1) };
        var nestedCodexDirectory = Path.Combine(actualCodexDirectory, "src", "feature");
        var descendantImport = WindowsTerminalImportPlanner.Create(new WindowsTerminalWindowCapture(IntPtr.Zero, "Windows Terminal", [
            WindowsTerminalImportPlanner.CreateTabCapture(0, "Nested Codex", $"OpenAI Codex (fixture){Environment.NewLine}directory: {nestedCodexDirectory}")
        ]), [importedCandidate]);
        var descendantDirectoryMatchesSessionRoot = descendantImport.Rows[0].SelectedChoice?.Session?.SessionId == fixtureId;
        var ambiguousImport = WindowsTerminalImportPlanner.Create(new WindowsTerminalWindowCapture(IntPtr.Zero, "Windows Terminal", [
            WindowsTerminalImportPlanner.CreateTabCapture(0, "First Codex", importedCodexTranscript),
            WindowsTerminalImportPlanner.CreateTabCapture(1, "Second Codex", importedCodexTranscript)
        ]), [importedCandidate, secondCandidate]);
        var ambiguousImportRequiresChoice = ambiguousImport.Rows.All(value => value.SelectedChoice?.Session is null);
        var importedSshCapture = new WindowsTerminalSshCapture(4242, ["-i", expandedHomeIdentity, "ubuntu@15.204.82.129"], "ubuntu@15.204.82.129");
        var remoteImportTab = WindowsTerminalImportPlanner.CreateTabCapture(0, "VPS Codex",
            $"Welcome to Ubuntu{Environment.NewLine}ubuntu@15.204.82.129{Environment.NewLine}OpenAI Codex{Environment.NewLine}directory: /home/ubuntu/project");
        var remoteImportPlan = WindowsTerminalImportPlanner.Create(
            new WindowsTerminalWindowCapture(IntPtr.Zero, "Windows Terminal", [remoteImportTab], [importedSshCapture]), []);
        var remoteImportRow = remoteImportPlan.Rows[0];
        remoteImportRow.SelectedSshChoice!.RemoteCodexProbe = new RemoteCodexProbeResult(true,
            new RemoteCodexRecoveryState(true, remoteCodexId, "/home/ubuntu/project", savedModel, savedSandboxMode,
                savedApprovalPolicy, savedPermissionProfile, savedApprovalsReviewer));
        var remoteImportedRecovery = WindowsTerminalImportPlanner.CreateRecoveryEntry(remoteImportRow, "remote-import", "remote-import.txt");
        var importCapturesSshAndRemoteCodex = remoteImportRow.SelectedSshChoice.Connection?.ProcessId == 4242
            && remoteImportedRecovery.SshWasActive
            && remoteImportedRecovery.SshConnectionArguments.SequenceEqual(importedSshCapture.ConnectionArguments)
            && remoteImportedRecovery.RemoteCodexWasActive && remoteImportedRecovery.RemoteCodexSessionId == remoteCodexId
            && remoteImportedRecovery.RemoteCodexModel == savedModel && remoteImportedRecovery.RemoteCodexPermissionProfile == savedPermissionProfile;
        var remoteImportedScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, remoteImportedRecovery));
        var importRestoresSshAndRemoteCodex = remoteImportedScript.Contains("ubuntu@15.204.82.129", StringComparison.Ordinal)
            && remoteImportedScript.Contains(remoteCodexId, StringComparison.Ordinal)
            && remoteImportedScript.Contains(savedModel, StringComparison.Ordinal);
        var parsedSshCommand = WindowsTerminalImportService.TryParseSshCommandLine(4242,
            $"\"C:\\Windows\\System32\\OpenSSH\\ssh.exe\" -i \"{expandedHomeIdentity}\" ubuntu@15.204.82.129");
        var importParsesQuotedSshIdentity = parsedSshCommand?.ProcessId == 4242
            && parsedSshCommand.ConnectionArguments.SequenceEqual(importedSshCapture.ConnectionArguments);
        var bridgeArguments = RemoteClipboardImageBridge.BuildSshArgumentsForTest(importedSshCapture.ConnectionArguments, "clipboard-test.png");
        var shortImageName = RemoteClipboardImageBridge.CreateRemoteFileName(
            new DateTime(2026, 7, 24, 9, 51, 29, DateTimeKind.Utc), Guid.Parse("1a22347c-8b6b-43c7-b00a-f880775cd32a"));
        var imageBridgeIsBoundedAndSafe = bridgeArguments.Contains("BatchMode=yes", StringComparer.Ordinal)
            && bridgeArguments.Contains("ubuntu@15.204.82.129", StringComparer.Ordinal)
            && bridgeArguments[^1].Contains("umask 077", StringComparison.Ordinal)
            && bridgeArguments[^1].Contains("$HOME/.cache/powershellplus/images", StringComparison.Ordinal)
            && bridgeArguments[^1].Contains("set -C", StringComparison.Ordinal)
            && shortImageName == "img-095129-1a22347c.png" && shortImageName.Length == 23
            && RemoteClipboardImageBridge.TryReadRemotePath("PSP_REMOTE_IMAGE:/home/ubuntu/.cache/powershellplus/images/clipboard-test.png", out _)
            && !RemoteClipboardImageBridge.TryReadRemotePath("PSP_REMOTE_IMAGE:/home/ubuntu/../etc/shadow", out _);

        HideToTray();
        await Task.Delay(300);
        var hidden = !IsVisible;
        var rootWhileHidden = pane.GetRootProcessId();
        RestoreWindow(false);
        await Task.Delay(300);
        var restored = IsVisible;
        var rootAfter = pane.GetRootProcessId();
        var sameLiveProcess = rootBefore is not null && rootBefore == rootWhileHidden && rootBefore == rootAfter;
        var success = workspaceTestIsolated && hidden && restored && sameLiveProcess && normalDoesNotResumeCodex && codexResumesExactSession && codexResumesSavedModel && codexResumesSavedPermissions && codexResumesSavedPermissionProfile && unsafeModelRejected && unsafePermissionsRejected && ambiguousCodexUsesPicker && powershellWrapperInstalled
            && sshWrapperInstalled && safeSshAccepted && quotedHomeIdentityAccepted && safeSshReliabilityOptionsAccepted && unsafeSshRejected && hermesExactSessionDetected && hermesModelChangeDetected && unsafeHermesModelRejected && exitedHermesNotRestored && sshHermesExactResume && sshRecoveryIsBoundedAndVisible && sshHermesFallbackResume && unsafeHermesModelNotInjected && remoteProbeParsed && remoteCodexExactResume && unsafeRemoteProbeRejected && sshLoginOnlyRestored && unsafeSshResumeRejected
            && codexSessionMapped && latestModelMapped && latestPermissionsMapped && partialRolloutIgnored && changedDirectoryRestored && inTuiResumeRebound && liveRolloutSharedRead && launchTimeFallbackRebound && exactLaunchBindingPersisted && normalCodexExitRecorded && wrapperRecordsPaneAndLifecycle
            && sshLaunchBindingPersisted && normalSshExitRecorded && sshWrapperRecordsSafeConnectionOnly && sshWrapperExecutesSafely && sshBannerTimeoutFallsBackInteractive && failedRecoveryStateRetained && recoveryRoundTrip && unsafeLegacyIdDiscarded && importPreservesStableTabNames && importExtractsWorkingDirectories && importAutoMatchesExactCodexThread && importCarriesExactCodexPermissions && importResumeCommandIsExact && descendantDirectoryMatchesSessionRoot && ambiguousImportRequiresChoice
            && importCapturesSshAndRemoteCodex && importRestoresSshAndRemoteCodex && importParsesQuotedSshIdentity && imageBridgeIsBoundedAndSafe;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Live panes survived hide/restore; recovery resumed local and remote Codex, SSH, and Hermes with validated durable state.\nWorkspaceTestIsolated={workspaceTestIsolated}\nHidden={hidden}\nRestored={restored}\nSameLiveProcess={sameLiveProcess}\nNormalDoesNotResumeCodex={normalDoesNotResumeCodex}\nCodexResumesExactSession={codexResumesExactSession}\nCodexResumesSavedModel={codexResumesSavedModel}\nCodexResumesSavedPermissions={codexResumesSavedPermissions}\nCodexResumesSavedPermissionProfile={codexResumesSavedPermissionProfile}\nUnsafeModelRejected={unsafeModelRejected}\nUnsafePermissionsRejected={unsafePermissionsRejected}\nAmbiguousCodexUsesPicker={ambiguousCodexUsesPicker}\nPowerShellWrapperInstalled={powershellWrapperInstalled}\nSshWrapperInstalled={sshWrapperInstalled}\nSafeSshAccepted={safeSshAccepted}\nQuotedHomeIdentityAccepted={quotedHomeIdentityAccepted}\nSafeSshReliabilityOptionsAccepted={safeSshReliabilityOptionsAccepted}\nUnsafeSshRejected={unsafeSshRejected}\nHermesExactSessionDetected={hermesExactSessionDetected}\nHermesModelChangeDetected={hermesModelChangeDetected}\nUnsafeHermesModelRejected={unsafeHermesModelRejected}\nExitedHermesNotRestored={exitedHermesNotRestored}\nSshHermesExactResume={sshHermesExactResume}\nSshRecoveryIsBoundedAndVisible={sshRecoveryIsBoundedAndVisible}\nSshHermesFallbackResume={sshHermesFallbackResume}\nUnsafeHermesModelNotInjected={unsafeHermesModelNotInjected}\nRemoteProbeParsed={remoteProbeParsed}\nRemoteCodexExactResume={remoteCodexExactResume}\nUnsafeRemoteProbeRejected={unsafeRemoteProbeRejected}\nSshLoginOnlyRestored={sshLoginOnlyRestored}\nUnsafeSshResumeRejected={unsafeSshResumeRejected}\nCodexSessionMappedAcrossChangedDirectory={codexSessionMapped}\nLatestModelMapped={latestModelMapped}\nLatestPermissionsMapped={latestPermissionsMapped}\nPartialRolloutIgnored={partialRolloutIgnored}\nChangedDirectoryRestored={changedDirectoryRestored}\nInTuiResumeRebound={inTuiResumeRebound}\nLiveRolloutSharedRead={liveRolloutSharedRead}\nLaunchTimeFallbackRebound={launchTimeFallbackRebound}\nExactLaunchBindingPersisted={exactLaunchBindingPersisted}\nNormalCodexExitRecorded={normalCodexExitRecorded}\nWrapperRecordsPaneAndLifecycle={wrapperRecordsPaneAndLifecycle}\nSshLaunchBindingPersisted={sshLaunchBindingPersisted}\nNormalSshExitRecorded={normalSshExitRecorded}\nSshWrapperRecordsSafeConnectionOnly={sshWrapperRecordsSafeConnectionOnly}\nSshWrapperExecutesSafely={sshWrapperExecutesSafely}\nSshWrapperDiagnostic={sshWrapperDiagnostic}\nSshBannerTimeoutFallsBackInteractive={sshBannerTimeoutFallsBackInteractive}\nSshBannerDiagnostic={sshBannerDiagnostic}\nFailedRecoveryStateRetained={failedRecoveryStateRetained}\nRecoveryRoundTrip={recoveryRoundTrip}\nUnsafeLegacyIdDiscarded={unsafeLegacyIdDiscarded}\nImportPreservesStableTabNames={importPreservesStableTabNames}\nImportExtractsWorkingDirectories={importExtractsWorkingDirectories}\nImportAutoMatchesExactCodexThread={importAutoMatchesExactCodexThread}\nImportCarriesExactCodexPermissions={importCarriesExactCodexPermissions}\nImportResumeCommandIsExact={importResumeCommandIsExact}\nDescendantDirectoryMatchesSessionRoot={descendantDirectoryMatchesSessionRoot}\nAmbiguousImportRequiresChoice={ambiguousImportRequiresChoice}\nImportCapturesSshAndRemoteCodex={importCapturesSshAndRemoteCodex}\nImportRestoresSshAndRemoteCodex={importRestoresSshAndRemoteCodex}\nImportParsesQuotedSshIdentity={importParsesQuotedSshIdentity}\nImageBridgeIsBoundedAndSafe={imageBridgeIsBoundedAndSafe}");
        return success;
    }

    public async Task<bool> RunMultiPaneSmokeTestAsync(string reportPath)
    {
        var originalWorkspaceSession = activeWorkspaceSession;
        var originalLayout = activeWorkspaceSession?.Layout ?? "Grid";
        var originalSendAllEnabled = state.Settings.SendToAllModifierEnabled;
        var originalSendAllModifier = state.Settings.SendToAllModifier;
        var originalWorkspaceSidebarExpanded = state.WorkspaceSidebarExpanded;
        var added = new List<SessionProfile>();
        AutomationRule? countdownRefreshFixture = null;
        CommandSnippet? quickAccessFixture = null;
        // The gate must pass regardless of how many sessions the user's saved
        // workspace already contains.
        var expectedPanes = panes.Count + 3;
        try
        {
            for (var index = 2; index <= 4; index++)
            {
                var profile = new SessionProfile { Name = $"PowerShell {index}", CommandLine = terminalProfile.CommandLine, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                added.Add(profile); AddTerminalToActiveSession(profile); CreatePane(profile);
            }
            quickAccessFixture = new CommandSnippet { Name = "Queue smoke", Category = "Test", Command = "Write-Output 'QUICK_ACCESS_READY'", ShowInQuickAccess = true };
            state.Snippets.Add(quickAccessFixture);
            SetLayout("Grid");
            await Task.Delay(2600);
            SetWorkspaceSidebarExpanded(true, false);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var originalWindowWidth = Width;
            var root = (FrameworkElement)Content;
            root.UpdateLayout();
            var terminalWidthWithSidebar = TerminalHost.ActualWidth;
            SetWorkspaceSidebarExpanded(false, false);
            await Dispatcher.Yield(DispatcherPriority.Render);
            root.UpdateLayout();
            var sidebarCollapses = WorkspaceSidebar.Visibility == Visibility.Collapsed && WorkspaceSidebarColumn.ActualWidth == 0
                && TerminalHost.ActualWidth >= terminalWidthWithSidebar + WorkspaceSidebarWidth - 2;
            WorkspaceStore.Save(state);
            var sidebarStatePersists = !WorkspaceStore.Load(terminalProfile).WorkspaceSidebarExpanded;
            SetWorkspaceSidebarExpanded(true, false);
            await Dispatcher.Yield(DispatcherPriority.Render);
            root.UpdateLayout();
            var sidebarExpands = WorkspaceSidebar.Visibility == Visibility.Visible && Math.Abs(WorkspaceSidebarColumn.ActualWidth - WorkspaceSidebarWidth) <= 1
                && TerminalHost.ActualWidth <= terminalWidthWithSidebar + 2;
            var initialToolbarCenter = TerminalToolbar.TranslatePoint(new Point(TerminalToolbar.ActualWidth / 2, 0), root).X;
            var initiallyCentered = Math.Abs(initialToolbarCenter - root.ActualWidth / 2) <= 1;
            Width = Math.Max(MinWidth, ActualWidth - 260);
            await Dispatcher.Yield(DispatcherPriority.Background);
            root.UpdateLayout();
            var resizedToolbarCenter = TerminalToolbar.TranslatePoint(new Point(TerminalToolbar.ActualWidth / 2, 0), root).X;
            var centeredAfterResize = Math.Abs(resizedToolbarCenter - root.ActualWidth / 2) <= 1;
            Width = originalWindowWidth;
            await Dispatcher.Yield(DispatcherPriority.Background);
            var scrollbarsHidden = panes.Values.All(pane => pane.IsNativeScrollbarHidden());
            var activationTarget = panes[added[0].Id];
            SelectPane(panes.Values.First().Profile.Id, false);
            var paneCommandInputTakesFocus = activationTarget.FocusCommandInputForTest();
            var handoffButtonReady = activationTarget.HandoffButtonReadyForTest;
            var terminalSurfaceHooked = activationTarget.HasTerminalSurfaceActivationHook;
            var terminalInputRouterPrecedesConPty = activationTarget.TerminalInputRouterPrecedesConPtyForTest();
            var remoteImagePasteIndicatorReady = activationTarget.HasRemoteImagePasteIndicatorForTest;
            var remoteImageShortcutInterceptReady = TerminalPane.RemoteImageShortcutsClassifiedForTest();
            var remoteImagePasteModesWork = TerminalPane.RemoteImagePasteModesFormatForTest();
            var remoteImagePasteIndicatorStatesWork = activationTarget.ExerciseRemoteImagePasteIndicatorForTest();
            var terminalClickSent = activationTarget.SimulateTerminalSurfaceClickForTest();
            var terminalSurfaceActivatesPane = terminalClickSent && ReferenceEquals(activePane, activationTarget)
                && ReferenceEquals(SessionList.SelectedItem, activationTarget.Profile);
            var terminalSurfaceTakesKeyboardFocus = activationTarget.HasNativeKeyboardFocus();
            activationTarget.SetCommandInputForTest(new string('W', 900));
            activationTarget.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            activationTarget.UpdateLayout();
            var commandInputAutoGrows = activationTarget.CommandInputAutoGrowsForTest && activationTarget.CommandInputHeightForTest > 30;
            var composerChromeStaysCompact = activationTarget.ComposerChromeStaysCompactForTest;
            activationTarget.SetCommandInputForTest(string.Empty);
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Working);
            var agentWorkingStateVisible = activationTarget.AgentActivityStateForTest == AgentActivityState.Working
                && activationTarget.AgentStatusTextForTest.Contains("Codex · working", StringComparison.Ordinal);
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Waiting);
            var agentWaitingStateVisible = activationTarget.AgentActivityStateForTest == AgentActivityState.Waiting
                && activationTarget.AgentStatusTextForTest.Contains("Codex · waiting for you", StringComparison.Ordinal);
            var inputEchoDoesNotActivateAgent = TerminalPane.ActivityTrackerRejectsInputEchoForTest();
            var codexTurnEventsDriveAgent = CodexSessionLocator.ActivityRecordsClassifyForTest();
            var cursorTransformConfigured = activationTarget.ForceCursorStyleForTest("\u001b[3 q") == "\u001b[5 q";
            var windowIconLoaded = Icon is not null;
            var executableIconEmbedded = false;
            try
            {
                using var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                executableIconEmbedded = executableIcon is { Width: >= 16, Height: >= 16 };
            }
            catch { }
            var inputReady = true;
            var indexValue = 1;
            foreach (var pane in panes.Values) inputReady &= await pane.SendCommandAsync($"Write-Output 'NATIVE_PANE_{indexValue++}_READY'");
            var deadline = DateTime.UtcNow.AddSeconds(12);
            bool outputReady;
            do
            {
                await Task.Delay(180);
                outputReady = panes.Values.Select((pane, index) => pane.GetOutput().Contains($"NATIVE_PANE_{index + 1}_READY", StringComparison.Ordinal)).All(value => value);
            } while (!outputReady && DateTime.UtcNow < deadline);

            const string renameMarker = "TERMINAL_RENAME_PRESERVES_LIVE_STATE";
            var renameMarkerAccepted = await activationTarget.SendCommandAsync($"Write-Output '{renameMarker}'");
            var renameMarkerDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < renameMarkerDeadline && !activationTarget.GetOutput().Contains(renameMarker, StringComparison.Ordinal)) await Task.Delay(100);
            var rootProcessBeforeRename = activationTarget.GetRootProcessId();
            var originalTerminalName = activationTarget.Profile.Name;
            var renameTriggeredRestart = await ApplyTerminalEditAsync(activationTarget.Profile, $"{originalTerminalName} renamed",
                activationTarget.Profile.CommandLine, activationTarget.Profile.WorkingDirectory, activationTarget.Profile.AutoStart);
            var terminalRenamePreservesLiveState = renameMarkerAccepted && !renameTriggeredRestart && rootProcessBeforeRename is not null
                && activationTarget.GetRootProcessId() == rootProcessBeforeRename
                && ReferenceEquals(panes[activationTarget.Profile.Id], activationTarget)
                && activationTarget.GetOutput().Contains(renameMarker, StringComparison.Ordinal)
                && activationTarget.TitleTextForTest == $"{originalTerminalName} renamed";
            await ApplyTerminalEditAsync(activationTarget.Profile, originalTerminalName,
                activationTarget.Profile.CommandLine, activationTarget.Profile.WorkingDirectory, activationTarget.Profile.AutoStart);

            var textPasteAccepted = activationTarget.PasteTextForTest("Write-Output ('TEXT_PASTE_' + (6 * 7))");
            activationTarget.SubmitTerminalInputForTest();
            var textPasteDeadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < textPasteDeadline && !activationTarget.GetOutput().Contains("TEXT_PASTE_42", StringComparison.Ordinal)) await Task.Delay(100);
            var textPasteWorks = textPasteAccepted && activationTarget.GetOutput().Contains("TEXT_PASTE_42", StringComparison.Ordinal)
                && TerminalPane.FormatClipboardTextForTest("1281660770492485763") == "\u001b[200~1281660770492485763\u001b[201~";
            activationTarget.EnableRemoteOutputCapture();
            var cursorSequenceAccepted = await activationTarget.SendCommandAsync("[Console]::Write(([char]27).ToString() + '[3 q'); Write-Output 'CURSOR_FILTER_READY'");
            var cursorDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < cursorDeadline && !activationTarget.GetOutput().Contains("CURSOR_FILTER_READY", StringComparison.Ordinal)) await Task.Delay(100);
            var rawCursorOutput = activationTarget.GetRawOutputForTest();
            var lastBarCursor = rawCursorOutput.LastIndexOf("\u001b[5 q", StringComparison.Ordinal);
            var lastUnderlineCursor = rawCursorOutput.LastIndexOf("\u001b[3 q", StringComparison.Ordinal);
            var cursorCommandCompleted = activationTarget.GetOutput().Contains("CURSOR_FILTER_READY", StringComparison.Ordinal);
            // ConPTY may consume DECSCUSR as terminal state instead of echoing it
            // into the passive output log, so gate the deterministic interceptor
            // transform plus a completed live command rather than log presence.
            var cursorBarEnforced = cursorTransformConfigured && cursorSequenceAccepted && cursorCommandCompleted;

            activationTarget.SetCommandBarExpandedForTest(false);
            var commandBarCollapses = !activationTarget.CommandBarExpandedForTest;
            WorkspaceStore.Save(state);
            var collapsedState = WorkspaceStore.Load(terminalProfile);
            var commandBarStatePersists = collapsedState.Sessions.First(value => value.Id == activationTarget.Profile.Id).CommandBarExpanded == false;
            var quickAccessTogglePersists = collapsedState.Snippets.First(value => value.Id == quickAccessFixture.Id).ShowInQuickAccess;
            activationTarget.SetCommandBarExpandedForTest(true);
            var commandBarExpands = activationTarget.CommandBarExpandedForTest;
            var quickAccessPopulatesInput = activationTarget.SelectFirstQuickAccessCommandForTest();
            for (var queueIndex = 1; queueIndex <= 18; queueIndex++)
            {
                activationTarget.SetCommandInputForTest($"Write-Output 'QUEUE_MENU_{queueIndex}'");
                activationTarget.QueueCommandForTest();
            }
            var queueMenuListsCommands = activationTarget.QueuedCommandCountForTest == 18
                && activationTarget.QueueCountTextForTest == "18"
                && activationTarget.OpenQueueMenuForTest() == 18
                && activationTarget.QueueMenuMaxHeightForTest == 300
                && activationTarget.SelectQueuedCommandForTest(12)
                && activationTarget.CommandInputTextForTest.Contains("QUEUE_MENU_13", StringComparison.Ordinal);
            activationTarget.ClearQueuedCommandsForTest();
            activationTarget.SetCommandInputForTest("Write-Output 'QUEUE_FIRST'"); activationTarget.QueueCommandForTest();
            activationTarget.SetCommandInputForTest("Write-Output 'QUEUE_SECOND'"); activationTarget.QueueCommandForTest();
            var queueAddsCommands = activationTarget.QueuedCommandCountForTest == 2 && activationTarget.CommandInputTextForTest.Length == 0;
            WorkspaceStore.Save(state);
            var queueStatePersists = WorkspaceStore.Load(terminalProfile).Sessions.First(value => value.Id == activationTarget.Profile.Id).PendingCommands.SequenceEqual(activationTarget.Profile.PendingCommands);
            activationTarget.SetCommandInputForTest("Write-Output 'QUEUE_NOW'");
            var currentCommandRuns = await activationTarget.RunCommandForTestAsync();
            var nextQueuedCommandPromoted = activationTarget.QueuedCommandCountForTest == 2 && activationTarget.CommandInputTextForTest.Contains("QUEUE_FIRST", StringComparison.Ordinal);
            activationTarget.NavigateQueueForTest(-1);
            var upArrowBrowsesQueue = activationTarget.CommandInputTextForTest.Contains("QUEUE_FIRST", StringComparison.Ordinal);
            var firstQueuedCommandRuns = await activationTarget.RunCommandForTestAsync();
            var queueAdvances = activationTarget.QueuedCommandCountForTest == 1 && activationTarget.CommandInputTextForTest.Contains("QUEUE_SECOND", StringComparison.Ordinal);
            var secondQueuedCommandRuns = await activationTarget.RunCommandForTestAsync();
            var queueDrains = activationTarget.QueuedCommandCountForTest == 0 && activationTarget.CommandInputTextForTest.Length == 0;
            var ctrlEnterQueues = await activationTarget.QueueWithCtrlEnterForTestAsync("Write-Output 'CTRL_ENTER_QUEUE'");
            var queueButtonOpensQueue = activationTarget.ClickQueueButtonForTest() == 1;
            activationTarget.ClearQueuedCommandsForTest();
            var quickAccessFiltersCommands = activationTarget.QuickAccessCommandCountForTest == state.Snippets.Count(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command));
            var queueOutputDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < queueOutputDeadline && (!activationTarget.GetOutput().Contains("QUEUE_NOW", StringComparison.Ordinal)
                || !activationTarget.GetOutput().Contains("QUEUE_FIRST", StringComparison.Ordinal) || !activationTarget.GetOutput().Contains("QUEUE_SECOND", StringComparison.Ordinal))) await Task.Delay(120);
            var queueCommandsExecuted = activationTarget.GetOutput().Contains("QUEUE_NOW", StringComparison.Ordinal)
                && activationTarget.GetOutput().Contains("QUEUE_FIRST", StringComparison.Ordinal) && activationTarget.GetOutput().Contains("QUEUE_SECOND", StringComparison.Ordinal);

            state.Settings.SendToAllModifierEnabled = true;
            state.Settings.SendToAllModifier = "Shift";
            activationTarget.RefreshCommandRoutingAppearance();
            var shiftModifierRoutesAll = activationTarget.SendToAllActiveForTest(ModifierKeys.Shift)
                && !activationTarget.SendToAllActiveForTest(ModifierKeys.Control);
            activationTarget.SetSendToAllVisualForTest(true);
            var sendAllVisualFeedback = activationTarget.SendCommandGlyphForTest == "⇉"
                && activationTarget.SendCommandToolTipForTest.Contains("all terminals", StringComparison.OrdinalIgnoreCase);
            activationTarget.SetSendToAllVisualForTest(false);
            state.Settings.SendToAllModifierEnabled = false;
            var modifierCanBeDisabled = !activationTarget.SendToAllActiveForTest(ModifierKeys.Shift);
            state.Settings.SendToAllModifierEnabled = true;
            state.Settings.SendToAllModifier = "Alt";
            var modifierCanBeRemapped = activationTarget.SendToAllActiveForTest(ModifierKeys.Alt)
                && !activationTarget.SendToAllActiveForTest(ModifierKeys.Shift);
            WorkspaceStore.Save(state);
            var sendAllSettingsPersist = WorkspaceStore.Load(terminalProfile).Settings is { SendToAllModifierEnabled: true, SendToAllModifier: "Alt" };
            state.Settings.SendToAllModifier = "Shift";
            activationTarget.SetCommandInputForTest("Write-Output 'SEND_ALL_READY'");
            var allCommandAccepted = await activationTarget.RunCommandForTestAsync(true);
            var allCommandDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < allCommandDeadline && panes.Values.Any(pane => !pane.GetOutput().Contains("SEND_ALL_READY", StringComparison.Ordinal))) await Task.Delay(120);
            var commandReachedAllPanes = allCommandAccepted && panes.Values.All(pane => pane.GetOutput().Contains("SEND_ALL_READY", StringComparison.Ordinal));

            SetLayout("Rows"); var rows = TerminalHost.Children.OfType<TerminalPane>().Count() == expectedPanes && TerminalHost.Children.OfType<GridSplitter>().Any();
            SetLayout("Columns"); var columns = TerminalHost.Children.OfType<TerminalPane>().Count() == expectedPanes && TerminalHost.Children.OfType<GridSplitter>().Any();
            SelectPane(state.Sessions[0].Id, false); SetLayout("Focus"); var focus = TerminalHost.Children.OfType<TerminalPane>().Count() == 1;
            SetLayout("Grid"); var grid = TerminalHost.Children.OfType<TerminalPane>().Count() == expectedPanes && TerminalHost.Children.OfType<GridSplitter>().Any();

            var primaryWorkspaceSession = activeWorkspaceSession!;
            var movedTerminal = added[^1];
            primaryWorkspaceSession.TerminalIds.Remove(movedTerminal.Id);
            var alternateWorkspaceSession = new TerminalSession
            {
                Name = "Smoke alternate session",
                Layout = "Columns",
                TerminalIds = [movedTerminal.Id],
                ActiveTerminalId = movedTerminal.Id
            };
            state.TerminalSessions.Add(alternateWorkspaceSession);
            BeginWorkspaceSessionHoverPreviewForTest(alternateWorkspaceSession);
            await Task.Delay(650);
            var hoverPreviewSwitchesAfterDelay = WorkspaceSessionHoverPreviewActiveForTest
                && ReferenceEquals(activeWorkspaceSession, alternateWorkspaceSession)
                && state.ActiveTerminalSessionId == primaryWorkspaceSession.Id
                && TerminalHost.Children.OfType<TerminalPane>().Count() == 1;
            EndWorkspaceSessionHoverPreviewForTest();
            var hoverPreviewRestoresOnLeave = !WorkspaceSessionHoverPreviewActiveForTest
                && ReferenceEquals(activeWorkspaceSession, primaryWorkspaceSession)
                && state.ActiveTerminalSessionId == primaryWorkspaceSession.Id
                && TerminalHost.Children.OfType<TerminalPane>().Count() == expectedPanes - 1;
            SelectWorkspaceSession(alternateWorkspaceSession.Id, false);
            var sessionSwitchShowsOwnedTerminals = TerminalHost.Children.OfType<TerminalPane>().Count() == 1
                && ReferenceEquals(activePane, panes[movedTerminal.Id]);
            SetLayout("Columns");
            SelectWorkspaceSession(primaryWorkspaceSession.Id, false);
            SetLayout("Rows");
            SelectWorkspaceSession(alternateWorkspaceSession.Id, false);
            var layoutsStayPerSession = alternateWorkspaceSession.Layout == "Columns" && primaryWorkspaceSession.Layout == "Rows";
            WorkspaceStore.Save(state);
            var persistedWorkspace = WorkspaceStore.Load(terminalProfile);
            var sessionContainersPersist = persistedWorkspace.Version == 7
                && persistedWorkspace.TerminalSessions.Any(value => value.Id == alternateWorkspaceSession.Id
                    && value.TerminalIds.SequenceEqual([movedTerminal.Id]) && value.Layout == "Columns");
            var legacySessionsMigrateWithoutLosingTerminals = WorkspaceStore.VerifyLegacySessionMigrationForTest(
                terminalProfile, Path.Combine(WorkspaceStore.DirectoryPath, "legacy-v6-migration"));
            alternateWorkspaceSession.TerminalIds.Remove(movedTerminal.Id);
            primaryWorkspaceSession.TerminalIds.Add(movedTerminal.Id);
            state.TerminalSessions.Remove(alternateWorkspaceSession);
            SelectWorkspaceSession(primaryWorkspaceSession.Id, false);
            SetLayout("Grid");

            var scheduleNow = new DateTime(2026, 7, 12, 20, 36, 30, DateTimeKind.Local);
            var dailyRule = new AutomationRule { Command = "Write-Output daily", ScheduleType = "Daily", DailyTime = "20:36", LastRunUtc = scheduleNow.AddDays(-1).ToUniversalTime() };
            var repeatedDailyRule = new AutomationRule { Command = "Write-Output daily", ScheduleType = "Daily", DailyTime = "20:36", LastRunUtc = scheduleNow.ToUniversalTime() };
            var onceRule = new AutomationRule { Command = "Write-Output once", ScheduleType = "Once", ScheduledDate = "2026-07-12", DailyTime = "20:36", LastRunUtc = scheduleNow.ToUniversalTime() };
            var futureOnceRule = new AutomationRule { Command = "Write-Output once", ScheduleType = "Once", ScheduledDate = "2026-07-12", DailyTime = "20:37", LastRunUtc = scheduleNow.ToUniversalTime() };
            var scheduleLogic = dailyRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && !repeatedDailyRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && onceRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && !futureOnceRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow);
            var countdownLogic = AutomationRule.FormatCountdown(TimeSpan.FromSeconds(61)) == "1m 1s"
                && AutomationRule.FormatCountdown(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10)) == "23h 1m 10s"
                && AutomationRule.FormatCountdown(TimeSpan.FromDays(1) + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30)) == "1d 2h";

            countdownRefreshFixture = new AutomationRule { Name = "Countdown refresh fixture", Enabled = false };
            state.Automations.Add(countdownRefreshFixture);
            ShowSection(AutomationPanel);
            AutomationList.UpdateLayout();
            var automationContainerBefore = AutomationList.ItemContainerGenerator.ContainerFromItem(countdownRefreshFixture);
            var countdownNotified = false;
            countdownRefreshFixture.PropertyChanged += (_, args) => countdownNotified |= args.PropertyName == nameof(AutomationRule.Countdown);
            RefreshAutomationCountdowns();
            AutomationList.UpdateLayout();
            var automationContainerAfter = AutomationList.ItemContainerGenerator.ContainerFromItem(countdownRefreshFixture);
            var automationHoverContainerStable = countdownNotified && automationContainerBefore is not null && ReferenceEquals(automationContainerBefore, automationContainerAfter);

            ShowSection(SessionsPanel);
            SelectPane(activationTarget.Profile.Id, false);
            selectedEditableValue = activationTarget.Profile;
            var f2OpensTerminalEditor = TryOpenSelectedEditor() && editorMode == EditorMode.Terminal
                && ReferenceEquals(editingValue, activationTarget.Profile) && EditorOverlay.Visibility == Visibility.Visible;
            var editorCardKeepsEditorOpen = !TryDismissEditorFromBackdrop(EditorCard) && EditorOverlay.Visibility == Visibility.Visible;
            var backdropDismissesEditor = TryDismissEditorFromBackdrop(EditorOverlay)
                && EditorOverlay.Visibility == Visibility.Collapsed && TerminalHost.Visibility == Visibility.Visible;
            var nativeTerminalF2OpensEditor = activationTarget.TriggerEditShortcutForTest()
                && editorMode == EditorMode.Terminal && ReferenceEquals(editingValue, activationTarget.Profile)
                && EditorOverlay.Visibility == Visibility.Visible;
            HideEditor();
            selectedEditableValue = primaryWorkspaceSession;
            var f2OpensSessionEditor = TryOpenSelectedEditor() && editorMode == EditorMode.WorkspaceSession
                && ReferenceEquals(editingValue, primaryWorkspaceSession);
            HideEditor();
            ShowSection(CommandsPanel);
            SnippetList.SelectedItem = quickAccessFixture;
            var f2OpensCommandEditor = TryOpenSelectedEditor() && editorMode == EditorMode.Snippet
                && ReferenceEquals(editingValue, quickAccessFixture);
            HideEditor();
            ShowSection(AutomationPanel);
            AutomationList.SelectedItem = countdownRefreshFixture;
            var f2OpensAutomationEditor = TryOpenSelectedEditor() && editorMode == EditorMode.Automation
                && ReferenceEquals(editingValue, countdownRefreshFixture);
            HideEditor();
            var f2OpensSelectedEditors = f2OpensTerminalEditor && nativeTerminalF2OpensEditor && f2OpensSessionEditor && f2OpensCommandEditor && f2OpensAutomationEditor;
            ShowSection(SessionsPanel);

            var paneCommandSystem = paneCommandInputTakesFocus && handoffButtonReady && commandBarCollapses && commandBarStatePersists && commandBarExpands && queueAddsCommands && queueStatePersists && currentCommandRuns
                && nextQueuedCommandPromoted && upArrowBrowsesQueue && firstQueuedCommandRuns && queueAdvances && secondQueuedCommandRuns && queueDrains
                && quickAccessFiltersCommands && quickAccessTogglePersists && quickAccessPopulatesInput && queueCommandsExecuted && queueMenuListsCommands
                && ctrlEnterQueues && queueButtonOpensQueue && commandInputAutoGrows && composerChromeStaysCompact && textPasteWorks && cursorBarEnforced
                && shiftModifierRoutesAll && sendAllVisualFeedback && modifierCanBeDisabled && modifierCanBeRemapped && sendAllSettingsPersist && commandReachedAllPanes;
            var titleLayoutControlsCentered = initiallyCentered && centeredAfterResize;
            var success = inputReady && outputReady && scrollbarsHidden && titleLayoutControlsCentered && sidebarCollapses && sidebarExpands && sidebarStatePersists
                && terminalSurfaceHooked && terminalInputRouterPrecedesConPty && remoteImagePasteIndicatorReady && remoteImageShortcutInterceptReady && remoteImagePasteModesWork && remoteImagePasteIndicatorStatesWork
                && terminalSurfaceActivatesPane && terminalSurfaceTakesKeyboardFocus && windowIconLoaded && executableIconEmbedded
                && rows && columns && focus && grid && hoverPreviewSwitchesAfterDelay && hoverPreviewRestoresOnLeave
                && sessionSwitchShowsOwnedTerminals && layoutsStayPerSession && sessionContainersPersist && legacySessionsMigrateWithoutLosingTerminals
                && agentWorkingStateVisible && agentWaitingStateVisible && inputEchoDoesNotActivateAgent && codexTurnEventsDriveAgent
                && scheduleLogic && countdownLogic && automationHoverContainerStable && terminalRenamePreservesLiveState
                && f2OpensSelectedEditors && editorCardKeepsEditorOpen && backdropDismissesEditor && paneCommandSystem;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Native panes accepted responsive input, hover-previewed Session containers, per-Session layouts, agent state animation, compact multiline composition, and scheduler behavior.\nInputReady={inputReady}\nOutputReady={outputReady}\nScrollbarsHidden={scrollbarsHidden}\nTitleLayoutControlsCentered={titleLayoutControlsCentered}\nSidebarCollapses={sidebarCollapses}\nSidebarExpands={sidebarExpands}\nSidebarStatePersists={sidebarStatePersists}\nPaneCommandInputTakesFocus={paneCommandInputTakesFocus}\nTerminalSurfaceHooked={terminalSurfaceHooked}\nTerminalSurfaceActivatesPane={terminalSurfaceActivatesPane}\nTerminalSurfaceTakesKeyboardFocus={terminalSurfaceTakesKeyboardFocus}\nCommandInputAutoGrows={commandInputAutoGrows}\nComposerChromeStaysCompact={composerChromeStaysCompact}\nAgentWorkingStateVisible={agentWorkingStateVisible}\nAgentWaitingStateVisible={agentWaitingStateVisible}\nHoverPreviewSwitchesAfterDelay={hoverPreviewSwitchesAfterDelay}\nHoverPreviewRestoresOnLeave={hoverPreviewRestoresOnLeave}\nSessionSwitchShowsOwnedTerminals={sessionSwitchShowsOwnedTerminals}\nLayoutsStayPerSession={layoutsStayPerSession}\nSessionContainersPersist={sessionContainersPersist}\nLegacySessionsMigrateWithoutLosingTerminals={legacySessionsMigrateWithoutLosingTerminals}\nTextPasteWorks={textPasteWorks}\nCursorTransformConfigured={cursorTransformConfigured}\nCursorSequenceAccepted={cursorSequenceAccepted}\nCursorCommandCompleted={cursorCommandCompleted}\nLastBarCursor={lastBarCursor}\nLastUnderlineCursor={lastUnderlineCursor}\nCursorBarEnforced={cursorBarEnforced}\nCommandBarCollapses={commandBarCollapses}\nCommandBarStatePersists={commandBarStatePersists}\nCommandBarExpands={commandBarExpands}\nQueueAddsCommands={queueAddsCommands}\nQueueMenuListsCommands={queueMenuListsCommands}\nQueueStatePersists={queueStatePersists}\nCtrlEnterQueues={ctrlEnterQueues}\nQueueButtonOpensQueue={queueButtonOpensQueue}\nCurrentCommandRuns={currentCommandRuns}\nNextQueuedCommandPromoted={nextQueuedCommandPromoted}\nUpArrowBrowsesQueue={upArrowBrowsesQueue}\nQueueAdvances={queueAdvances}\nQueueDrains={queueDrains}\nQuickAccessFiltersCommands={quickAccessFiltersCommands}\nQuickAccessTogglePersists={quickAccessTogglePersists}\nQuickAccessPopulatesInput={quickAccessPopulatesInput}\nQueueCommandsExecuted={queueCommandsExecuted}\nShiftModifierRoutesAll={shiftModifierRoutesAll}\nSendAllVisualFeedback={sendAllVisualFeedback}\nModifierCanBeDisabled={modifierCanBeDisabled}\nModifierCanBeRemapped={modifierCanBeRemapped}\nSendAllSettingsPersist={sendAllSettingsPersist}\nCommandReachedAllPanes={commandReachedAllPanes}\nWindowIconLoaded={windowIconLoaded}\nExecutableIconEmbedded={executableIconEmbedded}\nGrid={grid}\nRows={rows}\nColumns={columns}\nFocus={focus}\nExactSchedules={scheduleLogic}\nCountdownFormatting={countdownLogic}\nAutomationHoverContainerStable={automationHoverContainerStable}");
            File.AppendAllText(reportPath, $"\nInputEchoDoesNotActivateAgent={inputEchoDoesNotActivateAgent}\nCodexTurnEventsDriveAgent={codexTurnEventsDriveAgent}");
            File.AppendAllText(reportPath, $"\nTerminalRenamePreservesLiveState={terminalRenamePreservesLiveState}\nF2OpensSelectedEditors={f2OpensSelectedEditors}\nEditorCardKeepsEditorOpen={editorCardKeepsEditorOpen}\nBackdropDismissesEditor={backdropDismissesEditor}\nTerminalInputRouterPrecedesConPty={terminalInputRouterPrecedesConPty}\nRemoteImagePasteIndicatorReady={remoteImagePasteIndicatorReady}\nRemoteImageShortcutInterceptReady={remoteImageShortcutInterceptReady}\nRemoteImagePasteModesWork={remoteImagePasteModesWork}\nRemoteImagePasteIndicatorStatesWork={remoteImagePasteIndicatorStatesWork}");
            if (!success)
            {
                var paneIndex = 0;
                foreach (var value in panes.Values)
                {
                    var output = value.GetOutput();
                    File.AppendAllText(reportPath, $"\n\n--- Pane {++paneIndex}: {value.Profile.Name}; Started={value.GetRootProcessId() is not null} ---\n{(output.Length <= 5000 ? output : output[^5000..])}");
                }
            }
            File.AppendAllText(reportPath, $"\nHandoffButtonReady={handoffButtonReady}");
            return success;
        }
        finally
        {
            if (countdownRefreshFixture is not null) state.Automations.Remove(countdownRefreshFixture);
            if (quickAccessFixture is not null) state.Snippets.Remove(quickAccessFixture);
            state.Settings.SendToAllModifierEnabled = originalSendAllEnabled;
            state.Settings.SendToAllModifier = originalSendAllModifier;
            state.WorkspaceSidebarExpanded = originalWorkspaceSidebarExpanded;
            ApplyWorkspaceSidebarState(false);
            foreach (var profile in added)
            {
                panes[profile.Id].Stop(); TerminalHost.Children.Remove(panes[profile.Id]); panes.Remove(profile.Id); state.Sessions.Remove(profile);
                foreach (var session in state.TerminalSessions) session.TerminalIds.Remove(profile.Id);
            }
            if (originalWorkspaceSession is not null)
            {
                originalWorkspaceSession.Layout = originalLayout;
                SelectWorkspaceSession(originalWorkspaceSession.Id, false);
            }
            else ApplyLayout();
        }
    }

    private void ShowSection(Grid panel)
    {
        SessionsPanel.Visibility = Visibility.Collapsed; CommandsPanel.Visibility = Visibility.Collapsed; AutomationPanel.Visibility = Visibility.Collapsed; SettingsPanel.Visibility = Visibility.Collapsed; panel.Visibility = Visibility.Visible;
        SessionsRail.Tag = panel == SessionsPanel ? "Active" : null;
        CommandsRail.Tag = panel == CommandsPanel ? "Active" : null;
        AutomationRail.Tag = panel == AutomationPanel ? "Active" : null;
        SettingsRail.Tag = panel == SettingsPanel ? "Active" : null;
    }

    private void WorkspaceSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        SetWorkspaceSidebarExpanded(!state.WorkspaceSidebarExpanded, true);
        e.Handled = true;
    }
    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void SessionsSectionClick(object sender, RoutedEventArgs e) => ShowSection(SessionsPanel);
    private void CommandsSectionClick(object sender, RoutedEventArgs e) => ShowSection(CommandsPanel);
    private void AutomationSectionClick(object sender, RoutedEventArgs e) => ShowSection(AutomationPanel);
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionProfile value) return;
        SelectPane(value.Id, false);
        selectedEditableValue = value;
    }
    private void NewSessionClick(object sender, RoutedEventArgs e) => OpenSessionEditor(null);
    private void WorkspaceSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (workspaceSessionSelectionSync || sender is not ListBox list || list.SelectedItem is not TerminalSession value) return;
        CancelWorkspaceSessionHoverPreview(false);
        SelectWorkspaceSession(value.Id, false);
        selectedEditableValue = value;
    }
    private void WorkspaceSessionCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TerminalSession session }) return;
        BeginWorkspaceSessionHoverPreview(session);
    }
    private void WorkspaceSessionCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalSession session } && workspaceSessionHoverCandidate != session
            && (!workspaceSessionHoverPreviewActive || activeWorkspaceSession != session)) return;
        CancelWorkspaceSessionHoverPreview(true);
    }
    private void BeginWorkspaceSessionHoverPreview(TerminalSession session)
    {
        workspaceSessionHoverTimer.Stop();
        workspaceSessionHoverCandidate = session;
        if (session.Id == state.ActiveTerminalSessionId) return;
        workspaceSessionHoverTimer.Start();
    }
    private void WorkspaceSessionHoverTimerTick(object? sender, EventArgs e)
    {
        workspaceSessionHoverTimer.Stop();
        var candidate = workspaceSessionHoverCandidate;
        if (candidate is null || candidate.Id == state.ActiveTerminalSessionId) return;
        workspaceSessionHoverOrigin = state.TerminalSessions.FirstOrDefault(value => value.Id == state.ActiveTerminalSessionId);
        if (workspaceSessionHoverOrigin is null) return;
        CaptureLayoutSizing();
        activeLayoutSizeKey = null;
        workspaceSessionHoverPreviewActive = true;
        DisplayWorkspaceSession(candidate, false, false);
    }
    private void CancelWorkspaceSessionHoverPreview(bool restoreOrigin)
    {
        workspaceSessionHoverTimer.Stop();
        var origin = workspaceSessionHoverOrigin;
        var wasPreviewing = workspaceSessionHoverPreviewActive;
        workspaceSessionHoverCandidate = null;
        workspaceSessionHoverOrigin = null;
        workspaceSessionHoverPreviewActive = false;
        if (!restoreOrigin || !wasPreviewing || origin is null) return;
        activeLayoutSizeKey = null;
        DisplayWorkspaceSession(origin, false, false);
        UpdateStatus($"{origin.Name} · {origin.Subtitle}");
    }
    internal void BeginWorkspaceSessionHoverPreviewForTest(TerminalSession session) => BeginWorkspaceSessionHoverPreview(session);
    internal void EndWorkspaceSessionHoverPreviewForTest() => CancelWorkspaceSessionHoverPreview(true);
    internal bool WorkspaceSessionHoverPreviewActiveForTest => workspaceSessionHoverPreviewActive;
    private void NewWorkspaceSessionClick(object sender, RoutedEventArgs e)
    {
        var session = new TerminalSession { Name = $"Session {state.TerminalSessions.Count + 1}" };
        state.TerminalSessions.Add(session);
        SelectWorkspaceSession(session.Id, false);
        var terminal = new SessionProfile
        {
            Name = terminalProfile.ProfileName,
            CommandLine = DefaultSessionCommandLine,
            WorkingDirectory = DefaultSessionDirectory,
            AutoStart = true
        };
        AddTerminalToActiveSession(terminal);
        CreatePane(terminal);
        SelectPane(terminal.Id, true);
        ApplyLayout();
        RefreshWorkspaceSessionViews();
        UpdateStatus($"Created {session.Name} with a new terminal");
        ScheduleSave();
    }
    private void OpenWorkspaceSessionEditor(TerminalSession value)
    {
        editorMode = EditorMode.WorkspaceSession;
        editingValue = value;
        EditorTitle.Text = "Rename session";
        WorkspaceSessionNameEdit.Text = value.Name;
        ShowEditor(WorkspaceSessionEditor);
        WorkspaceSessionNameEdit.Focus();
        WorkspaceSessionNameEdit.SelectAll();
    }
    private void WorkspaceSessionRenameClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSender<TerminalSession>(sender) is { } value) OpenWorkspaceSessionEditor(value);
    }
    private void WorkspaceSessionRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSender<TerminalSession>(sender) is not { } value) return;
        if (state.TerminalSessions.Count <= 1)
        {
            PowerShellPlusDialog.ShowMessage(this, "Keep at least one Session. You can remove or replace its Terminals instead.", "Session required", PowerShellPlusDialogKind.Information);
            return;
        }
        var terminalCount = value.TerminalIds.Count;
        if (!PowerShellPlusDialog.Confirm(this,
                $"Remove {value.Name}?\n\nIts {terminalCount} live terminal{(terminalCount == 1 ? string.Empty : "s")} will be closed. Other Sessions keep running.",
                "Remove session?", PowerShellPlusDialogKind.Question, "Remove", "Cancel", true, true)) return;
        foreach (var terminalId in value.TerminalIds.ToArray())
            if (state.Sessions.FirstOrDefault(item => item.Id == terminalId) is { } profile)
                RemoveSession(profile, true);
        state.TerminalSessions.Remove(value);
        var next = state.TerminalSessions.First();
        SelectWorkspaceSession(next.Id, false);
        RefreshWorkspaceSessionViews();
        ScheduleSave();
    }
    private void EditSessionClick(object sender, RoutedEventArgs e) { if (SessionList.SelectedItem is SessionProfile value) OpenSessionEditor(value); }
    private async void RestartSessionClick(object sender, RoutedEventArgs e) { if (activePane is not null) await activePane.RestartAsync(); }
    private void RemoveSessionClick(object sender, RoutedEventArgs e) { if (SessionList.SelectedItem is SessionProfile value) RemoveSession(value); }
    private void MoveSessionUpClick(object sender, RoutedEventArgs e) => MoveSelectedSession(-1);
    private void MoveSessionDownClick(object sender, RoutedEventArgs e) => MoveSelectedSession(1);
    private void MoveSelectedSession(int offset)
    {
        if (SessionList.SelectedItem is SessionProfile value) MoveSession(value, offset);
    }
    private void MoveSession(SessionProfile value, int offset)
    {
        if (activeWorkspaceSession is null) return;
        var current = activeWorkspaceSession.TerminalIds.IndexOf(value.Id); var target = current + offset;
        if (current < 0 || target < 0 || target >= activeWorkspaceSession.TerminalIds.Count) return;
        activeWorkspaceSession.TerminalIds.RemoveAt(current);
        activeWorkspaceSession.TerminalIds.Insert(target, value.Id);
        RefreshActiveTerminalList(); ApplyLayout(); SessionList.SelectedItem = value; ScheduleSave(); UpdateStatus($"Moved {value.Name}");
    }
    private static T? ItemFromSender<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;
    private void SelectCard(object? value)
    {
        switch (value)
        {
            case TerminalSession workspaceSession:
                CancelWorkspaceSessionHoverPreview(false);
                SelectWorkspaceSession(workspaceSession.Id, false);
                break;
            case SessionProfile session: SessionList.SelectedItem = session; break;
            case CommandSnippet snippet: SnippetList.SelectedItem = snippet; break;
            case AutomationRule automation: AutomationList.SelectedItem = automation; break;
        }
        selectedEditableValue = value;
    }
    private void CardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement card) SelectCard(card.DataContext);
    }
    private void OpenCardMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FrameworkElement card || card.ContextMenu is not ContextMenu menu) return;
        SelectCard(card.DataContext);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.HorizontalOffset = 8;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;
        e.Handled = true;
    }
    private void CardContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = null;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
    }
    private void SessionItemEditClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) { SessionList.SelectedItem = value; OpenSessionEditor(value); } }
    private async void SessionItemRestartClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value && panes.TryGetValue(value.Id, out var pane)) { SessionList.SelectedItem = value; await pane.RestartAsync(); } }
    private void SessionItemUpClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) MoveSession(value, -1); }
    private void SessionItemDownClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) MoveSession(value, 1); }
    private void SessionItemRemoveClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) { SessionList.SelectedItem = value; RemoveSession(value); } }
    private void NewSnippetClick(object sender, RoutedEventArgs e) => OpenSnippetEditor(null);
    private void EditSnippetClick(object sender, RoutedEventArgs e) { if (SnippetList.SelectedItem is CommandSnippet value) OpenSnippetEditor(value); }
    private void DeleteSnippetClick(object sender, RoutedEventArgs e) { if (SnippetList.SelectedItem is CommandSnippet value) { state.Snippets.Remove(value); ScheduleSave(); } }
    private void SnippetDoubleClick(object sender, MouseButtonEventArgs e) => RunSnippet(false);
    private void RunSnippetClick(object sender, RoutedEventArgs e) => RunSnippet(false);
    private void RunSnippetAllClick(object sender, RoutedEventArgs e) => RunSnippet(true);
    private void SnippetItemRunClick(object sender, RoutedEventArgs e) { if (ItemFromSender<CommandSnippet>(sender) is { } value) { SnippetList.SelectedItem = value; RunSnippet(false); } }
    private void SnippetItemRunAllClick(object sender, RoutedEventArgs e) { if (ItemFromSender<CommandSnippet>(sender) is { } value) { SnippetList.SelectedItem = value; RunSnippet(true); } }
    private void SnippetItemEditClick(object sender, RoutedEventArgs e) { if (ItemFromSender<CommandSnippet>(sender) is { } value) { SnippetList.SelectedItem = value; OpenSnippetEditor(value); } }
    private void SnippetItemDeleteClick(object sender, RoutedEventArgs e) { if (ItemFromSender<CommandSnippet>(sender) is { } value) { state.Snippets.Remove(value); ScheduleSave(); } }
    private void NewAutomationClick(object sender, RoutedEventArgs e) => OpenAutomationEditor(null);
    private void EditAutomationClick(object sender, RoutedEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) OpenAutomationEditor(value); }
    private void DeleteAutomationClick(object sender, RoutedEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) { state.Automations.Remove(value); ScheduleSave(); } }
    private void ToggleAutomationClick(object sender, RoutedEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) ToggleAutomation(value); }
    private void ToggleAutomation(AutomationRule value) { value.Enabled = !value.Enabled; if (value.Enabled && value.ScheduleType == "Once") value.HasRun = false; value.NotifyDisplayChanged(); ScheduleSave(); UpdateStatus(value.Enabled ? $"Enabled {value.Name}" : $"Paused {value.Name}"); }
    private async void AutomationDoubleClick(object sender, MouseButtonEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) await RunAutomationAsync(value, true); }
    private async void RunAutomationClick(object sender, RoutedEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) await RunAutomationAsync(value, true); }
    private async void TestAutomationClick(object sender, RoutedEventArgs e) { if (AutomationList.SelectedItem is AutomationRule value) await RunAutomationAsync(value, false); }
    private async void AutomationItemRunClick(object sender, RoutedEventArgs e) { if (ItemFromSender<AutomationRule>(sender) is { } value) { AutomationList.SelectedItem = value; await RunAutomationAsync(value, true); } }
    private async void AutomationItemTestClick(object sender, RoutedEventArgs e) { if (ItemFromSender<AutomationRule>(sender) is { } value) { AutomationList.SelectedItem = value; await RunAutomationAsync(value, false); } }
    private void AutomationItemToggleClick(object sender, RoutedEventArgs e) { if (ItemFromSender<AutomationRule>(sender) is { } value) { AutomationList.SelectedItem = value; ToggleAutomation(value); } }
    private void AutomationItemEditClick(object sender, RoutedEventArgs e) { if (ItemFromSender<AutomationRule>(sender) is { } value) { AutomationList.SelectedItem = value; OpenAutomationEditor(value); } }
    private void AutomationItemDeleteClick(object sender, RoutedEventArgs e) { if (ItemFromSender<AutomationRule>(sender) is { } value) { state.Automations.Remove(value); ScheduleSave(); } }
    private void EditableListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: not null } list) selectedEditableValue = list.SelectedItem;
    }
    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || EditorOverlay.Visibility == Visibility.Visible) return;
        e.Handled = TryOpenSelectedEditor();
    }
    private bool TryOpenSelectedEditor()
    {
        if (SessionsPanel.Visibility == Visibility.Visible)
        {
            if (selectedEditableValue is TerminalSession workspaceSession && state.TerminalSessions.Contains(workspaceSession))
            {
                OpenWorkspaceSessionEditor(workspaceSession);
                return true;
            }
            if (selectedEditableValue is SessionProfile terminal && activeSessionTerminals.Contains(terminal))
            {
                OpenSessionEditor(terminal);
                return true;
            }
        }
        else if (CommandsPanel.Visibility == Visibility.Visible && SnippetList.SelectedItem is CommandSnippet snippet)
        {
            OpenSnippetEditor(snippet);
            return true;
        }
        else if (AutomationPanel.Visibility == Visibility.Visible && AutomationList.SelectedItem is AutomationRule automation)
        {
            OpenAutomationEditor(automation);
            return true;
        }
        return false;
    }
    private void EditorOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && TryDismissEditorFromBackdrop(source)) e.Handled = true;
    }
    private bool TryDismissEditorFromBackdrop(DependencyObject source)
    {
        if (ReferenceEquals(source, EditorCard) || EditorCard.IsAncestorOf(source)) return false;
        HideEditor();
        return true;
    }
    private void GridLayoutClick(object sender, RoutedEventArgs e) => SetLayout("Grid");
    private void ColumnsLayoutClick(object sender, RoutedEventArgs e) => SetLayout("Columns");
    private void RowsLayoutClick(object sender, RoutedEventArgs e) => SetLayout("Rows");
    private void FocusLayoutClick(object sender, RoutedEventArgs e) => SetLayout("Focus");
    private void CancelEditorClick(object sender, RoutedEventArgs e) => HideEditor();
    private void AutomationTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateAutomationScheduleEditor();
    private async void AutomationDateCalendarOpened(object? sender, RoutedEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        try { ApplyAutomationCalendarTheme(); }
        catch (Exception exception)
        {
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] Calendar theme: {exception}\n");
        }
    }
    private void ApplyAutomationCalendarTheme()
    {
        AutomationDateEdit.ApplyTemplate();
        if (AutomationDateEdit.Template.FindName("PART_Popup", AutomationDateEdit) is not System.Windows.Controls.Primitives.Popup popup || popup.Child is not DependencyObject root) return;
        var calendar = FindVisualDescendant<System.Windows.Controls.Calendar>(root);
        if (calendar is null) return;
        calendar.Style = (Style)FindResource("DarkCalendar");
        calendar.CalendarItemStyle = (Style)FindResource("DarkCalendarItem");
        calendar.CalendarDayButtonStyle = (Style)FindResource("DarkCalendarDayButton");
        calendar.CalendarButtonStyle = (Style)FindResource("DarkCalendarButton");
        calendar.ApplyTemplate();
    }
    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T rootMatch) return rootMatch;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
    private void UpdateAutomationScheduleEditor()
    {
        if (AutomationIntervalPanel is null) return;
        var exact = AutomationTypeEdit.SelectedIndex is 1 or 2;
        AutomationIntervalPanel.Visibility = exact ? Visibility.Collapsed : Visibility.Visible;
        AutomationExactPanel.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
        AutomationDatePanel.Visibility = AutomationTypeEdit.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AutomationTimeLabel.Text = AutomationTypeEdit.SelectedIndex == 2 ? "Run at" : "Run every day at";
    }
    private void BrowseDirectoryClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { InitialDirectory = SessionDirectoryEdit.Text }; if (dialog.ShowDialog(this) == true) SessionDirectoryEdit.Text = dialog.FolderName; }
    private void SettingsSectionClick(object sender, RoutedEventArgs e) => ShowSection(SettingsPanel);

    private bool settingsUiReady;
    private void PopulateSettingsUi()
    {
        settingsUiReady = false;
        var settings = state.Settings;
        SettingsFontFace.Text = settings.FontFace ?? string.Empty;
        SettingsFontSize.ItemsSource = new[] { "Windows Terminal default" }.Concat(Enumerable.Range(8, 25).Select(size => size.ToString(CultureInfo.InvariantCulture))).ToList();
        SettingsFontSize.SelectedIndex = settings.FontSize is int size && size >= 8 && size <= 32 ? size - 7 : 0;
        SettingsCursorStyle.SelectedIndex = settings.CursorStyle switch { "Block" => 1, "Underline" => 2, _ => 0 };
        SettingsCursorBlink.IsChecked = settings.CursorBlink;
        SettingsDefaultShell.Text = settings.DefaultCommandLine ?? string.Empty;
        SettingsDefaultDirectory.Text = settings.DefaultWorkingDirectory ?? string.Empty;
        SettingsConfirmRemove.IsChecked = settings.ConfirmBeforeRemove;
        SettingsKeepSessionsInTray.IsChecked = settings.KeepSessionsRunningInTray;
        SettingsRestoreAfterRestart.IsChecked = settings.RestoreSessionsAfterRestart;
        SettingsSaveTranscripts.IsChecked = settings.SaveTerminalTranscripts;
        SettingsSendAllModifierEnabled.IsChecked = settings.SendToAllModifierEnabled;
        SettingsSendAllModifier.SelectedIndex = settings.SendToAllModifier == "Alt" ? 1 : 0;
        SettingsSendAllModifier.IsEnabled = settings.SendToAllModifierEnabled;
        settingsUiReady = true;
    }

    private void ApplySettingsChange()
    {
        if (!settingsUiReady) return;
        var appearance = EffectiveAppearance();
        foreach (var pane in panes.Values) pane.ApplyAppearance(appearance);
        ScheduleSave();
        UpdateStatus("Settings applied");
    }

    private void SettingsFontFaceChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        var value = SettingsFontFace.Text.Trim();
        if ((state.Settings.FontFace ?? string.Empty) == value) return;
        state.Settings.FontFace = value.Length == 0 ? null : value;
        ApplySettingsChange();
    }

    private void SettingsTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is UIElement box) { box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
    }

    private void SettingsFontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.FontSize = SettingsFontSize.SelectedIndex <= 0 ? null : SettingsFontSize.SelectedIndex + 7;
        ApplySettingsChange();
    }

    private void SettingsCursorChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.CursorStyle = SettingsCursorStyle.SelectedIndex switch { 1 => "Block", 2 => "Underline", _ => "Bar" };
        state.Settings.CursorBlink = SettingsCursorBlink.IsChecked == true;
        ApplySettingsChange();
    }

    private void SettingsDefaultsChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        var shell = SettingsDefaultShell.Text.Trim();
        var directory = SettingsDefaultDirectory.Text.Trim();
        if (directory.Length > 0 && !Directory.Exists(directory)) { UpdateStatus("Default working directory does not exist"); return; }
        state.Settings.DefaultCommandLine = shell.Length == 0 ? null : shell;
        state.Settings.DefaultWorkingDirectory = directory.Length == 0 ? null : directory;
        ScheduleSave();
        UpdateStatus("New session defaults saved");
    }

    private void SettingsBrowseDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DefaultSessionDirectory };
        if (dialog.ShowDialog(this) != true) return;
        SettingsDefaultDirectory.Text = dialog.FolderName;
        SettingsDefaultsChanged(sender, e);
    }

    private void SettingsBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.ConfirmBeforeRemove = SettingsConfirmRemove.IsChecked == true;
        state.Settings.KeepSessionsRunningInTray = SettingsKeepSessionsInTray.IsChecked == true;
        state.Settings.RestoreSessionsAfterRestart = SettingsRestoreAfterRestart.IsChecked == true;
        state.Settings.SaveTerminalTranscripts = SettingsSaveTranscripts.IsChecked == true;
        if (state.Settings.RestoreSessionsAfterRestart && !automationMode) recoveryTimer.Start(); else recoveryTimer.Stop();
        if (!state.Settings.SaveTerminalTranscripts) SessionRecoveryStore.DeleteAllTranscripts();
        ScheduleSave();
        UpdateStatus(state.Settings.KeepSessionsRunningInTray ? "Live session preservation enabled" : "The close button will quit PowerShellPlus");
    }

    private void SettingsSendAllChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.SendToAllModifierEnabled = SettingsSendAllModifierEnabled.IsChecked == true;
        state.Settings.SendToAllModifier = SettingsSendAllModifier.SelectedIndex == 1 ? "Alt" : "Shift";
        SettingsSendAllModifier.IsEnabled = state.Settings.SendToAllModifierEnabled;
        foreach (var pane in panes.Values) pane.RefreshCommandRoutingAppearance();
        ScheduleSave();
        UpdateStatus(state.Settings.SendToAllModifierEnabled
            ? $"Hold {state.Settings.SendToAllModifier} to send commands to all terminals"
            : "Send-to-all modifier disabled");
    }

    private void QuitApplicationClick(object sender, RoutedEventArgs e)
    {
        explicitShutdown = true;
        Close();
    }

    private void SettingsResetClick(object sender, RoutedEventArgs e)
    {
        state.Settings.FontFace = null;
        state.Settings.FontSize = null;
        state.Settings.CursorStyle = "Bar";
        state.Settings.CursorBlink = true;
        PopulateSettingsUi();
        ApplySettingsChange();
        UpdateStatus("Appearance reset to the Windows Terminal profile");
    }

    private void OpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{WorkspaceStore.DirectoryPath}\"") { UseShellExecute = true });
        }
        catch { }
    }
    private void OpenWindowsTerminalClick(object sender, RoutedEventArgs e) { var selected = activePane?.Profile; var args = $"-w new -p \"{terminalProfile.ProfileName}\"" + (selected is null ? string.Empty : $" -d \"{selected.WorkingDirectory}\""); try { Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true }); } catch { } }
}
