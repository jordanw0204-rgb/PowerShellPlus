using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PowerShellPlus.Native;

public partial class MainWindow : Window
{
    private enum EditorMode { Terminal, WorkspaceSession, Snippet, Automation }
    private enum AccentColorPickerTarget
    {
        Terminal,
        WorkspaceSession,
        ThemeBackground,
        ThemeSurface,
        ThemeAccent,
        ThemeText,
        ThemeGradientEnd
    }
    private const string TrayOpenLabel = "Open PowerShellPlus";
    private const string TrayQuitLabel = "Quit PowerShellPlus";
    private const string TrayRestartLabel = "Restart PowerShellPlus";
    private const string TrayFullQuitLabel = "Fully Quit PowerShellPlus && Tmux Terminals";
    private const string TerminalDragDataFormat = "PowerShellPlus.TerminalOrder";
    private sealed record RecoveryPaneSource(string SessionId, string WorkingDirectory, TerminalPane Pane, int? RootProcessId,
        bool StartupSettled, bool StartupSucceeded, bool LocalTmuxVerified);
    private sealed record RecoveryPaneCapture(string SessionId, string WorkingDirectory, string Output, int? RootProcessId,
        bool StartupSettled, bool StartupSucceeded, bool LocalTmuxVerified);
    private const double WorkspaceSidebarWidth = 278;
    private readonly WindowsTerminalProfile terminalProfile;
    private readonly WorkspaceState state;
    private readonly Dictionary<string, TerminalPane> panes = [];
    private readonly ObservableCollection<SessionProfile> activeSessionTerminals = [];
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer automationTimer;
    private readonly DispatcherTimer recoveryTimer;
    private readonly SemaphoreSlim terminalStartupThrottle = new(3, 3);
    private readonly DispatcherTimer workspaceSessionHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer terminalTabHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer terminalDragSessionHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly bool automationMode;
    private readonly SessionRecoverySnapshot loadedRecovery;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private MediaPlayer? notificationSoundPlayer;
    private AgentNotificationToast? activeAgentToast;
    private bool explicitShutdown;
    private bool suppressShutdownRecoveryCapture;
    private bool lifecycleOperationInProgress;
    private bool localTmuxDetachedForShutdown;
    private bool shutdownComplete;
    private bool trayNoticeShown;
    private EditorMode editorMode;
    private object? editingValue;
    private object? selectedEditableValue;
    private TerminalPane? activePane;
    private TerminalSession? activeWorkspaceSession;
    private string? activeLayoutSizeKey;
    private bool workspaceSessionSelectionSync;
    private bool terminalTabSelectionSync;
    private Point? terminalOrderDragStart;
    private string? terminalOrderDragId;
    private string? terminalDragSourceId;
    private string? terminalDragOriginSessionId;
    private string? terminalDragHoverSessionId;
    private ListBoxItem? terminalDragHoverSessionContainer;
    private TerminalPane? terminalDropIndicatorPane;
    private bool terminalDragMoveCompleted;
    private TerminalSession? workspaceSessionHoverCandidate;
    private TerminalSession? workspaceSessionHoverOrigin;
    private bool workspaceSessionHoverPreviewActive;
    private SessionProfile? terminalTabHoverCandidate;
    private TerminalPane? terminalTabHoverOrigin;
    private bool terminalTabHoverPreviewActive;
    private bool automationCheckRunning;
    private bool workspaceSaveInProgress;
    private bool workspaceSaveRequestedWhileBusy;
    private WindowsTerminalDragMonitor? windowsTerminalDragMonitor;
    private bool windowsTerminalImportRunning;
    private bool windowsTerminalDropVisible;
    private bool topmostBeforeWindowsTerminalDrop;
    private readonly object recoveryCaptureSync = new();
    private readonly HashSet<string> remoteDetachOperations = new(StringComparer.Ordinal);
    private int recoveryCaptureInProgress;
    private bool terminalStartupRecoverySettled;
    private int layoutTransitionVersion;
    private string terminalEditorAccentColor = WorkspaceAccentPalette.DefaultTerminal;
    private string workspaceEditorAccentColor = WorkspaceAccentPalette.DefaultSession;
    private AccentColorPickerTarget accentColorPickerTarget;
    private double accentPickerHue;
    private double accentPickerSaturation = 1;
    private double accentPickerValue = 1;
    private bool accentSelectionSync;
    private bool accentHexSync;
    private bool accentFieldDragging;
    private bool accentHueDragging;
    private CustomAppThemeState? customThemeDraft;
    private string? editingCustomThemeId;
    private AppThemeDefinition? customThemePreviewOriginal;
    private string? accentColorPickerOriginalValue;

    public MainWindow(bool automationMode = false, Action<StartupProgress>? startupProgress = null)
    {
        this.automationMode = automationMode;
        startupProgress?.Invoke(new StartupProgress("Reading terminal profile", "Loading Windows Terminal appearance and shell defaults", 0, 4));
        var loadedTerminalProfile = WindowsTerminalProfile.Load();
        terminalProfile = automationMode ? loadedTerminalProfile.ForAutomation() : loadedTerminalProfile;
        startupProgress?.Invoke(new StartupProgress("Loading workspace", "Reading sessions, terminals, drafts, and layouts", 1, 4));
        state = WorkspaceStore.Load(terminalProfile);
        AppThemeCatalog.Apply(state.Settings.ApplicationTheme);
        startupProgress?.Invoke(new StartupProgress("Reading recovery state", "Checking saved transcripts, SSH connections, and agent sessions", 2, 4));
        loadedRecovery = automationMode || !state.Settings.RestoreSessionsAfterRestart ? new SessionRecoverySnapshot() : SessionRecoveryStore.Load();
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart) ReconcileCodexRecovery();
        startupProgress?.Invoke(new StartupProgress("Building workspace", $"Preparing {state.TerminalSessions.Count} sessions and {state.Sessions.Count} terminals", 3, 4));
        InitializeComponent();
        ConfigureLayoutControls();
        WorkspaceSessionList.ItemsSource = state.TerminalSessions;
        WorkspaceSessionTabs.ItemsSource = state.TerminalSessions;
        SessionList.ItemsSource = activeSessionTerminals;
        TerminalTabList.ItemsSource = activeSessionTerminals;
        SessionAccentEdit.ItemsSource = WorkspaceAccentPalette.Choices;
        WorkspaceSessionAccentEdit.ItemsSource = WorkspaceAccentPalette.Choices;
        workspaceSessionHoverTimer.Tick += WorkspaceSessionHoverTimerTick;
        terminalTabHoverTimer.Tick += TerminalTabHoverTimerTick;
        terminalDragSessionHoverTimer.Tick += TerminalDragSessionHoverTimerTick;
        SnippetList.ItemsSource = state.Snippets;
        AutomationList.ItemsSource = state.Automations;
        InitializeAutomationTimeUi();
        PopulateSettingsUi();
        ApplyWorkspaceSidebarState(false);

        saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += SaveTimerTick;
        automationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        automationTimer.Tick += async (_, _) => { RefreshAutomationCountdowns(); await CheckAutomationsAsync(); };
        automationTimer.Start();
        recoveryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(12) };
        recoveryTimer.Tick += async (_, _) => await CaptureRecoverySnapshotAsync();

        var terminalIndex = 0;
        foreach (var profile in state.Sessions)
        {
            startupProgress?.Invoke(new StartupProgress("Creating terminals", profile.Name, terminalIndex, Math.Max(1, state.Sessions.Count)));
            CreatePane(profile);
            terminalIndex++;
        }
        var initialSession = state.TerminalSessions.FirstOrDefault(value => value.Id == state.ActiveTerminalSessionId)
            ?? state.TerminalSessions.First();
        SelectWorkspaceSession(initialSession.Id, false);
        UpdateStatus("Native Windows Terminal renderer ready");
        Closing += WindowClosing;
        SourceInitialized += (_, _) => InitializeWindowsTerminalImport();
        Loaded += async (_, _) => await AutoStartDiscordRemoteBotAsync();
        if (!automationMode) InitializeTrayIcon();
    }

    internal async Task WaitForTerminalStartupAsync(Action<StartupProgress>? report, TimeSpan? timeout = null)
    {
        // Pane construction is intentionally independent from visual selection.
        // Inactive Sessions and hidden Tabs/Focus terminals are not in WPF's
        // visual tree, so their Loaded event cannot be their startup trigger.
        // Start every saved backend here with bounded concurrency; the pane will
        // attach its already-running ConPTY when it later becomes visible.
        var pending = panes.Values.Select(StartTerminalForWorkspaceAsync).ToList();
        var allStartupTasks = pending.ToArray();
        if (pending.Count == 0)
        {
            MarkTerminalStartupRecoverySettled();
            report?.Invoke(new StartupProgress("Workspace ready", "No terminals need to be started", 1, 1));
            return;
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var completed = 0;
        var readyCount = 0;
        var failedCount = 0;
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var timeoutTask = Task.Delay(remaining);
            var completion = await Task.WhenAny(pending.Cast<Task>().Append(timeoutTask));
            if (ReferenceEquals(completion, timeoutTask)) break;
            var terminalCompletion = (Task<(TerminalPane Pane, bool Ready)>)completion;
            pending.Remove(terminalCompletion);
            (TerminalPane Pane, bool Ready) result;
            try { result = await terminalCompletion; }
            catch (Exception exception)
            {
                LogNativeError("Background terminal startup", exception);
                completed++;
                failedCount++;
                report?.Invoke(new StartupProgress("Terminal needs attention", exception.GetBaseException().Message, completed, panes.Count));
                continue;
            }
            completed++;
            if (result.Ready) readyCount++; else failedCount++;
            report?.Invoke(new StartupProgress(result.Ready ? "Terminal ready" : "Terminal needs attention",
                result.Pane.Profile.Name, completed, panes.Count));
        }

        if (pending.Count > 0)
            report?.Invoke(new StartupProgress("Opening workspace", $"{pending.Count} terminals are still starting in the background", completed, panes.Count));
        else if (failedCount > 0)
            report?.Invoke(new StartupProgress("Workspace needs attention", $"{readyCount} terminals started · {failedCount} show a Retry action", completed, panes.Count));
        else
            report?.Invoke(new StartupProgress("Workspace ready", $"Started {readyCount} terminals", completed, panes.Count));

        if (pending.Count == 0) MarkTerminalStartupRecoverySettled();
        else _ = MarkTerminalStartupRecoverySettledWhenCompleteAsync(allStartupTasks);
    }

    private async Task MarkTerminalStartupRecoverySettledWhenCompleteAsync(IEnumerable<Task<(TerminalPane Pane, bool Ready)>> tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch (Exception exception) { LogNativeError("Background terminal startup settlement", exception); }
        MarkTerminalStartupRecoverySettled();
    }

    private void MarkTerminalStartupRecoverySettled()
    {
        if (terminalStartupRecoverySettled) return;
        terminalStartupRecoverySettled = true;
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart && !shutdownComplete) recoveryTimer.Start();
    }

    private async Task<(TerminalPane Pane, bool Ready)> StartTerminalForWorkspaceAsync(TerminalPane pane)
    {
        await terminalStartupThrottle.WaitAsync();
        var mountedForWarmup = false;
        try
        {
            if (!pane.Profile.IsRemoteDetached && !pane.HasStartupAttemptOverrideForTest
                && !pane.IsLoaded && VisualTreeHelper.GetParent(pane) is null)
            {
                pane.Visibility = Visibility.Visible;
                TerminalWarmupHost.Children.Add(pane);
                mountedForWarmup = true;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                await Dispatcher.Yield(DispatcherPriority.Render);
            }
            // Always invoke the pane coordinator directly. Hidden HwndHost panes
            // cannot rely on WPF Loaded ordering; startupGate makes the Loaded
            // callback and this eager path safely idempotent.
            var ready = await pane.EnsureTerminalStartupAsync();
            return (pane, ready);
        }
        finally
        {
            if (mountedForWarmup && ReferenceEquals(VisualTreeHelper.GetParent(pane), TerminalWarmupHost))
                TerminalWarmupHost.Children.Remove(pane);
            terminalStartupThrottle.Release();
        }
    }

    internal int TerminalCountForStartup => panes.Count;

    internal async Task PreparePersistentBackendsAsync(Action<StartupProgress>? report)
    {
        var managed = panes.Values.Where(pane => pane.Profile.UseLocalTmux).ToArray();
        for (var index = 0; index < managed.Length; index++)
        {
            var pane = managed[index];
            report?.Invoke(new StartupProgress("Preparing persistent terminals", pane.Profile.Name, index, Math.Max(1, managed.Length)));
            _ = await pane.PrepareLocalTmuxBackendAsync();
        }
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
        terminalTabHoverTimer.Stop();
        terminalDragSessionHoverTimer.Stop();
        saveTimer.Stop();
        windowsTerminalDragMonitor?.Dispose();
        windowsTerminalDragMonitor = null;
        StopLanRemoteForShutdown();
        if (!automationMode && !suppressShutdownRecoveryCapture) CaptureRecoverySnapshot();
        if (!automationMode && !localTmuxDetachedForShutdown)
        {
            var managedLocalTmux = state.Sessions.Where(profile => profile.UseLocalTmux)
                .Select(profile => (profile.Id, profile.LocalTmuxDistribution)).ToArray();
            try
            {
                var results = Task.Run(() => Task.WhenAll(managedLocalTmux
                    .Select(profile => LocalTmuxSession.DetachAsync(profile.Id, profile.LocalTmuxDistribution)))).GetAwaiter().GetResult();
                localTmuxDetachedForShutdown = results.All(result => result.CommandSucceeded);
            }
            catch (Exception exception) { LogNativeError("Local tmux shutdown detach", exception); }
        }
        shutdownComplete = true;
        SaveNow();
        foreach (var pane in panes.Values) pane.Stop();
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }
        activeAgentToast?.DismissImmediately();
        activeAgentToast = null;
        notificationSoundPlayer?.Close();
        notificationSoundPlayer = null;
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(TrayOpenLabel, null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var quitItem = new System.Windows.Forms.ToolStripMenuItem(TrayQuitLabel, null, (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(QuitPowerShellPlusToSessionHost));
        }) { ToolTipText = "Hide the PowerShellPlus window while keeping every local and remote terminal running." };
        var restartItem = new System.Windows.Forms.ToolStripMenuItem(TrayRestartLabel, null, (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() => _ = RestartPowerShellPlusAsync()));
        }) { ToolTipText = "Restart the UI and reattach managed local and remote tmux terminals without stopping them." };
        var fullQuitItem = new System.Windows.Forms.ToolStripMenuItem(TrayFullQuitLabel, null, (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() => _ = FullyQuitPowerShellPlusAsync()));
        }) { ToolTipText = "Stop every managed local and remote tmux terminal, close other terminals, and exit PowerShellPlus." };
        menu.Items.Add(quitItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(fullQuitItem);
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

    private void PaneAgentActivityChanged(TerminalPane pane, AgentKind kind, AgentActivityState previous, AgentActivityState current)
    {
        if (!state.Settings.AgentNotificationsEnabled || !pane.Profile.AgentNotificationsEnabled
            || !ShouldNotifyAgentTransition(kind, previous, current)) return;
        ShowAgentNotification(pane.Profile, kind, current);
    }

    internal static bool ShouldNotifyAgentTransition(AgentKind kind, AgentActivityState previous, AgentActivityState current)
        => kind != AgentKind.Terminal
            && (current == AgentActivityState.Waiting && previous != AgentActivityState.Waiting
                || current == AgentActivityState.Idle && previous == AgentActivityState.Working);

    private void ShowAgentNotification(SessionProfile profile, AgentKind kind, AgentActivityState stateValue, bool test = false)
    {
        var agent = kind switch { AgentKind.Hermes => "Hermes", AgentKind.Codex => "Codex", _ => "Agent" };
        var waiting = stateValue == AgentActivityState.Waiting;
        var title = test ? "PowerShellPlus notification test"
            : waiting ? $"{agent} needs your response" : $"{agent} finished";
        var message = test ? $"Notifications are ready for {profile.Name}."
            : waiting ? $"{profile.Name} is waiting for your response."
            : $"{agent} finished working in {profile.Name}.";
        activeAgentToast?.DismissImmediately();
        var toast = new AgentNotificationToast(title, message, profile.Name, waiting,
            WorkspaceAccentPalette.Normalize(profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal),
            new WindowInteropHelper(this).Handle, () =>
            {
                RestoreFromTray();
                SelectPane(profile.Id, true);
            });
        activeAgentToast = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(activeAgentToast, toast)) activeAgentToast = null;
        };
        toast.Show();
        PlayConfiguredNotificationSound();
    }

    private void PlayConfiguredNotificationSound()
    {
        if (state.Settings.NotificationSound.Equals("Silent", StringComparison.OrdinalIgnoreCase)) return;
        if (!state.Settings.NotificationSound.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            return;
        }
        var path = state.Settings.CustomNotificationSoundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            notificationSoundPlayer?.Close();
            notificationSoundPlayer = new MediaPlayer { Volume = 1 };
            notificationSoundPlayer.MediaEnded += (_, _) => notificationSoundPlayer?.Close();
            notificationSoundPlayer.MediaFailed += (_, _) => notificationSoundPlayer?.Close();
            notificationSoundPlayer.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
            notificationSoundPlayer.Play();
        }
        catch { notificationSoundPlayer?.Close(); }
    }

    private void QuitPowerShellPlusToSessionHost()
    {
        if (shutdownComplete || lifecycleOperationInProgress) return;
        CaptureRecoverySnapshot();
        HideToTray();
        UpdateStatus("PowerShellPlus is hidden · local and remote terminals remain live");
    }

    private async Task RestartPowerShellPlusAsync()
    {
        if (shutdownComplete || lifecycleOperationInProgress) return;
        lifecycleOperationInProgress = true;
        try
        {
            await CaptureRecoverySnapshotAsync();
            await DetachManagedLocalTmuxAsync();
            var startInfo = BuildRestartStartInfo(Environment.ProcessId);
            using var replacement = Process.Start(startInfo);
            if (replacement is null)
                throw new InvalidOperationException("Windows could not start the replacement PowerShellPlus process.");
            suppressShutdownRecoveryCapture = true;
            explicitShutdown = true;
            Close();
        }
        catch (Exception exception)
        {
            lifecycleOperationInProgress = false;
            LogNativeError("PowerShellPlus restart", exception);
            RestoreWindow(true);
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "PowerShellPlus could not restart", PowerShellPlusDialogKind.Error);
        }
    }

    internal async Task DetachManagedLocalTmuxAsync()
    {
        if (localTmuxDetachedForShutdown) return;
        var managed = state.Sessions.Where(profile => profile.UseLocalTmux)
            .Select(profile => (profile.Id, profile.LocalTmuxDistribution)).ToArray();
        if (managed.Length == 0)
        {
            localTmuxDetachedForShutdown = true;
            return;
        }
        var results = await Task.WhenAll(managed.Select(profile =>
            LocalTmuxSession.DetachAsync(profile.Id, profile.LocalTmuxDistribution)));
        var failures = results.Where(result => !result.CommandSucceeded).Select(result => result.Message).Distinct().ToArray();
        if (failures.Length > 0)
            throw new InvalidOperationException("PowerShellPlus could not safely detach one or more local tmux terminals:\n\n" + string.Join("\n", failures));
        localTmuxDetachedForShutdown = true;
    }

    private async Task FullyQuitPowerShellPlusAsync()
    {
        if (shutdownComplete || lifecycleOperationInProgress) return;
        RestoreWindow(true);
        if (!PowerShellPlusDialog.Confirm(this,
            "This permanently stops every local and remote tmux terminal managed by PowerShellPlus, then closes the app. Running commands and agents in those tmux terminals will stop.",
            "Fully quit PowerShellPlus?", PowerShellPlusDialogKind.Warning,
            "Fully quit", "Cancel", defaultToPrimary: false, primaryIsDangerous: true)) return;

        lifecycleOperationInProgress = true;
        try
        {
            await CaptureRecoverySnapshotAsync();
            var snapshot = SessionRecoveryStore.Load();
            var managedRemoteSessions = snapshot.Sessions.Values
                .Where(entry => entry.SshWasActive && entry.RemoteTmuxManaged)
                .ToArray();
            var results = await Task.WhenAll(managedRemoteSessions.Select(entry => RemoteTmuxSession.KillAsync(entry)));
            var localResults = await Task.WhenAll(state.Sessions.Where(profile => profile.UseLocalTmux)
                .Select(profile => LocalTmuxSession.KillAsync(profile.Id, profile.LocalTmuxDistribution)));
            var failures = results.Where(result => !result.CommandSucceeded).Select(result => result.Message)
                .Concat(localResults.Where(result => !result.CommandSucceeded).Select(result => result.Message)).Distinct().ToArray();
            if (failures.Length > 0)
            {
                lifecycleOperationInProgress = false;
                PowerShellPlusDialog.ShowMessage(this,
                    "PowerShellPlus stayed open because one or more managed tmux terminals could not be stopped:\n\n" + string.Join("\n", failures),
                    "Some tmux terminals are still running", PowerShellPlusDialogKind.Warning);
                return;
            }

            SessionRecoveryStore.Save(new SessionRecoverySnapshot());
            suppressShutdownRecoveryCapture = true;
            explicitShutdown = true;
            Close();
        }
        catch (Exception exception)
        {
            lifecycleOperationInProgress = false;
            LogNativeError("Full PowerShellPlus shutdown", exception);
            PowerShellPlusDialog.ShowMessage(this, exception.Message,
                "PowerShellPlus stayed open", PowerShellPlusDialogKind.Error);
        }
    }

    private static ProcessStartInfo BuildRestartStartInfo(int currentProcessId)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("PowerShellPlus could not locate its executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"--restart-after={currentProcessId.ToString(CultureInfo.InvariantCulture)}");
        return startInfo;
    }

    internal static bool TrayLifecycleContractPassesForTest()
    {
        var startInfo = BuildRestartStartInfo(4242);
        return TrayOpenLabel == "Open PowerShellPlus"
            && TrayQuitLabel == "Quit PowerShellPlus"
            && TrayRestartLabel == "Restart PowerShellPlus"
            && TrayFullQuitLabel.Contains("Tmux Terminals", StringComparison.Ordinal)
            && !startInfo.UseShellExecute && startInfo.CreateNoWindow
            && startInfo.ArgumentList.SequenceEqual(["--restart-after=4242"]);
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
        CaptureRecoverySnapshotCore(MaterializeRecoveryPaneCaptures(CollectRecoveryPaneSources()), state.Settings.SaveTerminalTranscripts);
        RefreshTmuxTerminalIndicators(SessionRecoveryStore.Load());
    }

    private async Task CaptureRecoverySnapshotAsync()
    {
        if (automationMode || windowsTerminalImportRunning || !state.Settings.RestoreSessionsAfterRestart || shutdownComplete) return;
        if (System.Threading.Interlocked.CompareExchange(ref recoveryCaptureInProgress, 1, 0) != 0) return;
        try
        {
            var sources = CollectRecoveryPaneSources();
            var saveTerminalTranscripts = state.Settings.SaveTerminalTranscripts;
            await Task.Run(() =>
            {
                var captures = MaterializeRecoveryPaneCaptures(sources);
                CaptureRecoverySnapshotCore(captures, saveTerminalTranscripts);
            });
            RefreshTmuxTerminalIndicators(SessionRecoveryStore.Load());
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref recoveryCaptureInProgress, 0);
        }
    }

    private List<RecoveryPaneSource> CollectRecoveryPaneSources() => panes.Values
        .Select(pane => new RecoveryPaneSource(pane.Profile.Id, pane.Profile.WorkingDirectory, pane, pane.GetRootProcessId(),
            pane.RecoveryStartupSettledForSnapshot, pane.RecoveryStartupSucceededForSnapshot, pane.LocalTmuxVerifiedForSnapshot))
        .ToList();

    private static List<RecoveryPaneCapture> MaterializeRecoveryPaneCaptures(IEnumerable<RecoveryPaneSource> sources) => sources
        .Select(source => new RecoveryPaneCapture(source.SessionId, source.WorkingDirectory,
            source.Pane.GetRecoveryOutputForSnapshot(), source.RootProcessId,
            source.StartupSettled, source.StartupSucceeded, source.LocalTmuxVerified))
        .ToList();

    private void RefreshTmuxTerminalIndicators(SessionRecoverySnapshot snapshot)
    {
        foreach (var profile in state.Sessions)
            profile.SetTmuxTerminal(snapshot.Sessions.TryGetValue(profile.Id, out var recovery)
                && (recovery.LocalTmuxManaged || recovery.SshWasActive && recovery.RemoteTmuxManaged));
    }

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
                    var profile = state.Sessions.FirstOrDefault(value => value.Id == capture.SessionId);
                    if (ShouldPreservePreviousRecovery(capture.StartupSettled, capture.StartupSucceeded, oldEntry))
                    {
                        var retained = oldEntry!.CopyForTransition();
                        retained.CapturedUtc = DateTime.UtcNow;
                        snapshot.Sessions[capture.SessionId] = retained;
                        continue;
                    }
                    if (profile?.IsRemoteDetached == true && oldEntry?.RemoteTmuxManaged == true)
                    {
                        oldEntry.CapturedUtc = DateTime.UtcNow;
                        snapshot.Sessions[capture.SessionId] = oldEntry;
                        continue;
                    }
                    var codex = capture.RootProcessId is int rootProcessId ? ProcessTreeInspector.FindCodexProcess(rootProcessId) : default;
                    var launch = CodexLaunchStore.Load(capture.SessionId);
                    if (!codex.IsActive && launch?.IsActive == true && launch.ShellProcessId is > 0)
                        codex = ProcessTreeInspector.FindCodexProcess(launch.ShellProcessId.Value);
                    var codexIsActive = codex.IsActive;
                    var exactCodexMatch = codex.IsActive
                        ? CodexActivityStore.FindActiveCliSession(codex.ProcessId, codex.StartedUtc, usedCodexSessionIds)
                        : null;
                    var codexMatch = exactCodexMatch;
                    var activeCodexThreadIds = codex.IsActive
                        ? CodexActivityStore.FindActiveThreadIds(codex.ProcessId, codex.StartedUtc)
                        : [];
                    var launchSessionIsBound = launch?.IsActive == true
                        && launch.ShellProcessId == (capture.RootProcessId ?? launch.ShellProcessId)
                        && CodexSessionLocator.IsSafeCodexId(launch.SessionId)
                        && (string.Equals(launch.ExplicitSessionId, launch.SessionId, StringComparison.OrdinalIgnoreCase)
                            || activeCodexThreadIds.Contains(launch.SessionId!, StringComparer.OrdinalIgnoreCase));
                    var codexSessionId = codexMatch?.SessionId;
                    var codexDirectory = codexMatch?.WorkingDirectory;
                    var codexModel = codexMatch?.Model;
                    var codexSandboxMode = codexMatch?.SandboxMode;
                    var codexApprovalPolicy = codexMatch?.ApprovalPolicy;
                    var codexPermissionProfile = codexMatch?.PermissionProfile;
                    var codexApprovalsReviewer = codexMatch?.ApprovalsReviewer;
                    if (codexSessionId is null && launchSessionIsBound && launch is not null)
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
                    // Only a process-ID correlation may become durable launch state. Time/CWD
                    // fallbacks can collide when two Codex CLIs start in the same directory.
                    if (exactCodexMatch is not null && launch?.IsActive == true && launch.ShellProcessId == capture.RootProcessId)
                        CodexLaunchStore.Confirm(launch, exactCodexMatch);
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
                    var sshLaunchIsActive = sshLaunch?.IsActive == true;
                    var sshIsObserved = sshLaunchIsActive && sshProcess.IsActive;
                    var keepPendingSshRecovery = SshRecovery.ShouldKeepPendingRecovery(oldEntry, sshLaunch, sshProcess.IsActive);
                    var sshRestorable = SshRecovery.IsRestorableLaunch(oldEntry, sshLaunch, sshProcess.IsActive);
                    var sshArguments = sshLaunchIsActive ? sshLaunch!.ConnectionArguments
                        : keepPendingSshRecovery ? oldEntry!.SshConnectionArguments : [];
                    var samePreviousSsh = sshRestorable && oldEntry?.SshWasActive == true
                        && oldEntry.SshConnectionArguments.SequenceEqual(sshArguments, StringComparer.Ordinal);
                    var remoteTmuxManaged = sshRestorable
                        && (sshLaunch?.PersistentSessionRequested == true || samePreviousSsh && oldEntry?.RemoteTmuxManaged == true)
                        && !capture.Output.Contains("tmux is not installed", StringComparison.OrdinalIgnoreCase);
                    var previousHermes = samePreviousSsh
                        ? new HermesRecoveryState(oldEntry!.HermesWasActive, oldEntry.HermesSessionId, oldEntry.HermesModel, oldEntry.HermesUseTui)
                        : default;
                    var hermes = sshIsObserved ? HermesRecovery.Detect(capture.Output, previousHermes)
                        : samePreviousSsh ? previousHermes : default;
                    var previousRemoteCodex = samePreviousSsh
                        ? new RemoteCodexRecoveryState(oldEntry!.RemoteCodexWasActive, oldEntry.RemoteCodexSessionId,
                            oldEntry.RemoteCodexWorkingDirectory, oldEntry.RemoteCodexModel, oldEntry.RemoteCodexSandboxMode,
                            oldEntry.RemoteCodexApprovalPolicy, oldEntry.RemoteCodexPermissionProfile, oldEntry.RemoteCodexApprovalsReviewer)
                        : default;
                    var remoteCodexProbe = sshIsObserved && !hermes.WasActive
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
                            : sshLaunchIsActive ? sshLaunch!.WorkingDirectory
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
                        RemoteTmuxManaged = remoteTmuxManaged,
                        RemoteTmuxSessionName = remoteTmuxManaged ? RemoteTmuxSession.GetSessionName(capture.SessionId) : null,
                        LocalTmuxManaged = capture.LocalTmuxVerified,
                        LocalTmuxSessionName = capture.LocalTmuxVerified ? LocalTmuxSession.GetSessionName(capture.SessionId) : null,
                        LocalTmuxDistribution = capture.LocalTmuxVerified ? profile?.LocalTmuxDistribution : null,
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

    internal static bool ShouldPreservePreviousRecovery(bool startupSettled, bool startupSucceeded, SessionRecoveryEntry? previous)
        => previous is not null && (!startupSettled || !startupSucceeded);

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

            var launchProcess = launch.ShellProcessId is > 0
                ? ProcessTreeInspector.FindCodexProcess(launch.ShellProcessId.Value)
                : default;
            if (!launchProcess.IsActive) continue;
            var match = CodexActivityStore.FindActiveCliSession(launchProcess.ProcessId, launchProcess.StartedUtc, usedCodexSessionIds);
            if (match is not null) CodexLaunchStore.Confirm(launch, match);
            var activeThreadIds = CodexActivityStore.FindActiveThreadIds(launchProcess.ProcessId, launchProcess.StartedUtc);
            var explicitSessionIsBound = CodexSessionLocator.IsSafeCodexId(launch.ExplicitSessionId)
                && string.Equals(launch.ExplicitSessionId, launch.SessionId, StringComparison.OrdinalIgnoreCase);
            var capturedSessionIsBound = CodexSessionLocator.IsSafeCodexId(launch.SessionId)
                && activeThreadIds.Contains(launch.SessionId!, StringComparer.OrdinalIgnoreCase);
            var sessionId = match?.SessionId ?? (explicitSessionIsBound || capturedSessionIsBound ? launch.SessionId : null);
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

    private TerminalAppearance EffectiveAppearance(AppThemeDefinition? themeOverride = null)
    {
        var settings = state.Settings;
        var fontFace = string.IsNullOrWhiteSpace(settings.FontFace) ? terminalProfile.FontFace : settings.FontFace.Trim();
        var fontSize = Math.Clamp(settings.FontSize ?? terminalProfile.FontSize, 8, 32);
        var theme = themeOverride is null
            ? AppThemeCatalog.CreateTerminalTheme(terminalProfile.Theme, settings.ApplicationTheme)
            : AppThemeCatalog.CreateTerminalTheme(terminalProfile.Theme, themeOverride);
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

    private string NewTerminalAccent => state.Settings.AutomaticallySetTerminalColor
        ? WorkspaceAccentPalette.RandomTerminalAccent()
        : WorkspaceAccentPalette.DefaultTerminal;

    private SessionProfile CreateDefaultTerminalProfile() => new()
    {
        Name = terminalProfile.ProfileName,
        AccentColor = NewTerminalAccent,
        CommandLine = DefaultSessionCommandLine,
        WorkingDirectory = DefaultSessionDirectory,
        AutoStart = true,
        UseRemoteTmux = true,
        UseLocalTmux = true
    };

    private void CreateDefaultTerminal()
    {
        var created = CreateDefaultTerminalProfile();
        AddTerminalToActiveSession(created);
        CreatePane(created);
        SelectPane(created.Id, true);
        ApplyLayout();
        ScheduleSave();
        UpdateStatus($"Created {created.Name}");
    }

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
        var results = await Task.WhenAll(targets.Select(pane => pane.SendComposerCommandAsync(command)));
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
            () => state.Settings.SendToAllModifierEnabled, () => SendToAllModifier,
            () => state.Automations, OpenAutomationEditor);
        // A native terminal click already gives its HWND keyboard focus. Only
        // update application selection here so WPF does not steal that focus.
        pane.Activated += (_, _) => SelectPane(profile.Id, false);
        pane.CloseRequested += async (_, _) => await CloseTerminalAsync(profile);
        pane.EditRequested += (_, _) => OpenSessionEditor(profile);
        pane.DetachRequested += (_, _) => DetachSessionToWindowsTerminal(profile, pane);
        pane.DragRequested += (_, _) => BeginTerminalDrag(profile.Id);
        pane.AgentActivityChanged += PaneAgentActivityChanged;
        pane.AllowDrop = true;
        pane.DragOver += TerminalLayoutDragOver;
        pane.DragLeave += TerminalLayoutDragLeave;
        pane.Drop += TerminalLayoutDrop;
        panes[profile.Id] = pane;
    }

    private void SelectPane(string sessionId, bool focus = true)
    {
        if (!panes.TryGetValue(sessionId, out var pane)) return;
        if (pane.Profile.IsRemoteDetached)
        {
            _ = ReattachRemoteTerminalAsync(pane, focus);
            return;
        }
        var owner = state.TerminalSessions.FirstOrDefault(value => value.TerminalIds.Contains(sessionId, StringComparer.Ordinal));
        if (owner is not null && owner != activeWorkspaceSession) SelectWorkspaceSession(owner.Id, false);
        activePane = pane;
        state.ActiveSessionId = sessionId;
        if (activeWorkspaceSession is not null) activeWorkspaceSession.ActiveTerminalId = sessionId;
        foreach (var value in panes.Values) value.SetActive(value == pane);
        SessionList.SelectedItem = pane.Profile;
        terminalTabSelectionSync = true;
        TerminalTabList.SelectedItem = pane.Profile;
        terminalTabSelectionSync = false;
        if (activeWorkspaceSession?.Layout is "Focus" or "Tabs") ApplyLayout();
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
        terminalTabSelectionSync = true;
        TerminalTabList.SelectedItem = activePane?.Profile;
        terminalTabSelectionSync = false;
        ApplyLayout(commit);
        UpdateLayoutControls();
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
        var ordered = workspaceSession?.TerminalIds.Where(value => panes.TryGetValue(value, out var pane) && !pane.Profile.IsRemoteDetached).Select(value => panes[value]).ToList() ?? [];
        activeLayoutSizeKey = null;
        TerminalTabBar.Visibility = workspaceSession?.Layout == "Tabs" ? Visibility.Visible : Visibility.Collapsed;
        if (ordered.Count == 0) { UpdateCounts(); return; }
        foreach (var pane in ordered) pane.Visibility = Visibility.Visible;

        if (workspaceSession?.Layout is "Focus" or "Tabs")
        {
            TerminalHost.RowDefinitions.Add(new RowDefinition()); TerminalHost.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (var pane in ordered) if (pane != activePane) pane.Visibility = Visibility.Collapsed;
            if (activePane is not null)
            {
                TerminalWarmupHost.Children.Remove(activePane);
                TerminalHost.Children.Add(activePane);
            }
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
                var pane = ordered[index];
                TerminalWarmupHost.Children.Remove(pane);
                Grid.SetColumn(pane, (index % columns) * 2); Grid.SetRow(pane, (index / columns) * 2); TerminalHost.Children.Add(pane);
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

    private void SetLayout(string layout)
    {
        if (activeWorkspaceSession is null) return;
        CaptureLayoutSizing();
        var animate = !automationMode && IsVisible && TerminalHost.ActualWidth > 0 && TerminalHost.ActualHeight > 0;
        if (animate) TerminalHost.Visibility = Visibility.Hidden;
        activeWorkspaceSession.Layout = layout;
        ApplyLayout();
        UpdateLayoutControls();
        if (animate) BeginLayoutTransition(layout);
        UpdateStatus($"{layout} layout in {activeWorkspaceSession.Name} - drag the dividers to resize terminals");
    }

    private void ConfigureLayoutControls()
    {
        foreach (var (button, layout) in LayoutButtons())
        {
            button.ToolTip = CreateLayoutPreviewToolTip(layout);
            ToolTipService.SetPlacement(button, PlacementMode.Right);
        }
        UpdateLayoutControls();
    }

    private (Button Button, string Layout)[] LayoutButtons() =>
    [
        (GridLayoutButton, "Grid"),
        (ColumnsLayoutButton, "Columns"),
        (RowsLayoutButton, "Rows"),
        (FocusLayoutButton, "Focus"),
        (TabsLayoutButton, "Tabs")
    ];

    private void UpdateLayoutControls()
    {
        var current = activeWorkspaceSession?.Layout ?? "Grid";
        ActiveLayoutText.Text = current;
        foreach (var (button, layout) in LayoutButtons())
        {
            button.IsEnabled = activeWorkspaceSession is not null;
            button.Tag = string.Equals(layout, current, StringComparison.Ordinal) ? "Active" : null;
            AutomationProperties.SetHelpText(button, $"Use {layout.ToLowerInvariant()} layout for {activeWorkspaceSession?.Name ?? "the active session"}");
        }
    }

    private ToolTip CreateLayoutPreviewToolTip(string layout)
    {
        var canvas = new Canvas { Width = 148, Height = 84, Background = new SolidColorBrush(Color.FromRgb(17, 17, 27)) };
        PopulateLayoutPreview(canvas, layout, 4, false);
        var title = layout switch
        {
            "Columns" => "Side by side",
            "Rows" => "Stacked terminals",
            "Focus" => "Selected terminal only",
            "Tabs" => "One terminal with reorderable tabs",
            _ => "Balanced terminal grid"
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = $"{layout} layout", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(205, 214, 244)) });
        content.Children.Add(new TextBlock { Text = title, FontSize = 11, Margin = new Thickness(0, 2, 0, 9), Foreground = new SolidColorBrush(Color.FromRgb(166, 173, 200)) });
        content.Children.Add(canvas);
        return new ToolTip
        {
            Placement = PlacementMode.Right,
            HorizontalOffset = 8,
            Content = new Border
            {
                Padding = new Thickness(11),
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(69, 71, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = content
            }
        };
    }

    private static IReadOnlyList<Rect> BuildLayoutPreviewRects(string layout, int terminalCount, double width, double height, double gap = 6)
    {
        terminalCount = Math.Clamp(terminalCount, 1, 4);
        if (layout == "Focus") return [new Rect(0, 0, width, height)];
        if (layout == "Tabs") return [new Rect(0, 18, width, Math.Max(1, height - 18))];
        int columns;
        int rows;
        if (layout == "Rows") { columns = 1; rows = terminalCount; }
        else if (layout == "Columns") { columns = terminalCount; rows = 1; }
        else { columns = (int)Math.Ceiling(Math.Sqrt(terminalCount)); rows = (int)Math.Ceiling((double)terminalCount / columns); }
        var paneWidth = (width - gap * (columns - 1)) / columns;
        var paneHeight = (height - gap * (rows - 1)) / rows;
        var result = new List<Rect>(terminalCount);
        for (var index = 0; index < terminalCount; index++)
            result.Add(new Rect((index % columns) * (paneWidth + gap), (index / columns) * (paneHeight + gap), paneWidth, paneHeight));
        return result;
    }

    private static void PopulateLayoutPreview(Canvas canvas, string layout, int terminalCount, bool transition)
    {
        canvas.Children.Clear();
        if (layout == "Tabs")
        {
            var tabWidth = Math.Min(42, (canvas.Width - 8) / 3);
            for (var tabIndex = 0; tabIndex < 3; tabIndex++)
            {
                var tab = new Border
                {
                    Width = tabWidth,
                    Height = 13,
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    Background = new SolidColorBrush(tabIndex == 0 ? Color.FromRgb(38, 54, 83) : Color.FromRgb(30, 30, 46)),
                    BorderBrush = new SolidColorBrush(tabIndex == 0 ? Color.FromRgb(137, 180, 250) : Color.FromRgb(69, 71, 90)),
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(tab, tabIndex * (tabWidth + 4));
                Canvas.SetTop(tab, 2);
                canvas.Children.Add(tab);
            }
        }
        foreach (var (rect, index) in BuildLayoutPreviewRects(layout, terminalCount, canvas.Width, canvas.Height).Select((rect, index) => (rect, index)))
        {
            var pane = new Border
            {
                Width = rect.Width,
                Height = rect.Height,
                CornerRadius = new CornerRadius(transition ? 7 : 4),
                BorderThickness = new Thickness(transition ? 1.5 : 1),
                BorderBrush = new SolidColorBrush(index == 0 ? Color.FromRgb(137, 180, 250) : Color.FromRgb(88, 91, 112)),
                Background = new SolidColorBrush(index == 0 ? Color.FromRgb(38, 54, 83) : Color.FromRgb(30, 30, 46)),
                Child = transition ? new TextBlock
                {
                    Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 190, 254)),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                } : null
            };
            Canvas.SetLeft(pane, rect.X);
            Canvas.SetTop(pane, rect.Y);
            canvas.Children.Add(pane);
        }
    }

    private async void BeginLayoutTransition(string layout)
    {
        var version = ++layoutTransitionVersion;
        try
        {
            var paneCount = Math.Clamp(activeSessionTerminals.Count, 1, 4);
            LayoutTransitionTitle.Text = $"{layout.ToUpperInvariant()} LAYOUT";
            PopulateLayoutPreview(LayoutTransitionCanvas, layout, paneCount, true);
            LayoutTransitionOverlay.BeginAnimation(OpacityProperty, null);
            LayoutTransitionOverlay.Opacity = 1;
            LayoutTransitionOverlay.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(230));
            foreach (var pane in LayoutTransitionCanvas.Children.OfType<Border>())
            {
                var left = Canvas.GetLeft(pane);
                var top = Canvas.GetTop(pane);
                var translate = new TranslateTransform(
                    LayoutTransitionCanvas.Width / 2 - (left + pane.Width / 2),
                    LayoutTransitionCanvas.Height / 2 - (top + pane.Height / 2));
                var scale = new ScaleTransform(.78, .78);
                var transforms = new TransformGroup();
                transforms.Children.Add(scale);
                transforms.Children.Add(translate);
                pane.RenderTransformOrigin = new Point(.5, .5);
                pane.RenderTransform = transforms;
                pane.Opacity = 0;
                pane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.78, 1, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.78, 1, duration) { EasingFunction = ease });
                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(translate.X, 0, duration) { EasingFunction = ease });
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, 0, duration) { EasingFunction = ease });
            }

            await Task.Delay(240);
            if (version != layoutTransitionVersion) return;
            LayoutTransitionOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90)));
            await Task.Delay(105);
            if (version != layoutTransitionVersion) return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Layout transition failed: {ex.Message}");
        }
        finally
        {
            if (version == layoutTransitionVersion)
            {
                LayoutTransitionOverlay.BeginAnimation(OpacityProperty, null);
                LayoutTransitionOverlay.Opacity = 1;
                LayoutTransitionOverlay.Visibility = Visibility.Collapsed;
                TerminalHost.Visibility = Visibility.Visible;
                TerminalHost.InvalidateArrange();
            }
        }
    }

    private bool LayoutPreviewGeometryWorksForTest()
    {
        var grid = BuildLayoutPreviewRects("Grid", 4, 100, 80);
        var rows = BuildLayoutPreviewRects("Rows", 3, 100, 80);
        var columns = BuildLayoutPreviewRects("Columns", 3, 100, 80);
        var focus = BuildLayoutPreviewRects("Focus", 4, 100, 80);
        var tabs = BuildLayoutPreviewRects("Tabs", 4, 100, 80);
        return grid.Count == 4 && grid.Select(rect => rect.X).Distinct().Count() == 2 && grid.Select(rect => rect.Y).Distinct().Count() == 2
            && rows.Count == 3 && rows.All(rect => rect.X == 0) && rows.Zip(rows.Skip(1)).All(pair => pair.First.Y < pair.Second.Y)
            && columns.Count == 3 && columns.All(rect => rect.Y == 0) && columns.Zip(columns.Skip(1)).All(pair => pair.First.X < pair.Second.X)
            && focus.Count == 1 && focus[0] == new Rect(0, 0, 100, 80)
            && tabs.Count == 1 && tabs[0].Y == 18 && tabs[0].Height == 62;
    }

    private void OpenSessionEditor(SessionProfile? profile)
    {
        editorMode = EditorMode.Terminal; editingValue = profile;
        EditorTitle.Text = profile is null ? "New native terminal" : "Edit terminal";
        SessionNameEdit.Text = profile?.Name ?? terminalProfile.ProfileName;
        SetTerminalEditorAccent(profile?.AccentColor ?? NewTerminalAccent);
        SessionCommandEdit.Text = profile?.CommandLine ?? DefaultSessionCommandLine;
        SessionDirectoryEdit.Text = profile?.WorkingDirectory ?? DefaultSessionDirectory;
        SessionAutoStartEdit.IsChecked = profile?.AutoStart ?? true;
        SessionAgentNotificationsEdit.IsChecked = profile?.AgentNotificationsEnabled ?? true;
        SessionUseLocalTmuxEdit.IsChecked = profile?.UseLocalTmux ?? true;
        SessionUseTmuxEdit.IsChecked = profile?.UseRemoteTmux ?? true;
        UpdateTerminalTmuxEditorStatus(profile);
        ShowEditor(SessionEditor);
    }

    private void UpdateTerminalTmuxEditorStatus(SessionProfile? profile)
    {
        SessionLocalTmuxStatusText.Text = LocalTmuxEditorStatus(profile, SessionUseLocalTmuxEdit.IsChecked == true);
        SessionTmuxStatusText.Text = TerminalTmuxEditorStatus(profile, SessionUseTmuxEdit.IsChecked == true);
    }

    private async void SessionUseLocalTmuxEditClick(object sender, RoutedEventArgs e)
    {
        if (SessionUseLocalTmuxEdit.IsChecked != true)
        {
            UpdateTerminalTmuxEditorStatus(editingValue as SessionProfile);
            return;
        }
        SessionUseLocalTmuxEdit.IsEnabled = false;
        SessionLocalTmuxStatusText.Text = "Checking WSL and tmux...";
        try
        {
            var profile = editingValue as SessionProfile;
            var status = await LocalTmuxSession.ProbeAsync(profile?.LocalTmuxDistribution, profile?.Id);
            SessionLocalTmuxStatusText.Text = status.Message;
        }
        finally { SessionUseLocalTmuxEdit.IsEnabled = true; }
    }

    private void SessionUseTmuxEditClick(object sender, RoutedEventArgs e)
        => UpdateTerminalTmuxEditorStatus(editingValue as SessionProfile);

    internal static string TerminalTmuxEditorStatus(SessionProfile? profile, bool enabled)
    {
        if (profile is { IsTmuxTerminal: true, UseLocalTmux: false } && profile.LiveWorkingDirectoryIsSsh)
            return enabled
                ? "Active on the SSH host · this terminal has a real persistent tmux session and shows the TMUX badge."
                : "Active now · saving this change reconnects the SSH terminal without tmux.";
        return enabled
            ? "Remote policy armed - the next SSH connection in this terminal is placed inside tmux on the SSH host."
            : "Disabled - future SSH connections in this terminal use standard SSH without tmux.";
    }

    internal static string LocalTmuxEditorStatus(SessionProfile? profile, bool enabled)
    {
        if (enabled && profile is { UseLocalTmux: true, IsTmuxTerminal: true })
            return $"Active locally through WSL{(string.IsNullOrWhiteSpace(profile.LocalTmuxDistribution) ? string.Empty : $" - {profile.LocalTmuxDistribution}")} - this exact Windows terminal survives app restarts.";
        return enabled
            ? "Requires WSL, a registered Linux distribution, and tmux. PowerShellPlus verifies all three before changing this terminal."
            : "Off - this local Windows terminal closes when PowerShellPlus fully exits.";
    }

    private void SetTerminalEditorAccent(string? value)
    {
        terminalEditorAccentColor = WorkspaceAccentPalette.Normalize(value, WorkspaceAccentPalette.DefaultTerminal);
        accentSelectionSync = true;
        SessionAccentEdit.SelectedValue = WorkspaceAccentPalette.Choices.Any(choice => string.Equals(choice.Value, terminalEditorAccentColor, StringComparison.OrdinalIgnoreCase))
            ? terminalEditorAccentColor
            : null;
        accentSelectionSync = false;
        SessionAccentPreview.Background = WorkspaceAccentPalette.BrushFor(terminalEditorAccentColor, WorkspaceAccentPalette.DefaultTerminal);
        SessionAccentValueText.Text = terminalEditorAccentColor;
    }

    private void SetWorkspaceEditorAccent(string? value)
    {
        workspaceEditorAccentColor = WorkspaceAccentPalette.Normalize(value, WorkspaceAccentPalette.DefaultSession);
        accentSelectionSync = true;
        WorkspaceSessionAccentEdit.SelectedValue = WorkspaceAccentPalette.Choices.Any(choice => string.Equals(choice.Value, workspaceEditorAccentColor, StringComparison.OrdinalIgnoreCase))
            ? workspaceEditorAccentColor
            : null;
        accentSelectionSync = false;
        WorkspaceSessionAccentPreview.Background = WorkspaceAccentPalette.BrushFor(workspaceEditorAccentColor, WorkspaceAccentPalette.DefaultSession);
        WorkspaceSessionAccentValueText.Text = workspaceEditorAccentColor;
    }

    private void SessionAccentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!accentSelectionSync && SessionAccentEdit.SelectedValue is string value) SetTerminalEditorAccent(value);
    }

    private void WorkspaceSessionAccentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!accentSelectionSync && WorkspaceSessionAccentEdit.SelectedValue is string value) SetWorkspaceEditorAccent(value);
    }

    private void OpenTerminalColorPickerClick(object sender, RoutedEventArgs e) => OpenAccentColorPicker(AccentColorPickerTarget.Terminal, terminalEditorAccentColor);
    private void OpenWorkspaceColorPickerClick(object sender, RoutedEventArgs e) => OpenAccentColorPicker(AccentColorPickerTarget.WorkspaceSession, workspaceEditorAccentColor);

    private void OpenAccentColorPicker(AccentColorPickerTarget target, string colorValue)
    {
        accentColorPickerTarget = target;
        accentColorPickerOriginalValue = IsCustomThemePickerTarget(target) ? colorValue : null;
        var fallback = target switch
        {
            AccentColorPickerTarget.Terminal => WorkspaceAccentPalette.DefaultTerminal,
            AccentColorPickerTarget.WorkspaceSession => WorkspaceAccentPalette.DefaultSession,
            _ => "#89B4FA"
        };
        var color = (Color)ColorConverter.ConvertFromString(AppThemeCatalog.NormalizeColor(colorValue, fallback))!;
        (accentPickerHue, accentPickerSaturation, accentPickerValue) = RgbToHsv(color);
        AccentColorPickerOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => UpdateAccentColorPickerVisuals(true), DispatcherPriority.Loaded);
    }

    private static bool IsCustomThemePickerTarget(AccentColorPickerTarget target) => target is
        AccentColorPickerTarget.ThemeBackground or AccentColorPickerTarget.ThemeSurface or
        AccentColorPickerTarget.ThemeAccent or AccentColorPickerTarget.ThemeText or AccentColorPickerTarget.ThemeGradientEnd;

    private void CloseAccentColorPicker(bool restorePreview = false)
    {
        if (restorePreview && accentColorPickerOriginalValue is { } original && IsCustomThemePickerTarget(accentColorPickerTarget))
            SetCustomThemeColor(accentColorPickerTarget, original);
        accentFieldDragging = false;
        accentHueDragging = false;
        accentColorPickerOriginalValue = null;
        Mouse.Capture(null);
        AccentColorPickerOverlay.Visibility = Visibility.Collapsed;
    }

    private void AccentColorPickerBackdropMouseDown(object sender, MouseButtonEventArgs e) { CloseAccentColorPicker(true); e.Handled = true; }
    private void AccentColorPickerCardMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void CancelAccentColorPickerClick(object sender, RoutedEventArgs e) { CloseAccentColorPicker(true); e.Handled = true; }
    private void ApplyAccentColorPickerClick(object sender, RoutedEventArgs e)
    {
        var value = ColorToHex(HsvToColor(accentPickerHue, accentPickerSaturation, accentPickerValue));
        switch (accentColorPickerTarget)
        {
            case AccentColorPickerTarget.Terminal: SetTerminalEditorAccent(value); break;
            case AccentColorPickerTarget.WorkspaceSession: SetWorkspaceEditorAccent(value); break;
            default: SetCustomThemeColor(accentColorPickerTarget, value); break;
        }
        CloseAccentColorPicker();
        e.Handled = true;
    }

    private void AccentColorFieldMouseDown(object sender, MouseButtonEventArgs e)
    {
        accentFieldDragging = true;
        AccentColorField.CaptureMouse();
        UpdateAccentFieldFromPoint(e.GetPosition(AccentColorField));
        e.Handled = true;
    }

    private void AccentColorFieldMouseMove(object sender, MouseEventArgs e)
    {
        if (accentFieldDragging && e.LeftButton == MouseButtonState.Pressed) UpdateAccentFieldFromPoint(e.GetPosition(AccentColorField));
    }

    private void AccentColorFieldMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!accentFieldDragging) return;
        UpdateAccentFieldFromPoint(e.GetPosition(AccentColorField));
        accentFieldDragging = false;
        AccentColorField.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateAccentFieldFromPoint(Point point)
    {
        accentPickerSaturation = Math.Clamp(point.X / Math.Max(1, AccentColorField.ActualWidth), 0, 1);
        accentPickerValue = 1 - Math.Clamp(point.Y / Math.Max(1, AccentColorField.ActualHeight), 0, 1);
        UpdateAccentColorPickerVisuals(true);
    }

    private void AccentHueBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        accentHueDragging = true;
        AccentHueBar.CaptureMouse();
        UpdateAccentHueFromPoint(e.GetPosition(AccentHueBar));
        e.Handled = true;
    }

    private void AccentHueBarMouseMove(object sender, MouseEventArgs e)
    {
        if (accentHueDragging && e.LeftButton == MouseButtonState.Pressed) UpdateAccentHueFromPoint(e.GetPosition(AccentHueBar));
    }

    private void AccentHueBarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!accentHueDragging) return;
        UpdateAccentHueFromPoint(e.GetPosition(AccentHueBar));
        accentHueDragging = false;
        AccentHueBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateAccentHueFromPoint(Point point)
    {
        accentPickerHue = Math.Clamp(point.X / Math.Max(1, AccentHueBar.ActualWidth), 0, 1) * 360;
        UpdateAccentColorPickerVisuals(true);
    }

    private void AccentColorHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (accentHexSync || AccentColorPickerOverlay.Visibility != Visibility.Visible) return;
        var value = AccentColorHexEdit.Text.Trim();
        if (value is not { Length: 7 } || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit)) return;
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        (accentPickerHue, accentPickerSaturation, accentPickerValue) = RgbToHsv(color);
        UpdateAccentColorPickerVisuals(false);
    }

    private void UpdateAccentColorPickerVisuals(bool updateHex)
    {
        var color = HsvToColor(accentPickerHue, accentPickerSaturation, accentPickerValue);
        AccentColorHueBase.Background = new SolidColorBrush(HsvToColor(accentPickerHue, 1, 1));
        AccentColorPreview.Background = new SolidColorBrush(color);
        Canvas.SetLeft(AccentColorFieldThumb, Math.Clamp(accentPickerSaturation * AccentColorField.ActualWidth - AccentColorFieldThumb.Width / 2, -AccentColorFieldThumb.Width / 2, Math.Max(0, AccentColorField.ActualWidth - AccentColorFieldThumb.Width / 2)));
        Canvas.SetTop(AccentColorFieldThumb, Math.Clamp((1 - accentPickerValue) * AccentColorField.ActualHeight - AccentColorFieldThumb.Height / 2, -AccentColorFieldThumb.Height / 2, Math.Max(0, AccentColorField.ActualHeight - AccentColorFieldThumb.Height / 2)));
        Canvas.SetLeft(AccentHueThumb, Math.Clamp(accentPickerHue / 360 * AccentHueBar.ActualWidth - AccentHueThumb.Width / 2, -AccentHueThumb.Width / 2, Math.Max(0, AccentHueBar.ActualWidth - AccentHueThumb.Width / 2)));
        var colorValue = ColorToHex(color);
        if (updateHex)
        {
            accentHexSync = true;
            AccentColorHexEdit.Text = colorValue;
            AccentColorHexEdit.CaretIndex = AccentColorHexEdit.Text.Length;
            accentHexSync = false;
        }
        if (IsCustomThemePickerTarget(accentColorPickerTarget) && AccentColorPickerOverlay.Visibility == Visibility.Visible)
            SetCustomThemeColor(accentColorPickerTarget, colorValue);
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var red = color.R / 255d; var green = color.G / 255d; var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue)); var minimum = Math.Min(red, Math.Min(green, blue)); var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == red ? 60 * (((green - blue) / delta) % 6) : maximum == green ? 60 * ((blue - red) / delta + 2) : 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, maximum == 0 ? 0 : delta / maximum, maximum);
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation; var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1)); var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((red + match) * 255), (byte)Math.Round((green + match) * 255), (byte)Math.Round((blue + match) * 255));
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
        var targets = new ObservableCollection<SessionProfile>(state.Sessions);
        targets.Insert(0, new SessionProfile { Id = "*", Name = "All terminals", CommandLine = string.Empty });
        targets.Insert(0, new SessionProfile { Id = AutomationRule.NoTarget, Name = "None · manual only", CommandLine = string.Empty });
        AutomationTargetEdit.ItemsSource = targets; AutomationTargetEdit.SelectedValue = rule?.TargetSessionId ?? AutomationRule.NoTarget;
        AutomationTypeEdit.SelectedIndex = rule?.ScheduleType switch { "Interval" => 1, "Daily" => 2, "Once" => 3, _ => 0 };
        AutomationValueEdit.Text = (rule?.IntervalMinutes ?? 60).ToString(CultureInfo.InvariantCulture);
        var exactTime = TimeSpan.TryParseExact(rule?.DailyTime ?? "09:00", @"hh\:mm", CultureInfo.InvariantCulture, out var parsedTime) ? parsedTime : TimeSpan.FromHours(9);
        var hour = exactTime.Hours % 12; if (hour == 0) hour = 12;
        AutomationHourEdit.SelectedItem = hour;
        AutomationMinuteEdit.SelectedItem = exactTime.Minutes.ToString("00", CultureInfo.InvariantCulture);
        AutomationAmPmEdit.SelectedIndex = exactTime.Hours >= 12 ? 1 : 0;
        AutomationDateEdit.SelectedDate = DateTime.TryParseExact(rule?.ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTime.Today;
        AutomationClearLineEdit.IsChecked = rule?.ClearLine ?? false;
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
        CloseAccentColorPicker();
        EditorOverlay.Visibility = Visibility.Collapsed;
        TerminalHost.Visibility = Visibility.Visible;
    }

    private async Task<bool> ApplyTerminalEditAsync(SessionProfile profile, string name, string commandLine, string workingDirectory, bool autoStart,
        string? accentColor = null, bool? useRemoteTmux = null, bool? useLocalTmux = null, string? localTmuxDistribution = null)
    {
        var restartRequired = TerminalEditRequiresRestart(profile, commandLine, workingDirectory, useRemoteTmux, useLocalTmux);
        if (!panes.TryGetValue(profile.Id, out var pane))
        {
            profile.Name = name;
            profile.AccentColor = WorkspaceAccentPalette.Normalize(accentColor ?? profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal);
            profile.CommandLine = commandLine;
            profile.WorkingDirectory = workingDirectory;
            profile.AutoStart = autoStart;
            if (useRemoteTmux is bool detached) profile.UseRemoteTmux = detached;
            if (useLocalTmux is bool local) profile.UseLocalTmux = local;
            profile.LocalTmuxDistribution = profile.UseLocalTmux ? localTmuxDistribution ?? profile.LocalTmuxDistribution : null;
            return restartRequired;
        }

        // A running ConPTY process cannot be adopted by tmux. Capture its exact
        // recoverable identity first, then perform a verified restart into tmux.
        // This preserves Codex/Hermes/SSH identity instead of silently replacing
        // the terminal with a blank shell.
        if (restartRequired) CaptureRecoverySnapshot();
        var recoverySnapshot = SessionRecoveryStore.Load();
        recoverySnapshot.Sessions.TryGetValue(profile.Id, out var savedRecovery);
        var originalRecovery = savedRecovery?.CopyForTransition();
        var transitionRecovery = savedRecovery?.CopyForTransition() ?? new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            WorkingDirectory = profile.WorkingDirectory,
            CapturedUtc = DateTime.UtcNow
        };
        var previous = new TerminalEditRollback(profile);
        var requestedLocalTmux = useLocalTmux ?? profile.UseLocalTmux;
        var requestedRemoteTmux = useRemoteTmux ?? profile.UseRemoteTmux;
        if (restartRequired && requestedRemoteTmux && profile.LiveWorkingDirectoryIsSsh && !transitionRecovery.SshWasActive)
            throw new InvalidOperationException("PowerShellPlus could not capture a safe SSH recovery command. The terminal was not restarted or moved into tmux.");

        transitionRecovery.LocalTmuxManaged = requestedLocalTmux;
        transitionRecovery.LocalTmuxSessionName = requestedLocalTmux ? LocalTmuxSession.GetSessionName(profile.Id) : null;
        transitionRecovery.LocalTmuxDistribution = requestedLocalTmux ? localTmuxDistribution ?? profile.LocalTmuxDistribution : null;
        if (transitionRecovery.SshWasActive)
        {
            transitionRecovery.RemoteTmuxManaged = requestedRemoteTmux;
            transitionRecovery.RemoteTmuxSessionName = requestedRemoteTmux ? RemoteTmuxSession.GetSessionName(profile.Id) : null;
        }

        var replaceLocalSession = profile.UseLocalTmux
            && (useLocalTmux == false || !string.Equals(profile.CommandLine, commandLine, StringComparison.Ordinal)
                || !PathsEqual(profile.WorkingDirectory, workingDirectory));
        try
        {
            if (replaceLocalSession)
            {
                var stopped = await LocalTmuxSession.KillAsync(profile.Id, profile.LocalTmuxDistribution);
                if (!stopped.CommandSucceeded) throw new InvalidOperationException(stopped.Message);
            }
            profile.Name = name;
            profile.AccentColor = WorkspaceAccentPalette.Normalize(accentColor ?? profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal);
            profile.CommandLine = commandLine;
            profile.WorkingDirectory = workingDirectory;
            profile.AutoStart = autoStart;
            profile.UseRemoteTmux = requestedRemoteTmux;
            profile.UseLocalTmux = requestedLocalTmux;
            profile.LocalTmuxDistribution = requestedLocalTmux ? localTmuxDistribution ?? profile.LocalTmuxDistribution : null;
            if (restartRequired)
            {
                pane.ApplyProfile(profile, transitionRecovery);
                if (!await pane.RestartAndVerifyAsync(transitionRecovery))
                    throw new InvalidOperationException("The replacement terminal exited before its tmux/recovery backend was ready.");
                if (transitionRecovery is { SshWasActive: true, RemoteTmuxManaged: true }
                    && !await WaitForRemoteTmuxSessionAsync(transitionRecovery))
                    throw new InvalidOperationException("The SSH connection restarted, but the remote tmux session could not be verified.");
                transitionRecovery.CapturedUtc = DateTime.UtcNow;
                PersistRecoveryEntry(transitionRecovery);
            }
            else pane.RefreshProfileDisplay(profile);
        }
        catch (Exception exception)
        {
            if (requestedLocalTmux && !previous.UseLocalTmux)
            {
                try { _ = await LocalTmuxSession.KillAsync(profile.Id, localTmuxDistribution); } catch { }
            }
            if (transitionRecovery is { SshWasActive: true, RemoteTmuxManaged: true } && !previous.UseRemoteTmux)
            {
                try { _ = await RemoteTmuxSession.KillAsync(transitionRecovery); } catch { }
            }
            previous.Restore(profile);
            pane.ApplyProfile(profile, originalRecovery);
            var rollbackReady = await pane.RestartAndVerifyAsync(originalRecovery);
            if (originalRecovery is not null) PersistRecoveryEntry(originalRecovery);
            else RemoveRecoveryEntry(profile.Id);
            throw new InvalidOperationException(rollbackReady
                ? exception.GetBaseException().Message + " The original terminal configuration was restored."
                : exception.GetBaseException().Message + " The original recovery state was preserved, but its renderer also needs Retry.", exception);
        }
        SessionList.Items.Refresh();
        TerminalTabList.Items.Refresh();
        return restartRequired;
    }

    private async Task<bool> WaitForRemoteTmuxSessionAsync(SessionRecoveryEntry recovery)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            var status = await RemoteTmuxSession.ProbeAsync(recovery);
            if (status.CommandSucceeded && status.SessionExists) return true;
            await Task.Delay(300);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    private void PersistRecoveryEntry(SessionRecoveryEntry entry)
    {
        lock (recoveryCaptureSync)
        {
            var snapshot = SessionRecoveryStore.Load();
            snapshot.Sessions[entry.SessionId] = entry.CopyForTransition();
            snapshot.CapturedUtc = DateTime.UtcNow;
            SessionRecoveryStore.Save(snapshot);
            loadedRecovery.Sessions[entry.SessionId] = entry.CopyForTransition();
        }
    }

    private void RemoveRecoveryEntry(string terminalId)
    {
        lock (recoveryCaptureSync)
        {
            var snapshot = SessionRecoveryStore.Load();
            snapshot.Sessions.Remove(terminalId);
            snapshot.CapturedUtc = DateTime.UtcNow;
            SessionRecoveryStore.Save(snapshot);
            loadedRecovery.Sessions.Remove(terminalId);
        }
    }

    private sealed record TerminalEditRollback(string Name, string AccentColor, string CommandLine, string WorkingDirectory,
        bool AutoStart, bool UseRemoteTmux, bool UseLocalTmux, string? LocalTmuxDistribution)
    {
        public TerminalEditRollback(SessionProfile profile) : this(profile.Name, profile.AccentColor, profile.CommandLine,
            profile.WorkingDirectory, profile.AutoStart, profile.UseRemoteTmux, profile.UseLocalTmux, profile.LocalTmuxDistribution) { }

        public void Restore(SessionProfile profile)
        {
            profile.Name = Name;
            profile.AccentColor = AccentColor;
            profile.CommandLine = CommandLine;
            profile.WorkingDirectory = WorkingDirectory;
            profile.AutoStart = AutoStart;
            profile.UseRemoteTmux = UseRemoteTmux;
            profile.UseLocalTmux = UseLocalTmux;
            profile.LocalTmuxDistribution = LocalTmuxDistribution;
        }
    }

    internal static bool TerminalEditRequiresRestart(SessionProfile profile, string commandLine, string workingDirectory, bool? useRemoteTmux,
        bool? useLocalTmux = null)
    {
        if (!string.Equals(profile.CommandLine, commandLine, StringComparison.Ordinal)
            || !PathsEqual(profile.WorkingDirectory, workingDirectory)) return true;
        if (useLocalTmux is bool requestedLocalTmux
            && (profile.UseLocalTmux != requestedLocalTmux || requestedLocalTmux && !profile.IsTmuxTerminal)) return true;
        return useRemoteTmux is bool requestedTmux
            && (profile.LiveWorkingDirectoryIsSsh || profile.IsTmuxTerminal && !profile.UseLocalTmux)
            && (profile.UseRemoteTmux != requestedTmux || profile.IsTmuxTerminal != requestedTmux);
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

    private async Task<(bool Valid, string? Distribution)> ValidateLocalTmuxSelectionAsync(SessionProfile? profile)
    {
        if (SessionUseLocalTmuxEdit.IsChecked != true) return (true, null);
        SessionUseLocalTmuxEdit.IsEnabled = false;
        SessionLocalTmuxStatusText.Text = "Verifying WSL and tmux before saving...";
        try
        {
            var status = await LocalTmuxSession.ProbeAsync(profile?.LocalTmuxDistribution, profile?.Id);
            SessionLocalTmuxStatusText.Text = status.Message;
            if (status.WslAvailable && status.TmuxAvailable && !string.IsNullOrWhiteSpace(status.Distribution))
                return (true, status.Distribution);
            PowerShellPlusDialog.ShowMessage(this,
                status.Message + "\n\nLocal tmux was not enabled and this terminal was not restarted.",
                "Local tmux needs WSL", PowerShellPlusDialogKind.Warning);
            return (false, null);
        }
        finally { SessionUseLocalTmuxEdit.IsEnabled = true; }
    }

    private async void SaveEditorClick(object sender, RoutedEventArgs e)
    {
        if (editorMode == EditorMode.Terminal)
        {
            if (string.IsNullOrWhiteSpace(SessionNameEdit.Text) || string.IsNullOrWhiteSpace(SessionCommandEdit.Text) || !Directory.Exists(SessionDirectoryEdit.Text)) { UpdateStatus("Session fields are incomplete"); return; }
            var localTmux = await ValidateLocalTmuxSelectionAsync(editingValue as SessionProfile);
            if (!localTmux.Valid) return;
            try
            {
                if (editingValue is SessionProfile existing)
                {
                    await ApplyTerminalEditAsync(existing, SessionNameEdit.Text.Trim(), SessionCommandEdit.Text.Trim(), SessionDirectoryEdit.Text.Trim(), SessionAutoStartEdit.IsChecked == true,
                        terminalEditorAccentColor, SessionUseTmuxEdit.IsChecked == true, SessionUseLocalTmuxEdit.IsChecked == true, localTmux.Distribution);
                    existing.AgentNotificationsEnabled = SessionAgentNotificationsEdit.IsChecked == true;
                }
                else
                {
                    var created = new SessionProfile
                    {
                        Name = SessionNameEdit.Text.Trim(), AccentColor = terminalEditorAccentColor,
                        CommandLine = SessionCommandEdit.Text.Trim(), WorkingDirectory = SessionDirectoryEdit.Text.Trim(),
                        AutoStart = SessionAutoStartEdit.IsChecked == true, UseRemoteTmux = SessionUseTmuxEdit.IsChecked == true,
                        UseLocalTmux = SessionUseLocalTmuxEdit.IsChecked == true, LocalTmuxDistribution = localTmux.Distribution,
                        AgentNotificationsEnabled = SessionAgentNotificationsEdit.IsChecked == true
                    };
                    AddTerminalToActiveSession(created);
                    CreatePane(created);
                    if (created.UseLocalTmux && !await panes[created.Id].PrepareLocalTmuxBackendAsync())
                        throw new InvalidOperationException("The local tmux backend could not be created. The terminal remains available with Retry.");
                    SelectPane(created.Id, false);
                    ApplyLayout();
                }
            }
            catch (InvalidOperationException exception)
            {
                LogNativeError("Local tmux terminal edit", exception);
                PowerShellPlusDialog.ShowMessage(this, exception.Message + "\n\nThe terminal was left unchanged.",
                    "Local tmux could not be changed", PowerShellPlusDialogKind.Error);
                return;
            }
        }
        else if (editorMode == EditorMode.WorkspaceSession)
        {
            if (editingValue is not TerminalSession session || string.IsNullOrWhiteSpace(WorkspaceSessionNameEdit.Text)) return;
            session.Name = WorkspaceSessionNameEdit.Text.Trim();
            session.AccentColor = workspaceEditorAccentColor;
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
            value.Name = AutomationNameEdit.Text.Trim(); value.Command = AutomationCommandEdit.Text.Trim(); value.TargetSessionId = AutomationTargetEdit.SelectedValue?.ToString() ?? AutomationRule.NoTarget; value.ScheduleType = AutomationTypeEdit.SelectedIndex switch { 1 => "Interval", 2 => "Daily", 3 => "Once", _ => AutomationRule.NoSchedule }; value.Enabled = AutomationEnabledEdit.IsChecked == true; value.ClearLine = AutomationClearLineEdit.IsChecked == true;
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

    private async Task CloseTerminalAsync(SessionProfile profile)
    {
        if (!panes.ContainsKey(profile.Id) || remoteDetachOperations.Contains(profile.Id)) return;
        if (profile.UseLocalTmux)
        {
            if (state.Settings.ConfirmBeforeRemove && !PowerShellPlusDialog.Confirm(this,
                    $"Remove {profile.Name}?\n\nIts local tmux session and every process inside it will be stopped. Closing or restarting PowerShellPlus itself keeps this terminal running instead.",
                    "Stop local tmux terminal?", PowerShellPlusDialogKind.Question,
                    "Stop & remove", "Cancel", defaultToPrimary: false, primaryIsDangerous: true)) return;
            await StopLocalAndRemoveAsync(profile);
            return;
        }
        if (panes.TryGetValue(profile.Id, out var livePane) && (profile.LiveWorkingDirectoryIsSsh || livePane.HasActiveSshConnection))
            await CaptureRecoverySnapshotAsync();
        var snapshot = SessionRecoveryStore.Load();
        if (!snapshot.Sessions.TryGetValue(profile.Id, out var recovery) || recovery.SshWasActive != true)
        {
            await RemoveSessionResponsiveAsync(profile);
            return;
        }

        var choice = PowerShellPlusDialog.ShowActions(this,
            $"{profile.Name} is connected through SSH.\n\nKeep running detaches this PowerShellPlus terminal while its remote shell, Codex, or Hermes process continues inside a managed tmux session. Selecting the terminal again reconnects to the exact live process.\n\nStop & remove closes the remote process and deletes this terminal.",
            "Close SSH terminal?", PowerShellPlusDialogKind.Question,
            "Keep running", "Stop & remove", "Cancel", defaultToPrimary: true, primaryIsDangerous: false);
        if (choice == PowerShellPlusDialogResult.Primary) await DetachRemoteTerminalAsync(profile, recovery);
        else if (choice == PowerShellPlusDialogResult.Secondary) await StopRemoteAndRemoveAsync(profile, recovery);
    }

    private async Task<bool> StopLocalAndRemoveAsync(SessionProfile profile)
    {
        if (!remoteDetachOperations.Add(profile.Id)) return false;
        try
        {
            var stopped = await LocalTmuxSession.KillAsync(profile.Id, profile.LocalTmuxDistribution);
            if (!stopped.CommandSucceeded)
            {
                PowerShellPlusDialog.ShowMessage(this,
                    stopped.Message + "\n\nNothing was removed, so PowerShellPlus did not orphan a terminal it could not verify as stopped.",
                    "Local tmux session could not be stopped", PowerShellPlusDialogKind.Error);
                return false;
            }
            return await RemoveSessionResponsiveAsync(profile, alreadyConfirmed: true);
        }
        finally { remoteDetachOperations.Remove(profile.Id); }
    }

    private async Task DetachRemoteTerminalAsync(SessionProfile profile, SessionRecoveryEntry? suppliedRecovery = null)
    {
        if (!panes.TryGetValue(profile.Id, out var pane) || !remoteDetachOperations.Add(profile.Id)) return;
        SessionRecoveryEntry? recoveryForReconnect = suppliedRecovery;
        var paneWasStopped = false;
        try
        {
            CaptureRecoverySnapshot();
            var snapshot = SessionRecoveryStore.Load();
            var recovery = suppliedRecovery;
            if (snapshot.Sessions.TryGetValue(profile.Id, out var latest)) recovery = latest;
            recoveryForReconnect = recovery;
            if (recovery?.SshWasActive != true)
            {
                PowerShellPlusDialog.ShowMessage(this, "This terminal does not currently have a verified SSH connection to detach.",
                    "Nothing to detach", PowerShellPlusDialogKind.Information);
                return;
            }

            UpdateStatus($"Checking the persistent remote session for {profile.Name}…");
            var remote = await RemoteTmuxSession.ProbeAsync(recovery);
            if (!remote.TmuxAvailable)
            {
                PowerShellPlusDialog.ShowMessage(this,
                    remote.Message + "\n\nInstall tmux on the remote machine (for Ubuntu/Debian: sudo apt-get install tmux), then reconnect this SSH terminal once. PowerShellPlus will manage it automatically.",
                    "Remote persistence unavailable", PowerShellPlusDialogKind.Warning);
                UpdateStatus("Remote terminal left open — tmux was not available");
                return;
            }

            if (!remote.SessionExists)
            {
                var migrate = PowerShellPlusDialog.Confirm(this,
                    "This SSH connection was created before managed remote persistence was enabled.\n\nPowerShellPlus can close the current SSH client, resume the saved Codex or Hermes chat inside tmux, and leave it running in the background. The transcript, model, and permissions are preserved, but an in-progress response may need to be sent again.",
                    "Move this session into tmux?", PowerShellPlusDialogKind.Question,
                    "Move & keep running", "Cancel", defaultToPrimary: true);
                if (!migrate) return;
                pane.Stop();
                paneWasStopped = true;
                await Task.Delay(700);
                recovery.RemoteTmuxManaged = true;
                recovery.RemoteTmuxSessionName = RemoteTmuxSession.GetSessionName(profile.Id);
                var ensured = await RemoteTmuxSession.EnsureDetachedAsync(recovery);
                if (!ensured.SessionExists)
                {
                    profile.SetRemoteDetached(false);
                    await pane.RestartAsync(recovery);
                    PowerShellPlusDialog.ShowMessage(this,
                        ensured.Message + "\n\nPowerShellPlus reconnected the terminal instead, so the saved session was not discarded.",
                        "Could not keep the remote process running", PowerShellPlusDialogKind.Error);
                    UpdateStatus("Remote migration failed — terminal reconnected safely");
                    return;
                }
            }
            else
            {
                pane.Stop();
                paneWasStopped = true;
            }

            recovery.RemoteTmuxManaged = true;
            recovery.RemoteTmuxSessionName = RemoteTmuxSession.GetSessionName(profile.Id);
            recovery.CapturedUtc = DateTime.UtcNow;
            snapshot.Sessions[profile.Id] = recovery;
            loadedRecovery.Sessions[profile.Id] = recovery;
            SessionRecoveryStore.Save(snapshot);
            MarkTerminalDetached(profile, pane);
            UpdateStatus($"{profile.Name} detached — its remote process is still running");
        }
        catch (Exception exception)
        {
            LogNativeError("Remote tmux detach", exception);
            if (paneWasStopped && recoveryForReconnect is not null)
            {
                try
                {
                    profile.SetRemoteDetached(false);
                    await pane.RestartAsync(recoveryForReconnect);
                }
                catch (Exception reconnectException)
                {
                    LogNativeError("Remote tmux detach recovery", reconnectException);
                }
            }
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "Could not detach remote terminal", PowerShellPlusDialogKind.Error);
        }
        finally { remoteDetachOperations.Remove(profile.Id); }
    }

    private void MarkTerminalDetached(SessionProfile profile, TerminalPane pane)
    {
        profile.SetTmuxTerminal(true);
        profile.SetRemoteDetached(true);
        if (activePane == pane)
        {
            var next = activeWorkspaceSession?.TerminalIds
                .Where(value => value != profile.Id && panes.TryGetValue(value, out var candidate) && !candidate.Profile.IsRemoteDetached)
                .Select(value => panes[value]).FirstOrDefault();
            activePane = next;
            if (activeWorkspaceSession is not null) activeWorkspaceSession.ActiveTerminalId = next?.Profile.Id;
            SessionList.SelectedItem = next?.Profile;
            terminalTabSelectionSync = true;
            TerminalTabList.SelectedItem = next?.Profile;
            terminalTabSelectionSync = false;
        }
        pane.SetActive(false);
        ApplyLayout();
        ScheduleSave();
    }

    private async Task ReattachRemoteTerminalAsync(TerminalPane pane, bool focus)
    {
        var profile = pane.Profile;
        if (!profile.IsRemoteDetached || !remoteDetachOperations.Add(profile.Id)) return;
        try
        {
            var snapshot = SessionRecoveryStore.Load();
            if (!snapshot.Sessions.TryGetValue(profile.Id, out var recovery) || !recovery.RemoteTmuxManaged)
                throw new InvalidOperationException("The saved remote tmux identity is missing.");
            UpdateStatus($"Reattaching {profile.Name} to its remote process…");
            profile.SetRemoteDetached(false);
            await pane.RestartAsync(recovery);
            SelectPane(profile.Id, focus);
            ApplyLayout();
            UpdateStatus($"Reattached {profile.Name} to {recovery.RemoteTmuxSessionName}");
        }
        catch (Exception exception)
        {
            profile.SetRemoteDetached(true);
            ApplyLayout();
            LogNativeError("Remote tmux reattach", exception);
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "Remote session could not be reattached", PowerShellPlusDialogKind.Error);
        }
        finally { remoteDetachOperations.Remove(profile.Id); }
    }

    private async Task<bool> StopRemoteAndRemoveAsync(SessionProfile profile, SessionRecoveryEntry recovery)
    {
        if (!remoteDetachOperations.Add(profile.Id)) return false;
        try
        {
            var remote = await RemoteTmuxSession.ProbeAsync(recovery);
            if (!remote.CommandSucceeded)
            {
                PowerShellPlusDialog.ShowMessage(this,
                    remote.Message + "\n\nNothing was removed. Reconnect the SSH host and try again so PowerShellPlus can verify that the remote process is stopped.",
                    "Remote process could not be verified", PowerShellPlusDialogKind.Error);
                return false;
            }
            if (remote.SessionExists)
            {
                var stopped = await RemoteTmuxSession.KillAsync(recovery);
                if (!stopped.CommandSucceeded)
                {
                    PowerShellPlusDialog.ShowMessage(this, stopped.Message,
                        "Remote process could not be stopped", PowerShellPlusDialogKind.Error);
                    return false;
                }
            }
            return await RemoveSessionResponsiveAsync(profile, alreadyConfirmed: true);
        }
        finally { remoteDetachOperations.Remove(profile.Id); }
    }

    private async Task<bool> RemoveSessionResponsiveAsync(SessionProfile profile, bool alreadyConfirmed = false)
    {
        if (!panes.TryGetValue(profile.Id, out var pane)) return false;
        if (!alreadyConfirmed && state.Settings.ConfirmBeforeRemove && !PowerShellPlusDialog.Confirm(this,
                $"Remove {profile.Name}?\n\nThe live terminal process will be closed and this terminal will be removed from its session.",
                "Remove terminal?", PowerShellPlusDialogKind.Question,
                "Remove", "Cancel", defaultToPrimary: true, primaryIsDangerous: true)) return false;
        if (!RemoveSession(profile, alreadyConfirmed: true, stopPane: false, releaseAuxiliary: false, deleteArtifacts: false)) return false;
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.WhenAll(pane.StopBackendAsync(), Task.Run(() =>
        {
            try { SessionRecoveryStore.DeleteSession(profile.Id); } catch { }
            try { LocalTmuxSession.DeleteLaunchArtifacts(profile.Id); } catch { }
        }));
        return true;
    }

    private bool RemoveSession(SessionProfile profile, bool alreadyConfirmed = false, bool stopPane = true,
        bool releaseAuxiliary = true, bool deleteArtifacts = true)
    {
        if (!panes.TryGetValue(profile.Id, out var pane)) return false;
        if (!alreadyConfirmed && state.Settings.ConfirmBeforeRemove && !PowerShellPlusDialog.Confirm(this,
                $"Remove {profile.Name}?\n\nThe live terminal process will be closed and this terminal will be removed from its session.",
                "Remove terminal?", PowerShellPlusDialogKind.Question,
                "Remove", "Cancel", defaultToPrimary: true, primaryIsDangerous: true)) return false;
        if (stopPane) pane.Stop();
        else if (releaseAuxiliary) pane.ReleaseAuxiliaryResources();
        TerminalHost.Children.Remove(pane); panes.Remove(profile.Id); state.Sessions.Remove(profile);
        if (deleteArtifacts)
        {
            SessionRecoveryStore.DeleteSession(profile.Id);
            LocalTmuxSession.DeleteLaunchArtifacts(profile.Id);
        }
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
        TerminalTabList.Items.Refresh();
        UpdateCounts();
    }

    private void RunSnippet(bool all) { if (SnippetList.SelectedItem is CommandSnippet value) { if (all) foreach (var pane in panes.Values) pane.SendCommand(value.Command); else activePane?.SendCommand(value.Command); UpdateStatus($"Ran {value.Name}"); } }
    private List<TerminalPane> AutomationTargets(AutomationRule rule)
    {
        if (rule.TargetSessionId == AutomationRule.NoTarget) return [];
        if (rule.TargetSessionId == "*") return panes.Values.ToList();
        return panes.TryGetValue(rule.TargetSessionId, out var pane) ? [pane] : [];
    }

    private async Task<int> RunAutomationAsync(AutomationRule rule, bool recordRun)
    {
        var targets = AutomationTargets(rule);
        var results = await Task.WhenAll(targets.Select(target => target.RunAutomationAsync(rule)));
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
    private async void SaveTimerTick(object? sender, EventArgs e)
    {
        saveTimer.Stop();
        await SaveNowInBackgroundAsync();
    }
    private async Task SaveNowInBackgroundAsync()
    {
        if (workspaceSaveInProgress)
        {
            workspaceSaveRequestedWhileBusy = true;
            return;
        }
        workspaceSaveInProgress = true;
        try
        {
            do
            {
                workspaceSaveRequestedWhileBusy = false;
                try { await WorkspaceStore.SaveAsync(state); }
                catch (Exception exception) { UpdateStatus(exception.Message); }
            }
            while (workspaceSaveRequestedWhileBusy && !shutdownComplete);
        }
        finally { workspaceSaveInProgress = false; }
    }
    private void SaveNow() { try { WorkspaceStore.Save(state); } catch (Exception exception) { UpdateStatus(exception.Message); } }
    private void UpdateStatus(string text) { StatusText.Text = text; UpdateCounts(); }
    private void UpdateCounts() => CountText.Text = $"{state.TerminalSessions.Count} session{(state.TerminalSessions.Count == 1 ? string.Empty : "s")} · {panes.Count} native terminal{(panes.Count == 1 ? string.Empty : "s")} · {terminalProfile.SchemeName}";

    private async void SettingsScrollerLoaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        ApplyThemedSettingsScrollbar();
    }

    private void ApplyThemedSettingsScrollbar()
    {
        if (FindVisualDescendant<ScrollBar>(SettingsScroller) is not { } scrollbar) return;
        scrollbar.Style = (Style)FindResource("ThemedScrollBar");
        scrollbar.Width = 11;
        scrollbar.MinWidth = 11;
    }

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
        var startupSnapshot = new StartupWindow { ShowActivated = false, ShowInTaskbar = false };
        startupSnapshot.Show();
        startupSnapshot.Report(new StartupProgress("Restoring terminals", "Connecting saved SSH sessions and terminal 3 of 8", 3, 8));
        await Settle();
        Render((FrameworkElement)startupSnapshot.Content, "ui-startup-loading.png");
        startupSnapshot.Close();
        await Settle();
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        var layoutButtons = LayoutButtons().Select(value => value.Button).ToArray();
        if (!WorkspaceSidebar.IsAncestorOf(LayoutControls) || LayoutControls.ActualWidth < 200 || layoutButtons.Any(button => button.ActualWidth < 40))
            throw new InvalidOperationException("Per-session layout controls must remain readable beneath the Terminals heading.");
        if (layoutButtons.Any(button => button.ToolTip is not ToolTip { Content: Border }))
            throw new InvalidOperationException("Every session layout action must provide a visual hover preview.");
        var openTerminalRight = TitleBarOpenWindowsTerminalButton.TranslatePoint(new Point(TitleBarOpenWindowsTerminalButton.ActualWidth, 0), root).X;
        var minimizeLeft = MinimizeButton.TranslatePoint(new Point(0, 0), root).X;
        if (openTerminalRight > minimizeLeft + 1)
            throw new InvalidOperationException($"Windows Terminal action must sit immediately before minimize. OpenRight={openTerminalRight:F1}, MinimizeLeft={minimizeLeft:F1}");
        Render(root, "ui-main.png");
        var snapshotTheme = state.Settings.ApplicationTheme;
        ShowSection(SettingsPanel);
        foreach (var theme in AppThemeCatalog.Themes)
        {
            SettingsThemeList.SelectedItem = theme;
            await Settle();
            Render(root, $"ui-theme-{theme.Id}.png");
        }
        SettingsThemeList.SelectedItem = AppThemeCatalog.BuiltInThemes.First(theme => theme.IsGradient);
        NewCustomThemeClick(this, new RoutedEventArgs());
        if (customThemeDraft is not null)
        {
            customThemeDraft.Name = "Northern lights";
            customThemeDraft.UseGradient = true;
            customThemeDraft.GradientDirection = "Horizontal";
            CustomThemeNameEdit.Text = customThemeDraft.Name;
            UpdateCustomThemeEditorVisuals();
        }
        await Settle();
        Render(root, "ui-custom-theme-editor.png");
        ThemeBackgroundColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Render(root, "ui-custom-theme-color-picker.png");
        CloseAccentColorPicker(true);
        CancelCustomThemeClick(this, new RoutedEventArgs());
        SettingsThemeList.SelectedItem = AppThemeCatalog.Resolve(snapshotTheme);
        ShowSection(SessionsPanel);
        await Settle();
        if (activePane is { } scrollbarSnapshotPane)
        {
            const string snapshotTail = "UI_SCROLLBAR_TAIL_READY";
            if (!await scrollbarSnapshotPane.SendCommandAsync(
                    "1..160 | ForEach-Object { Write-Output ('UI_SCROLLBAR_LINE_' + $_) }; Write-Output 'UI_SCROLLBAR_TAIL_READY'"))
                throw new InvalidOperationException("Terminal scrollbar snapshot command was not accepted.");
            var snapshotDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < snapshotDeadline
                && !scrollbarSnapshotPane.GetOutput().Contains(snapshotTail, StringComparison.Ordinal)) await Task.Delay(120);
            await Settle();
            if (!scrollbarSnapshotPane.TerminalScrollbarHasRangeForTest || !scrollbarSnapshotPane.ExerciseTerminalScrollbarForTest())
                throw new InvalidOperationException("Terminal scrollbar did not expose or move a real scrollback range.");
            Render(root, "ui-terminal-scrollbar.png");
        }
        if (activePane is { } historySnapshotPane)
        {
            var originalHistory = historySnapshotPane.Profile.CommandHistory.ToArray();
            var originalHistoryTimestamps = historySnapshotPane.Profile.CommandHistoryTimestampsUtc.ToArray();
            var originalDraft = historySnapshotPane.CommandInputTextForTest;
            historySnapshotPane.SetCommandHistoryForTest([
                "Deploy the validated release and verify every health endpoint.",
                "git status --short --branch\npnpm test"
            ]);
            historySnapshotPane.ShowCommandHistoryForTest();
            await Settle();
            Render(root, "ui-command-history.png");
            historySnapshotPane.HideCommandHistoryForTest();
            historySnapshotPane.SetCommandHistoryForTest(originalHistory);
            historySnapshotPane.Profile.CommandHistoryTimestampsUtc = originalHistoryTimestamps.ToList();
            historySnapshotPane.SetCommandInputForTest(originalDraft);
        }
        if (activePane is { } colorSnapshotPane)
        {
            OpenSessionEditor(colorSnapshotPane.Profile);
            OpenAccentColorPicker(AccentColorPickerTarget.Terminal, "#2DD4BF");
            await Settle();
            Render(root, "ui-custom-color-picker.png");
            HideEditor();
        }
        var snapshotLayout = activeWorkspaceSession?.Layout ?? "Grid";
        SetLayout("Tabs");
        await Settle();
        if (TerminalTabBar.Visibility != Visibility.Visible || TerminalHost.Children.OfType<TerminalPane>().Count() != 1)
            throw new InvalidOperationException("Tabs layout did not present one active terminal beneath its terminal tab strip.");
        Render(root, "ui-tabs-layout.png");
        SetLayout(snapshotLayout);
        await Settle();
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
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Microsoft TerminalControl hosts interactive ConPTY input/output.\nProfile={terminalProfile.ProfileName}\nFont={terminalProfile.FontFace}\nScheme={terminalProfile.SchemeName}\nStartup={pane.StartupDiagnosticForTest}\nCommand={pane.StartupCommandLineForTest}\n\n{output}");
        return success;
    }

    public async Task<bool> RunStartupRecoverySmokeTestAsync(string reportPath)
    {
        var fixtures = new List<SessionProfile>();
        TerminalPane? retryFixture = null;
        try
        {
            for (var index = 1; index <= 5; index++)
            {
                var profile = new SessionProfile
                {
                    Name = $"Inactive recovery fixture {index}",
                    CommandLine = terminalProfile.CommandLine,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };
                fixtures.Add(profile);
                CreatePane(profile);
                panes[profile.Id].SetStartupAttemptOverrideForTest(_ => Task.FromResult(true));
            }

            var queued = fixtures.Select(profile => StartTerminalForWorkspaceAsync(panes[profile.Id])).ToArray();
            var results = await Task.WhenAll(queued);
            var inactiveTerminalsStartEagerly = results.All(value => value.Ready)
                && fixtures.All(profile => panes[profile.Id].StartupAttemptCountForTest == 1 && !panes[profile.Id].IsLoaded);
            var warmupHostContract = TerminalWarmupHost.Width >= 800 && TerminalWarmupHost.Height >= 480
                && VisualTreeHelper.GetParent(TerminalWarmupHost) is Canvas { ClipToBounds: true, IsHitTestVisible: false };

            retryFixture = new TerminalPane(new SessionProfile
            {
                Name = "Startup retry fixture",
                CommandLine = terminalProfile.CommandLine,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }, EffectiveAppearance());
            retryFixture.SetStartupAttemptOverrideForTest(_ => Task.FromResult(false));
            var initialResult = await retryFixture.EnsureTerminalStartupAsync();
            var failureOffersRetry = !initialResult && retryFixture.StartupAttemptCountForTest == 3
                && retryFixture.StartupFailureVisibleForTest && retryFixture.StartupRetryButtonReadyForTest;
            retryFixture.SetStartupAttemptOverrideForTest(_ => Task.FromResult(true));
            var manualRetryWorks = await retryFixture.RetryTerminalStartupForTestAsync()
                && !retryFixture.StartupFailureVisibleForTest;
            var recoveredSshHandshakeContract = TerminalPane.RecoveredSshStartupContractPassesForTest();
            var previousRecovery = new SessionRecoveryEntry
            {
                SessionId = "checkpoint-fixture",
                CodexWasActive = true,
                CodexSessionId = "01900000-0000-7000-8000-000000000001",
                SshConnectionArguments = ["ubuntu@example.test"]
            };
            var unsettledRecoveryRetained = ShouldPreservePreviousRecovery(false, false, previousRecovery)
                && ShouldPreservePreviousRecovery(true, false, previousRecovery)
                && !ShouldPreservePreviousRecovery(true, true, previousRecovery)
                && !ShouldPreservePreviousRecovery(false, false, null);
            var transitionCopy = previousRecovery.CopyForTransition();
            transitionCopy.SshConnectionArguments[0] = "changed@example.test";
            var transitionRecoveryCopiesArrays = previousRecovery.SshConnectionArguments[0] == "ubuntu@example.test";
            var localTransitionProfile = new SessionProfile
            {
                Id = "local-transition-fixture",
                CommandLine = "powershell.exe",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseLocalTmux = true
            };
            var localTransitionRecovery = new SessionRecoveryEntry
            {
                SessionId = localTransitionProfile.Id,
                WorkingDirectory = localTransitionProfile.WorkingDirectory,
                CodexWasActive = true,
                CodexSessionId = "11111111-2222-3333-4444-555555555555",
                CodexSandboxMode = "danger-full-access",
                CodexApprovalPolicy = "never",
                CodexPermissionProfile = ":danger-full-access",
                CodexApprovalsReviewer = "user"
            };
            _ = TerminalPane.BuildCommandLine(localTransitionProfile, localTransitionRecovery);
            var localTransitionScript = File.ReadAllText(Path.Combine(PowerShellStartupScriptStore.DirectoryPath,
                SessionRecoveryStore.SafeSessionId(localTransitionProfile.Id) + ".ps1"));
            var localTmuxTransitionKeepsExactCodex = localTransitionScript.Contains(
                "codex resume '11111111-2222-3333-4444-555555555555'", StringComparison.Ordinal);
            LocalTmuxSession.DeleteLaunchArtifacts(localTransitionProfile.Id);
            PowerShellStartupScriptStore.Delete(localTransitionProfile.Id);
            var localTmuxPersistence = await LocalTmuxSession.RunPersistenceSmokeAsync();

            var success = inactiveTerminalsStartEagerly && warmupHostContract && failureOffersRetry && manualRetryWorks
                && recoveredSshHandshakeContract && unsettledRecoveryRetained && transitionRecoveryCopiesArrays
                && localTmuxTransitionKeepsExactCodex && localTmuxPersistence.Passed;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath,
                $"{(success ? "PASS" : "FAIL")} Every saved terminal is queued independently of Session selection, and failed recovery exposes a reusable Retry action.\n"
                + $"InactiveTerminalsStartEagerly={inactiveTerminalsStartEagerly}\nWarmupHostContract={warmupHostContract}\n"
                + $"FailureOffersRetry={failureOffersRetry}\nManualRetryWorks={manualRetryWorks}\n"
                + $"RecoveredSshHandshakeContract={recoveredSshHandshakeContract}\n"
                + $"UnsettledRecoveryRetained={unsettledRecoveryRetained}\nTransitionRecoveryCopiesArrays={transitionRecoveryCopiesArrays}\n"
                + $"LocalTmuxTransitionKeepsExactCodex={localTmuxTransitionKeepsExactCodex}\n"
                + $"LocalTmuxAvailable={localTmuxPersistence.Available}\nLocalTmuxPersistence={localTmuxPersistence.Passed}\nLocalTmuxDiagnostic={localTmuxPersistence.Diagnostic}\n"
                + $"QueuedTerminals={results.Length}\nAttempts={string.Join(',', fixtures.Select(profile => panes[profile.Id].StartupAttemptCountForTest))}");
            return success;
        }
        finally
        {
            retryFixture?.Stop();
            foreach (var profile in fixtures)
            {
                if (!panes.Remove(profile.Id, out var pane)) continue;
                TerminalWarmupHost.Children.Remove(pane);
                TerminalHost.Children.Remove(pane);
                pane.Stop();
            }
        }
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
        var composerDraftSurvivesStore = WorkspaceStore.VerifyComposerDraftPersistenceForTest(
            terminalProfile, Path.Combine(Path.GetDirectoryName(reportPath)!, "composer-persistence-store"));
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
        var unsafeModelScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = "gpt'; Write-Output unsafe; #", CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy }));
        var normalDoesNotResumeCodex = !normalScript.Contains("codex resume", StringComparison.OrdinalIgnoreCase);
        var startupCommandIsBounded = TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = false }).Length < 4096;
        var codexResumesExactSession = codexScript.Contains("codex resume '11111111-2222-3333-4444-555555555555'", StringComparison.OrdinalIgnoreCase);
        var codexResumesSavedModel = codexScript.Contains($"--model '{savedModel}'", StringComparison.Ordinal);
        var codexResumesSavedPermissions = codexScript.Contains($"--sandbox '{savedSandboxMode}' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal);
        var profilePermissionResumeStart = profilePermissionScript.LastIndexOf("; & codex resume", StringComparison.OrdinalIgnoreCase);
        var profilePermissionResumeCommand = profilePermissionResumeStart >= 0 ? profilePermissionScript[profilePermissionResumeStart..] : profilePermissionScript;
        var codexResumesSavedPermissionProfile = profilePermissionResumeCommand.Contains($"--sandbox '{savedSandboxMode}' --config 'approvals_reviewer=\"{savedApprovalsReviewer}\"' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal)
            && !profilePermissionResumeCommand.Contains("default_permissions", StringComparison.OrdinalIgnoreCase);
        var unsafeModelRejected = !unsafeModelScript.Contains("codex resume '11111111-2222-3333-4444-555555555555' --model", StringComparison.OrdinalIgnoreCase)
            && !unsafeModelScript.Contains("Write-Output unsafe", StringComparison.Ordinal);
        var unsafePermissionsRejected = false;
        try
        {
            _ = TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexSandboxMode = "danger-full-access'; Write-Output unsafe; #", CodexApprovalPolicy = savedApprovalPolicy });
        }
        catch (InvalidOperationException) { unsafePermissionsRejected = true; }
        var ambiguousCodexUsesPicker = pickerScript.Contains("codex resume --all", StringComparison.OrdinalIgnoreCase) && !pickerScript.Contains("--last", StringComparison.OrdinalIgnoreCase);
        var powershellWrapperInstalled = normalScript.Contains("function global:codex", StringComparison.OrdinalIgnoreCase)
            && normalScript.Contains(profile.Id, StringComparison.Ordinal);
        var sshWrapperInstalled = normalScript.Contains("function global:ssh", StringComparison.OrdinalIgnoreCase)
            && normalScript.Contains("ConnectionArguments", StringComparison.Ordinal);
        var managedSshShellCommand = SshLaunchStore.BuildRemoteInteractiveShellCommand(profile.Id);
        var managedTmuxSessionName = RemoteTmuxSession.GetSessionName(profile.Id);
        var managedSshShellUsesTmux = managedSshShellCommand.Contains($"export POWERSHELLPLUS_PANE_ID='{profile.Id}';", StringComparison.Ordinal)
            && managedSshShellCommand.StartsWith("printf '\\033]9;9;", StringComparison.Ordinal)
            && managedSshShellCommand.Contains("command -v tmux", StringComparison.Ordinal)
            && managedSshShellCommand.Contains("tmux attach-session", StringComparison.Ordinal)
            && managedSshShellCommand.Contains("tmux new-session", StringComparison.Ordinal)
            && managedSshShellCommand.Contains("status off", StringComparison.Ordinal)
            && managedSshShellCommand.Contains(managedTmuxSessionName, StringComparison.Ordinal)
            && managedSshShellCommand.Contains("tmux is not installed", StringComparison.Ordinal);
        var unsafeTmuxName = RemoteTmuxSession.GetSessionName("pane'; touch /tmp/unsafe; #");
        var tmuxNamesAreBoundedAndSafe = RemoteTmuxSession.IsSafeSessionName(managedTmuxSessionName)
            && RemoteTmuxSession.IsSafeSessionName(unsafeTmuxName)
            && unsafeTmuxName.IndexOfAny(['\'', ';', '/', ' ', '#']) < 0
            && RemoteTmuxSession.BuildEnsureDetachedCommand(profile.Id, "exec codex resume safe-session") is { } detachedCommand
            && detachedCommand.Contains("tmux new-session -d", StringComparison.Ordinal)
            && detachedCommand.Contains("status off", StringComparison.Ordinal)
            && detachedCommand.Contains("PSP_TMUX_READY", StringComparison.Ordinal);
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
        var sshHermesPlan = SshRecovery.BuildResumePlan(sshHermesRecovery);
        var sshHermesExactResume = sshHermesPlan?.Arguments.LastOrDefault() is { } sshHermesCommand
            && SshRecovery.TryDecodePowerShellSafeRemoteCommand(sshHermesCommand, out var decodedSshHermesCommand)
            && decodedSshHermesCommand.Contains("${SHELL:-/bin/sh}", StringComparison.Ordinal)
            && decodedSshHermesCommand.Contains(hermesSessionId, StringComparison.Ordinal)
            && decodedSshHermesCommand.Contains(hermesModel, StringComparison.Ordinal)
            && decodedSshHermesCommand.Contains("hermes", StringComparison.Ordinal);
        var managedHermesRecovery = new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = sshHermesRecovery.SshConnectionArguments,
            HermesWasActive = true,
            HermesSessionId = hermesSessionId,
            HermesModel = hermesModel,
            HermesUseTui = true,
            RemoteTmuxManaged = true,
            RemoteTmuxSessionName = managedTmuxSessionName
        };
        var managedHermesPlan = SshRecovery.BuildResumePlan(managedHermesRecovery);
        var persistentAgentResumeUsesTmux = managedHermesPlan?.Arguments.LastOrDefault() is { } managedHermesCommand
            && SshRecovery.TryDecodePowerShellSafeRemoteCommand(managedHermesCommand, out var decodedManagedHermesCommand)
            && decodedManagedHermesCommand.Contains("tmux attach-session", StringComparison.Ordinal)
            && decodedManagedHermesCommand.Contains("tmux new-session", StringComparison.Ordinal)
            && decodedManagedHermesCommand.Contains(managedTmuxSessionName, StringComparison.Ordinal)
            && decodedManagedHermesCommand.Contains(hermesSessionId, StringComparison.Ordinal);
        var sshRecoveryIsBoundedAndVisible = sshHermesScript.Contains("[PowerShellPlus] Restoring SSH and Hermes session", StringComparison.Ordinal)
            && sshHermesScript.Contains("$global:__PowerShellPlusSshRecoveryActive = $true", StringComparison.Ordinal)
            && sshHermesScript.Contains("saved session was kept", StringComparison.Ordinal)
            && sshHermesScript.Contains("PowerShell prompt remains interactive", StringComparison.Ordinal);
        var sshHermesFallbackRecovery = new SessionRecoveryEntry
        {
            SessionId = profile.Id,
            SshWasActive = true,
            SshConnectionArguments = ["deploy@vps.example"],
            HermesWasActive = true,
            HermesModel = changedHermesModel
        };
        var sshHermesFallbackPlan = SshRecovery.BuildResumePlan(sshHermesFallbackRecovery);
        var sshHermesFallbackResume = sshHermesFallbackPlan?.Arguments.LastOrDefault() is { } sshHermesFallbackCommand
            && SshRecovery.TryDecodePowerShellSafeRemoteCommand(sshHermesFallbackCommand, out var decodedSshHermesFallbackCommand)
            && decodedSshHermesFallbackCommand.Contains("${SHELL:-/bin/sh}", StringComparison.Ordinal)
            && decodedSshHermesFallbackCommand.Contains(changedHermesModel, StringComparison.Ordinal)
            && decodedSshHermesFallbackCommand.Contains("--continue", StringComparison.Ordinal);
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
        var currentPermissionFixtureId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        var currentPermissionMetadataTime = fixtureStarted.AddDays(-1);
        var currentPermissionMetadata = new { timestamp = currentPermissionMetadataTime.ToString("O"), type = "session_meta", payload = new { session_id = currentPermissionFixtureId, timestamp = currentPermissionMetadataTime.ToString("O"), cwd = actualCodexDirectory, source = "cli" } };
        var currentPermissionTurn = new
        {
            timestamp = fixtureStarted.AddSeconds(6).ToString("O"),
            type = "turn_context",
            payload = new
            {
                model = savedModel,
                approval_policy = "on-request",
                approvals_reviewer = savedApprovalsReviewer,
                sandbox_policy = new { type = "workspace-write" },
                permission_profile = new { type = "managed", file_system = new { type = "restricted" }, network = "restricted" }
            }
        };
        File.WriteAllLines(Path.Combine(fixtureRoot, "rollout-current-permissions.jsonl"),
            [System.Text.Json.JsonSerializer.Serialize(currentPermissionMetadata), System.Text.Json.JsonSerializer.Serialize(currentPermissionTurn)]);
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
        var currentTurnContextPermissions = CodexSessionLocator.FindLatestPermissions(currentPermissionFixtureId, fixtureRoot);
        var currentTurnContextPermissionsMapped = currentTurnContextPermissions?.PermissionProfile == "managed"
            && currentTurnContextPermissions.SandboxMode == "workspace-write"
            && currentTurnContextPermissions.ApprovalPolicy == "on-request"
            && currentTurnContextPermissions.ApprovalsReviewer == savedApprovalsReviewer;
        var changedDirectoryRestored = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = fixtureId, WorkingDirectory = actualCodexDirectory, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy }))
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
        var activeThreadIdsRemainProcessBound = CodexActivityStore.FindActiveThreadIds(fixtureProcessId, fixtureStarted, logsFixturePath)
            .SequenceEqual(new[] { subagentThreadId, resumedThreadId, launcherThreadId });
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
            && sshWrapperScript.Contains("PersistentSessionRequested", StringComparison.Ordinal)
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
                && runtimeMarker.RecoveryAttempt && runtimeMarker.PersistentSessionRequested && runtimeMarker.ExitCode is not null
                && runtimeMarker.ConnectionArguments.SequenceEqual(["deploy@vps.example"]);
            var unsafeWrapperExited = RunSshWrapper("unsafe-runtime-pane", "ssh 'ssh://deploy:password@vps.example'");
            var unsafeMarkerPath = Path.Combine(runtimeMarkers, SessionRecoveryStore.SafeSessionId("unsafe-runtime-pane") + ".json");
            sshWrapperExecutesSafely = validWrapperMarker && unsafeWrapperExited && !File.Exists(unsafeMarkerPath);
        }
        catch { sshWrapperExecutesSafely = false; }
        finally { try { Directory.Delete(sshWrapperRuntimeRoot, true); } catch { } }
        var sshBannerTimeoutFallsBackInteractive = false;
        var sshBannerDiagnostic = "not run";
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
            var timeoutScriptPath = Path.Combine(timeoutFixtureRoot, "timeout-recovery.ps1");
            File.WriteAllText(timeoutScriptPath, timeoutScript, new System.Text.UTF8Encoding(false));
            var timeoutStartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            timeoutStartInfo.ArgumentList.Add("-NoLogo");
            timeoutStartInfo.ArgumentList.Add("-NoProfile");
            timeoutStartInfo.ArgumentList.Add("-File");
            timeoutStartInfo.ArgumentList.Add(timeoutScriptPath);
            using var timeoutProcess = Process.Start(timeoutStartInfo);
            if (timeoutProcess is not null)
            {
                var outputTask = timeoutProcess.StandardOutput.ReadToEndAsync();
                var errorTask = timeoutProcess.StandardError.ReadToEndAsync();
                stalledSshClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
                await timeoutProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var timeoutOutput = await outputTask + await errorTask;
                var timeoutMarker = SshLaunchStore.Load(profile.Id, timeoutFixtureRoot);
                var restoreNotice = timeoutOutput.Contains("[PowerShellPlus] Restoring SSH session", StringComparison.Ordinal);
                var fallbackWarning = timeoutOutput.Contains("Automatic recovery could not connect", StringComparison.Ordinal);
                var interactiveFallback = timeoutOutput.Contains("PSPLUS_SSH_RECOVERY_FALLBACK_OK", StringComparison.Ordinal);
                var failedMarker = timeoutMarker?.IsFailedRecovery == true;
                sshBannerTimeoutFallsBackInteractive = timeoutProcess.ExitCode == 0
                    && restoreNotice && fallbackWarning && interactiveFallback && failedMarker;
                sshBannerDiagnostic = $"exit={timeoutProcess.ExitCode}; restore={restoreNotice}; warning={fallbackWarning}; interactive={interactiveFallback}; marker={failedMarker}";
            }
        }
        catch (Exception exception)
        {
            sshBannerTimeoutFallsBackInteractive = false;
            sshBannerDiagnostic = exception.GetType().Name + ": " + exception.Message;
        }
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
        var activeSshLaunchSurvivesTransientProcessMiss = SshRecovery.ActiveLaunchSurvivesTransientProcessMissForTest();
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
            RemoteCodexApprovalsReviewer = savedApprovalsReviewer,
            RemoteTmuxManaged = true, RemoteTmuxSessionName = RemoteTmuxSession.GetSessionName("test-session"),
            LocalTmuxManaged = true, LocalTmuxSessionName = LocalTmuxSession.GetSessionName("test-session"),
            LocalTmuxDistribution = "Ubuntu"
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
            && reloadedEntry.RemoteTmuxManaged && reloadedEntry.RemoteTmuxSessionName == RemoteTmuxSession.GetSessionName("test-session")
            && reloadedEntry.LocalTmuxManaged && reloadedEntry.LocalTmuxSessionName == LocalTmuxSession.GetSessionName("test-session")
            && reloadedEntry.LocalTmuxDistribution == "Ubuntu"
            && SessionRecoveryStore.ReadTranscript(reloadedEntry, recoveryRoot) == "previous terminal output";
        try { Directory.Delete(recoveryRoot, true); } catch { }
        var legacyRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "legacy-recovery-fixture");
        var legacyFixture = new SessionRecoverySnapshot { Version = 1 };
        legacyFixture.Sessions["legacy-session"] = new SessionRecoveryEntry { SessionId = "legacy-session", CodexWasActive = true, CodexSessionId = "99999999-8888-7777-6666-555555555555", WorkingDirectory = profile.WorkingDirectory };
        SessionRecoveryStore.Save(legacyFixture, legacyRoot);
        var migratedLegacy = SessionRecoveryStore.Load(legacyRoot);
        var unsafeLegacyIdDiscarded = migratedLegacy.Version == 11 && migratedLegacy.Sessions["legacy-session"].CodexSessionId is null;
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
        var importResumeCommandIsExact = importedResumeCommand.Contains($"codex resume '{fixtureId}' --model '{savedModel}' --sandbox '{savedSandboxMode}' --config 'approvals_reviewer=\"{savedApprovalsReviewer}\"' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal)
            && !importedResumeCommand.Contains("default_permissions", StringComparison.OrdinalIgnoreCase);
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
        var remoteImportedPlan = SshRecovery.BuildResumePlan(remoteImportedRecovery);
        var importRestoresSshAndRemoteCodex = remoteImportedPlan?.Arguments.Contains("ubuntu@15.204.82.129", StringComparer.Ordinal) == true
            && remoteImportedPlan.Arguments.LastOrDefault() is { } remoteImportedCommand
            && SshRecovery.TryDecodePowerShellSafeRemoteCommand(remoteImportedCommand, out var decodedRemoteImportedCommand)
            && decodedRemoteImportedCommand.Contains(remoteCodexId, StringComparison.Ordinal)
            && decodedRemoteImportedCommand.Contains(savedModel, StringComparison.Ordinal);
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
        var shortFileName = RemoteClipboardFileBridge.CreateRemoteFileName(
            new DateTime(2026, 7, 24, 9, 51, 29, DateTimeKind.Utc), Guid.Parse("1a22347c-8b6b-43c7-b00a-f880775cd32a"), ".PNG");
        var fileBridgeArguments = RemoteClipboardFileBridge.BuildSshArgumentsForTest(importedSshCapture.ConnectionArguments, shortFileName);
        var fileBridgeIsBoundedAndSafe = fileBridgeArguments.Contains("BatchMode=yes", StringComparer.Ordinal)
            && fileBridgeArguments.Contains("ubuntu@15.204.82.129", StringComparer.Ordinal)
            && fileBridgeArguments[^1].Contains("umask 077", StringComparison.Ordinal)
            && fileBridgeArguments[^1].Contains("$HOME/.cache/powershellplus/files", StringComparison.Ordinal)
            && fileBridgeArguments[^1].Contains("set -C", StringComparison.Ordinal)
            && shortFileName == "file-095129-1a22347c.png" && shortFileName.Length == 24
            && RemoteClipboardFileBridge.TryReadRemotePath("PSP_REMOTE_FILE:/home/ubuntu/.cache/powershellplus/files/file-test.png", out _)
            && !RemoteClipboardFileBridge.TryReadRemotePath("PSP_REMOTE_FILE:/home/ubuntu/../etc/shadow", out _);

        HideToTray();
        await Task.Delay(300);
        var hidden = !IsVisible;
        var rootWhileHidden = pane.GetRootProcessId();
        RestoreWindow(false);
        await Task.Delay(300);
        var restored = IsVisible;
        var rootAfter = pane.GetRootProcessId();
        var sameLiveProcess = rootBefore is not null && rootBefore == rootWhileHidden && rootBefore == rootAfter;
        var terminalHoverDetailsWork = TerminalHoverDetailsBuilder.WorksForTest();
        var success = workspaceTestIsolated && composerDraftSurvivesStore && hidden && restored && sameLiveProcess && normalDoesNotResumeCodex && startupCommandIsBounded && codexResumesExactSession && codexResumesSavedModel && codexResumesSavedPermissions && codexResumesSavedPermissionProfile && unsafeModelRejected && unsafePermissionsRejected && ambiguousCodexUsesPicker && powershellWrapperInstalled
            && sshWrapperInstalled && managedSshShellUsesTmux && tmuxNamesAreBoundedAndSafe && persistentAgentResumeUsesTmux && safeSshAccepted && quotedHomeIdentityAccepted && safeSshReliabilityOptionsAccepted && unsafeSshRejected && hermesExactSessionDetected && hermesModelChangeDetected && unsafeHermesModelRejected && exitedHermesNotRestored && sshHermesExactResume && sshRecoveryIsBoundedAndVisible && sshHermesFallbackResume && unsafeHermesModelNotInjected && remoteProbeParsed && remoteCodexExactResume && unsafeRemoteProbeRejected && sshLoginOnlyRestored && unsafeSshResumeRejected
            && codexSessionMapped && latestModelMapped && latestPermissionsMapped && currentTurnContextPermissionsMapped && partialRolloutIgnored && changedDirectoryRestored && inTuiResumeRebound && activeThreadIdsRemainProcessBound && liveRolloutSharedRead && launchTimeFallbackRebound && exactLaunchBindingPersisted && normalCodexExitRecorded && wrapperRecordsPaneAndLifecycle
            && sshLaunchBindingPersisted && normalSshExitRecorded && sshWrapperRecordsSafeConnectionOnly && sshWrapperExecutesSafely && sshBannerTimeoutFallsBackInteractive && failedRecoveryStateRetained && activeSshLaunchSurvivesTransientProcessMiss && recoveryRoundTrip && unsafeLegacyIdDiscarded && importPreservesStableTabNames && importExtractsWorkingDirectories && importAutoMatchesExactCodexThread && importCarriesExactCodexPermissions && importResumeCommandIsExact && descendantDirectoryMatchesSessionRoot && ambiguousImportRequiresChoice
            && importCapturesSshAndRemoteCodex && importRestoresSshAndRemoteCodex && importParsesQuotedSshIdentity && imageBridgeIsBoundedAndSafe && fileBridgeIsBoundedAndSafe && terminalHoverDetailsWork;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Live panes survived hide/restore; recovery resumed local and remote Codex, SSH, and Hermes with validated durable state.\nWorkspaceTestIsolated={workspaceTestIsolated}\nHidden={hidden}\nRestored={restored}\nSameLiveProcess={sameLiveProcess}\nNormalDoesNotResumeCodex={normalDoesNotResumeCodex}\nCodexResumesExactSession={codexResumesExactSession}\nCodexResumesSavedModel={codexResumesSavedModel}\nCodexResumesSavedPermissions={codexResumesSavedPermissions}\nCodexResumesSavedPermissionProfile={codexResumesSavedPermissionProfile}\nUnsafeModelRejected={unsafeModelRejected}\nUnsafePermissionsRejected={unsafePermissionsRejected}\nAmbiguousCodexUsesPicker={ambiguousCodexUsesPicker}\nPowerShellWrapperInstalled={powershellWrapperInstalled}\nSshWrapperInstalled={sshWrapperInstalled}\nSafeSshAccepted={safeSshAccepted}\nQuotedHomeIdentityAccepted={quotedHomeIdentityAccepted}\nSafeSshReliabilityOptionsAccepted={safeSshReliabilityOptionsAccepted}\nUnsafeSshRejected={unsafeSshRejected}\nHermesExactSessionDetected={hermesExactSessionDetected}\nHermesModelChangeDetected={hermesModelChangeDetected}\nUnsafeHermesModelRejected={unsafeHermesModelRejected}\nExitedHermesNotRestored={exitedHermesNotRestored}\nSshHermesExactResume={sshHermesExactResume}\nSshRecoveryIsBoundedAndVisible={sshRecoveryIsBoundedAndVisible}\nSshHermesFallbackResume={sshHermesFallbackResume}\nUnsafeHermesModelNotInjected={unsafeHermesModelNotInjected}\nRemoteProbeParsed={remoteProbeParsed}\nRemoteCodexExactResume={remoteCodexExactResume}\nUnsafeRemoteProbeRejected={unsafeRemoteProbeRejected}\nSshLoginOnlyRestored={sshLoginOnlyRestored}\nUnsafeSshResumeRejected={unsafeSshResumeRejected}\nCodexSessionMappedAcrossChangedDirectory={codexSessionMapped}\nLatestModelMapped={latestModelMapped}\nLatestPermissionsMapped={latestPermissionsMapped}\nCurrentTurnContextPermissionsMapped={currentTurnContextPermissionsMapped}\nPartialRolloutIgnored={partialRolloutIgnored}\nChangedDirectoryRestored={changedDirectoryRestored}\nInTuiResumeRebound={inTuiResumeRebound}\nActiveThreadIdsRemainProcessBound={activeThreadIdsRemainProcessBound}\nLiveRolloutSharedRead={liveRolloutSharedRead}\nLaunchTimeFallbackRebound={launchTimeFallbackRebound}\nExactLaunchBindingPersisted={exactLaunchBindingPersisted}\nNormalCodexExitRecorded={normalCodexExitRecorded}\nWrapperRecordsPaneAndLifecycle={wrapperRecordsPaneAndLifecycle}\nSshLaunchBindingPersisted={sshLaunchBindingPersisted}\nNormalSshExitRecorded={normalSshExitRecorded}\nSshWrapperRecordsSafeConnectionOnly={sshWrapperRecordsSafeConnectionOnly}\nSshWrapperExecutesSafely={sshWrapperExecutesSafely}\nSshWrapperDiagnostic={sshWrapperDiagnostic}\nSshBannerTimeoutFallsBackInteractive={sshBannerTimeoutFallsBackInteractive}\nSshBannerDiagnostic={sshBannerDiagnostic}\nFailedRecoveryStateRetained={failedRecoveryStateRetained}\nRecoveryRoundTrip={recoveryRoundTrip}\nUnsafeLegacyIdDiscarded={unsafeLegacyIdDiscarded}\nImportPreservesStableTabNames={importPreservesStableTabNames}\nImportExtractsWorkingDirectories={importExtractsWorkingDirectories}\nImportAutoMatchesExactCodexThread={importAutoMatchesExactCodexThread}\nImportCarriesExactCodexPermissions={importCarriesExactCodexPermissions}\nImportResumeCommandIsExact={importResumeCommandIsExact}\nDescendantDirectoryMatchesSessionRoot={descendantDirectoryMatchesSessionRoot}\nAmbiguousImportRequiresChoice={ambiguousImportRequiresChoice}\nImportCapturesSshAndRemoteCodex={importCapturesSshAndRemoteCodex}\nImportRestoresSshAndRemoteCodex={importRestoresSshAndRemoteCodex}\nImportParsesQuotedSshIdentity={importParsesQuotedSshIdentity}\nImageBridgeIsBoundedAndSafe={imageBridgeIsBoundedAndSafe}\nFileBridgeIsBoundedAndSafe={fileBridgeIsBoundedAndSafe}");
        File.AppendAllText(reportPath, $"\nComposerDraftSurvivesStore={composerDraftSurvivesStore}\nTerminalHoverDetailsWork={terminalHoverDetailsWork}\nStartupCommandIsBounded={startupCommandIsBounded}\nManagedSshShellUsesTmux={managedSshShellUsesTmux}\nTmuxNamesAreBoundedAndSafe={tmuxNamesAreBoundedAndSafe}\nPersistentAgentResumeUsesTmux={persistentAgentResumeUsesTmux}\nActiveSshLaunchSurvivesTransientProcessMiss={activeSshLaunchSurvivesTransientProcessMiss}");
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
        AutomationRule? terminalAutomationFixture = null;
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
            // These panes have not been placed in TerminalHost yet. The startup
            // coordinator must still visit all of them; this is the regression
            // that previously made inactive Sessions wait for a click.
            foreach (var profile in added)
                panes[profile.Id].SetStartupAttemptOverrideForTest(_ => Task.FromResult(true));
            await WaitForTerminalStartupAsync(null, TimeSpan.FromSeconds(3));
            var inactiveTerminalsStartEagerly = added.All(profile =>
                !panes[profile.Id].IsLoaded && panes[profile.Id].StartupAttemptCountForTest == 1);
            foreach (var profile in added) panes[profile.Id].SetStartupAttemptOverrideForTest(null);

            var retryFixture = new TerminalPane(new SessionProfile
            {
                Name = "Startup retry fixture",
                CommandLine = terminalProfile.CommandLine,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }, EffectiveAppearance());
            retryFixture.SetStartupAttemptOverrideForTest(_ => Task.FromResult(false));
            var initialRetryFixtureResult = await retryFixture.EnsureTerminalStartupAsync();
            var startupFailureOffersRetry = !initialRetryFixtureResult
                && retryFixture.StartupAttemptCountForTest == 3
                && retryFixture.StartupFailureVisibleForTest
                && retryFixture.StartupRetryButtonReadyForTest;
            retryFixture.SetStartupAttemptOverrideForTest(_ => Task.FromResult(true));
            var startupManualRetryWorks = await retryFixture.RetryTerminalStartupForTestAsync()
                && !retryFixture.StartupFailureVisibleForTest;
            retryFixture.Stop();
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
            var layoutButtons = LayoutButtons().Select(value => value.Button).ToArray();
            var layoutControlsInSidebar = WorkspaceSidebar.IsAncestorOf(LayoutControls) && LayoutControls.ActualWidth >= 200
                && layoutButtons.All(button => button.ActualWidth >= 40);
            var layoutHoverPreviewsReady = layoutButtons.All(button => button.ToolTip is ToolTip { Content: Border });
            var layoutPreviewGeometryWorks = LayoutPreviewGeometryWorksForTest();
            var layoutTransitionContractReady = LayoutTransitionOverlay.IsHitTestVisible == false
                && LayoutTransitionCanvas.Width == 240 && LayoutTransitionCanvas.Height == 138;
            ShowSection(SettingsPanel);
            await Dispatcher.Yield(DispatcherPriority.Render);
            root.UpdateLayout();
            ApplyThemedSettingsScrollbar();
            var settingsScrollBar = FindVisualDescendant<ScrollBar>(SettingsScroller);
            var settingsScrollbarThemed = settingsScrollBar is not null
                && ReferenceEquals(settingsScrollBar.Style, FindResource("ThemedScrollBar"));
            var updateUiContractReady = UpdateUiContractForTest;
            var originalTheme = state.Settings.ApplicationTheme;
            var previewTheme = AppThemeCatalog.Themes.First(theme => !string.Equals(theme.Id, originalTheme, StringComparison.OrdinalIgnoreCase));
            SettingsThemeList.SelectedItem = previewTheme;
            await Dispatcher.Yield(DispatcherPriority.Render);
            var themeCatalogContract = AppThemeCatalog.ContractPassesForTest();
            var themePickerReady = SettingsThemeList.Items.Count == AppThemeCatalog.Themes.Count
                && AppThemeCatalog.Themes.Any(theme => theme.Id == AppThemeCatalog.BlackThemeId);
            var liveThemeSwitchWorks = state.Settings.ApplicationTheme == previewTheme.Id
                && Background is SolidColorBrush liveThemeBrush
                && previewTheme.PreviewBackground is SolidColorBrush expectedThemeBrush
                && liveThemeBrush.Color == expectedThemeBrush.Color;
            var themeSelectionPersists = WorkspaceStore.CreateSnapshot(state).Settings.ApplicationTheme == previewTheme.Id;
            var themeStartupPersistenceWorks = WorkspaceStore.VerifyApplicationThemePersistenceForTest(terminalProfile,
                Path.Combine(Path.GetDirectoryName(reportPath)!, "theme-persistence"));
            var gradientTheme = AppThemeCatalog.BuiltInThemes.First(theme => theme.IsGradient);
            SettingsThemeList.SelectedItem = gradientTheme;
            await Dispatcher.Yield(DispatcherPriority.Render);
            var gradientThemeReady = state.Settings.ApplicationTheme == gradientTheme.Id
                && FindResource("AppBackdrop") is LinearGradientBrush
                && gradientTheme.PreviewBackground is LinearGradientBrush;
            NewCustomThemeClick(this, new RoutedEventArgs());
            if (customThemeDraft is not null)
            {
                customThemeDraft.UseGradient = true;
                customThemeDraft.GradientDirection = "Horizontal";
                UpdateCustomThemeEditorVisuals();
            }
            root.UpdateLayout();
            ThemeBackgroundColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.Render);
            var settingsThemeColorPickerOpens = AccentColorPickerOverlay.Visibility == Visibility.Visible
                && AccentColorPickerOverlay.IsVisible;
            SetCustomThemeColor(AccentColorPickerTarget.ThemeBackground, "#123456");
            var customThemeEditorReady = CustomThemeEditorPanel.Visibility == Visibility.Visible
                && ThemeGradientOptions.Visibility == Visibility.Visible
                && FindResource("AppBackdrop") is LinearGradientBrush
                && ThemeBackgroundHex.Text.StartsWith('#')
                && ThemeGradientEndHex.Text.StartsWith('#');
            var customThemeLivePreview = FindResource("Bg") is SolidColorBrush customThemeBackground
                && customThemeBackground.Color == (Color)ColorConverter.ConvertFromString("#123456")!;
            CloseAccentColorPicker(true);
            CancelCustomThemeClick(this, new RoutedEventArgs());
            var customThemeCancelRestores = FindResource("Bg") is SolidColorBrush restoredCustomThemeBackground
                && restoredCustomThemeBackground.Color == (Color)ColorConverter.ConvertFromString(gradientTheme.Background)!;
            SettingsThemeList.SelectedItem = AppThemeCatalog.Resolve(originalTheme);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var themeRestored = state.Settings.ApplicationTheme == AppThemeCatalog.Normalize(originalTheme);
            ShowSection(SessionsPanel);
            WorkspaceSessionList.UpdateLayout();
            SessionList.UpdateLayout();
            var workspaceCardContainer = WorkspaceSessionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var terminalCardContainer = SessionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var workspaceCardSurface = workspaceCardContainer is null ? null : FindVisualDescendant<Border>(workspaceCardContainer);
            var terminalCardSurface = terminalCardContainer is null ? null : FindVisualDescendant<Border>(terminalCardContainer);
            var sidebarCardsUseSingleFrame = workspaceCardContainer is not null && terminalCardContainer is not null
                && VisualTreeHelper.GetChildrenCount(workspaceCardContainer) == 1 && VisualTreeHelper.GetChild(workspaceCardContainer, 0) is ContentPresenter
                && VisualTreeHelper.GetChildrenCount(terminalCardContainer) == 1 && VisualTreeHelper.GetChild(terminalCardContainer, 0) is ContentPresenter;
            var accentCardStyle = FindResource("AccentCardSurface") as Style;
            var sidebarCardHoverStylesReady = accentCardStyle?.Triggers.OfType<DataTrigger>().Count() >= 2;
            var selectedWorkspaceContainer = WorkspaceSessionList.ItemContainerGenerator.ContainerFromItem(activeWorkspaceSession) as ListBoxItem;
            var selectedTerminalContainer = SessionList.ItemContainerGenerator.ContainerFromItem(activePane?.Profile) as ListBoxItem;
            var selectedWorkspaceSurface = selectedWorkspaceContainer is null ? null : FindVisualDescendant<Border>(selectedWorkspaceContainer);
            var selectedTerminalSurface = selectedTerminalContainer is null ? null : FindVisualDescendant<Border>(selectedTerminalContainer);
            var sidebarCardSelectionVisible = selectedWorkspaceContainer?.IsSelected == true && selectedTerminalContainer?.IsSelected == true
                && selectedWorkspaceSurface?.Background is SolidColorBrush workspaceSelectedBrush
                && activeWorkspaceSession?.AccentSelectedBrush is SolidColorBrush expectedWorkspaceSelectedBrush
                && workspaceSelectedBrush.Color == expectedWorkspaceSelectedBrush.Color
                && selectedTerminalSurface?.Background is SolidColorBrush terminalSelectedBrush
                && activePane?.Profile.AccentSelectedBrush is SolidColorBrush expectedTerminalSelectedBrush
                && terminalSelectedBrush.Color == expectedTerminalSelectedBrush.Color;
            var workspaceCardMenuReliable = workspaceCardSurface?.ContextMenu is { } workspaceCardMenu
                && workspaceCardMenu.Items.OfType<MenuItem>().Any(value => string.Equals(value.Header?.ToString(), "Edit session", StringComparison.Ordinal))
                && OpenCardContextMenu(workspaceCardSurface, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
            if (workspaceCardSurface?.ContextMenu is { } openedWorkspaceCardMenu) openedWorkspaceCardMenu.IsOpen = false;
            var terminalCardMenuReliable = terminalCardSurface?.ContextMenu is { } terminalCardMenu
                && terminalCardMenu.Items.OfType<MenuItem>().Any(value => string.Equals(value.Header?.ToString(), "Edit terminal", StringComparison.Ordinal))
                && OpenCardContextMenu(terminalCardSurface, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
            if (terminalCardSurface?.ContextMenu is { } openedTerminalCardMenu) openedTerminalCardMenu.IsOpen = false;
            Width = Math.Max(MinWidth, ActualWidth - 260);
            await Dispatcher.Yield(DispatcherPriority.Background);
            root.UpdateLayout();
            layoutControlsInSidebar = layoutControlsInSidebar && LayoutControls.ActualWidth >= 200
                && layoutButtons.All(button => button.ActualWidth >= 40);
            Width = originalWindowWidth;
            await Dispatcher.Yield(DispatcherPriority.Background);
            var terminalScrollbarsThemed = panes.Values.All(pane => pane.IsNativeScrollbarThemed());
            var terminalScrollbarsInteractive = panes.Values.All(pane => pane.NativeScrollbarInteractiveForTest);
            var terminalScrollbarBridgesStable = panes.Values.All(pane => pane.TerminalScrollbarBridgeStableForTest);
            var tmuxScrollbackBridgeContract = RemoteTmuxScrollback.ContractPassesForTest();
            var localTmuxScrollbarRoutingContract = TerminalPane.TmuxScrollbarRoutingContractPassesForTest();
            var persistentTmuxScrollChannelContract = PersistentSshCommandChannel.ContractPassesForTest();
            var trayLifecycleContract = TrayLifecycleContractPassesForTest() && App.RestartArgumentContractPassesForTest();
            var startupLoadingWindow = new StartupWindow { ShowActivated = false, ShowInTaskbar = false };
            startupLoadingWindow.Report(new StartupProgress("Starting terminals", "Smoke terminal", 1, 2));
            var startupLoadingScreenReady = startupLoadingWindow.ContractIsValidForTest;
            startupLoadingWindow.Close();
            var activationTarget = panes[added[0].Id];
            SelectPane(panes.Values.First().Profile.Id, false);
            var paneCommandInputTakesFocus = activationTarget.FocusCommandInputForTest();
            var handoffButtonReady = activationTarget.HandoffButtonReadyForTest;
            var terminalSurfaceHooked = activationTarget.HasTerminalSurfaceActivationHook;
            var terminalInputRouterPrecedesConPty = activationTarget.TerminalInputRouterPrecedesConPtyForTest();
            var remoteImagePasteIndicatorReady = activationTarget.HasRemoteImagePasteIndicatorForTest;
            var remoteImageShortcutInterceptReady = TerminalPane.RemoteImageShortcutsClassifiedForTest();
            var remoteImagePasteModesWork = TerminalPane.RemoteImagePasteModesFormatForTest();
            var remoteSshPasteConsumesAllClipboardKinds = TerminalPane.RemoteSshPasteRoutingConsumesAllClipboardKindsForTest();
            var threadMessagePasteInterceptsBeforeConPty = activationTarget.ExerciseThreadMessagePasteInterceptionForTest();
            var terminalTabQueuesInsideConPty = activationTarget.ExerciseThreadMessageTabInterceptionForTest();
            var tmuxControlKeysReachConPty = TerminalPane.TmuxControlCharactersClassifiedForTest()
                && activationTarget.ExerciseTerminalHookTmuxControlInterceptionForTest();
            var tmuxOwnsCursorSequences = TerminalPane.TmuxCursorOwnershipContractPassesForTest();
            var remoteImagePasteIndicatorStatesWork = activationTarget.ExerciseRemoteImagePasteIndicatorForTest();
            var attachmentFixture = Path.Combine(Path.GetDirectoryName(reportPath)!, "composer-preview-fixture.png");
            File.WriteAllBytes(attachmentFixture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            activationTarget.SetCommandInputForTest("inspect ");
            var plainTextPathPromoted = activationTarget.PastePlainTextAttachmentForTest($"please inspect {attachmentFixture}", attachmentFixture);
            var composerAttachmentAdded = plainTextPathPromoted
                && activationTarget.ComposerAttachmentCountForTest == 1 && activationTarget.AttachmentStripVisibleForTest
                && activationTarget.CommandInputTextForTest.Contains(attachmentFixture, StringComparison.OrdinalIgnoreCase);
            var composerTypingAvoidsPillRebuild = activationTarget.ComposerTypingAvoidsPillRebuildForTest();
            var composerImagePreviewOpens = activationTarget.OpenFirstAttachmentPreviewForTest();
            var composerDraftTracksAttachments = activationTarget.ComposerDraftPersistedForTest;
            var attachmentPreviewKindsWork = TerminalPane.AttachmentPreviewKindsForTest();
            var attachmentFixtureTwo = Path.Combine(Path.GetDirectoryName(reportPath)!, "composer-preview-fixture-2.png");
            File.Copy(attachmentFixture, attachmentFixtureTwo, true);
            var secondComposerAttachmentAdded = activationTarget.AddComposerAttachmentForTest(attachmentFixtureTwo, true)
                && activationTarget.ComposerAttachmentCountForTest == 2;
            composerTypingAvoidsPillRebuild = composerTypingAvoidsPillRebuild
                && activationTarget.ComposerTypingAvoidsPillRebuildForTest();
            var composerTokensMatchCanonicalPaths = activationTarget.ComposerTokensMatchCanonicalPathsForTest;
            var composerBlankSpacePreservesTokens = activationTarget.ComposerBlankSpacePreservesTokensForTest;
            var attachmentPillReorderUpdatesCommand = activationTarget.ReorderFirstTwoAttachmentsForTest();
            var composerScrollbarThemed = activationTarget.ComposerScrollbarThemedForTest;
            var perTerminalFontZoomPersists = activationTarget.PerTerminalFontZoomPersistsForTest();
            var droppedAttachmentFixture = Path.Combine(Path.GetDirectoryName(reportPath)!, "composer-drop-fixture.md");
            File.WriteAllText(droppedAttachmentFixture, "# Dropped attachment fixture");
            var composerFileDropAddsAttachment = activationTarget.DropComposerFileForTest(droppedAttachmentFixture);
            var composerFileDropIndicatorsWork = activationTarget.ComposerFileDropIndicatorsWorkForTest();
            var replacementAttachmentFixture = Path.Combine(Path.GetDirectoryName(reportPath)!, "composer-replacement-fixture.zip");
            File.WriteAllBytes(replacementAttachmentFixture, [0x50, 0x53, 0x50, 0x4C, 0x55, 0x53]);
            var attachmentPillDropReplacesFile = activationTarget.ReplaceFirstAttachmentFromFileDropForTest(replacementAttachmentFixture);
            var profileStartupWatchdogWorks = TerminalPane.ProfileStartupWatchdogWorksForTest();
            var composerSshPathsRewrite = TerminalPane.RewriteAttachmentPathsForTest(
                $"inspect {attachmentFixture}", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [attachmentFixture] = "/home/ubuntu/.cache/powershellplus/files/file-test.png"
                }) == "inspect /home/ubuntu/.cache/powershellplus/files/file-test.png";
            activationTarget.ClearComposerAttachmentsForTest();
            activationTarget.SetCommandInputForTest("inspect ");
            _ = activationTarget.AddComposerAttachmentForTest(attachmentFixture, true);
            var removingPathRemovesPill = activationTarget.RemoveFirstAttachmentPathForTest();
            activationTarget.ClearComposerAttachmentsForTest();
            activationTarget.SetCommandInputForTest(string.Empty);
            try { File.Delete(attachmentFixture); } catch { }
            try { File.Delete(attachmentFixtureTwo); } catch { }
            try { File.Delete(droppedAttachmentFixture); } catch { }
            try { File.Delete(replacementAttachmentFixture); } catch { }
            var terminalClickSent = activationTarget.SimulateTerminalSurfaceClickForTest();
            var terminalSurfaceActivatesPane = terminalClickSent && ReferenceEquals(activePane, activationTarget)
                && ReferenceEquals(SessionList.SelectedItem, activationTarget.Profile);
            var terminalSurfaceTakesKeyboardFocus = activationTarget.HasNativeKeyboardFocus();
            activationTarget.SetCommandInputForTest(new string('W', 900));
            activationTarget.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            activationTarget.UpdateLayout();
            var commandInputAutoGrows = activationTarget.CommandInputAutoGrowsForTest && activationTarget.CommandInputHeightForTest > 30
                && activationTarget.CommandInputRespectsLineCapForTest;
            var composerChromeStaysCompact = activationTarget.ComposerChromeStaysCompactForTest;
            activationTarget.SetCommandInputForTest(string.Empty);
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Working);
            var terminalTabShowsWorkingAgent = activationTarget.Profile.AgentStatusState == "working"
                && activationTarget.Profile.AgentStatusText == "Codex is working";
            var agentWorkingStateVisible = activationTarget.AgentActivityStateForTest == AgentActivityState.Working
                && activationTarget.AgentStatusTextForTest == "  Working"
                && activationTarget.AgentStatusColorForTest == Color.FromRgb(137, 180, 250)
                && activationTarget.AgentWorkingAnimationForTest;
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Waiting);
            var terminalTabShowsWaitingAgent = activationTarget.Profile.AgentStatusState == "waiting"
                && activationTarget.Profile.AgentStatusText == "Codex is waiting for your response";
            var agentWaitingStateVisible = activationTarget.AgentActivityStateForTest == AgentActivityState.Waiting
                && activationTarget.AgentStatusTextForTest == "  Waiting for Response"
                && activationTarget.AgentStatusColorForTest == Color.FromRgb(249, 226, 175)
                && activationTarget.AgentWaitingAttentionForTest;
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Idle);
            var terminalTabShowsIdleAgent = activationTarget.Profile.AgentStatusState == "idle"
                && activationTarget.Profile.AgentStatusText == "Codex is idle";
            var agentIdleStateVisible = activationTarget.AgentActivityStateForTest == AgentActivityState.Idle
                && activationTarget.AgentStatusTextForTest == "  Idle"
                && activationTarget.AgentStatusColorForTest == Color.FromRgb(166, 227, 161)
                && !activationTarget.AgentWorkingAnimationForTest
                && !activationTarget.AgentWaitingAttentionForTest;
            activationTarget.SetAgentStatusForTest(AgentKind.Terminal, AgentActivityState.Idle);
            var plainPowerShellHeaderVisible = activationTarget.AgentStatusTextForTest == "  Windows PowerShell";
            activationTarget.SetAgentStatusForTest(AgentKind.Codex, AgentActivityState.Idle);
            var terminalTabAgentStateMirrorsPane = terminalTabShowsWorkingAgent && terminalTabShowsWaitingAgent && terminalTabShowsIdleAgent;
            var inputEchoDoesNotActivateAgent = TerminalPane.ActivityTrackerRejectsInputEchoForTest();
            var codexTurnEventsDriveAgent = CodexSessionLocator.ActivityRecordsClassifyForTest();
            var codexActivityGrowthScanBounded = CodexSessionLocator.ActivityGrowthScanIsBoundedForTest();
            var shiftClickQuickCreatesTerminal = ShouldQuickCreateTerminal(ModifierKeys.Shift)
                && ShouldQuickCreateTerminal(ModifierKeys.Shift | ModifierKeys.Control)
                && !ShouldQuickCreateTerminal(ModifierKeys.None);
            var originalAutomaticTerminalColors = state.Settings.AutomaticallySetTerminalColor;
            state.Settings.AutomaticallySetTerminalColor = true;
            var randomColorProfile = CreateDefaultTerminalProfile();
            state.Settings.AutomaticallySetTerminalColor = false;
            var defaultColorProfile = CreateDefaultTerminalProfile();
            state.Settings.AutomaticallySetTerminalColor = originalAutomaticTerminalColors;
            var automaticTerminalColorsWork = WorkspaceAccentPalette.Choices.Any(value => value.Value == randomColorProfile.AccentColor)
                && defaultColorProfile.AccentColor == WorkspaceAccentPalette.DefaultTerminal;
            var bracketedPasteSubmissionContract = BracketedPasteModeTracker.SubmissionContractPassesForTest();
            var agentActivityClassificationExact = TerminalPane.AgentActivityClassificationForTest();
            var codexInteractivePromptsDriveWaiting = CodexOutputActivityTracker.StateTransitionsPassForTest();
            var hermesActivityTransitionsExact = HermesOutputActivityTracker.StateTransitionsPassForTest();
            var remoteCodexActivityProbeBounded = RemoteCodexActivityProbe.CommandIsReadOnlyAndBoundedForTest();
            var agentNotificationsUseExactTransitions = ShouldNotifyAgentTransition(AgentKind.Codex, AgentActivityState.Working, AgentActivityState.Idle)
                && ShouldNotifyAgentTransition(AgentKind.Hermes, AgentActivityState.Working, AgentActivityState.Waiting)
                && !ShouldNotifyAgentTransition(AgentKind.Terminal, AgentActivityState.Working, AgentActivityState.Idle)
                && !ShouldNotifyAgentTransition(AgentKind.Codex, AgentActivityState.Idle, AgentActivityState.Idle)
                && !ShouldNotifyAgentTransition(AgentKind.Codex, AgentActivityState.Waiting, AgentActivityState.Idle);
            var newTerminalPersistenceDefaults = randomColorProfile.AutoStart && randomColorProfile.UseLocalTmux && randomColorProfile.UseRemoteTmux
                && randomColorProfile.AgentNotificationsEnabled
                && SessionAutoStartEdit.Style == FindResource("ThemedCheckBox") as Style
                && SessionAgentNotificationsEdit.Style == FindResource("ThemedCheckBox") as Style
                && SessionUseLocalTmuxEdit.Style == FindResource("ThemedCheckBox") as Style
                && SessionUseTmuxEdit.Style == FindResource("ThemedCheckBox") as Style;
            var originalNotificationEnabled = state.Settings.AgentNotificationsEnabled;
            var originalNotificationSound = state.Settings.NotificationSound;
            var originalNotificationPath = state.Settings.CustomNotificationSoundPath;
            var originalTmuxWarning = state.Settings.ShowTmuxToggleWarning;
            state.Settings.AgentNotificationsEnabled = false;
            state.Settings.NotificationSound = "Custom";
            state.Settings.CustomNotificationSoundPath = @"D:\Sounds\done.wav";
            state.Settings.ShowTmuxToggleWarning = false;
            var notificationSnapshot = WorkspaceStore.CreateSnapshot(state).Settings;
            var notificationSettingsPersist = !notificationSnapshot.AgentNotificationsEnabled
                && notificationSnapshot.NotificationSound == "Custom"
                && notificationSnapshot.CustomNotificationSoundPath == @"D:\Sounds\done.wav"
                && !notificationSnapshot.ShowTmuxToggleWarning;
            var originalTerminalNotifications = activationTarget.Profile.AgentNotificationsEnabled;
            activationTarget.Profile.AgentNotificationsEnabled = false;
            var perTerminalNotificationsPersist = !WorkspaceStore.CreateSnapshot(state).Sessions
                .Single(value => value.Id == activationTarget.Profile.Id).AgentNotificationsEnabled;
            activationTarget.Profile.AgentNotificationsEnabled = originalTerminalNotifications;
            var customNotificationToastReady = AgentNotificationToast.ContractPassesForTest();
            state.Settings.AgentNotificationsEnabled = originalNotificationEnabled;
            state.Settings.NotificationSound = originalNotificationSound;
            state.Settings.CustomNotificationSoundPath = originalNotificationPath;
            state.Settings.ShowTmuxToggleWarning = originalTmuxWarning;
            var cursorTransformConfigured = activationTarget.ForceCursorStyleForTest("\u001b[3 q") == "\u001b[5 q";
            var rendererVtStreamTransparent = activationTarget.RendererFilterPreservesVtStreamForTest();
            var remoteVtStreamTransparent = activationTarget.RemoteOutputRelayPreservesVtStreamForTest();
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
            var recoverySources = CollectRecoveryPaneSources();
            var recoveryCaptures = await Task.Run(() => MaterializeRecoveryPaneCaptures(recoverySources));
            var recoveryCapturesOutput = recoveryCaptures.Select((capture, index) =>
                capture.Output.Contains($"NATIVE_PANE_{index + 1}_READY", StringComparison.Ordinal)).All(value => value);
            var recoverySnapshotsAvoidUiThread = recoverySources.All(source => !source.Pane.LastRecoverySnapshotReadUsedDispatcherForTest);
            var recoveryOutputBuffersBounded = TerminalPane.BoundedRecoveryOutputWorksForTest()
                && panes.Values.All(pane => pane.RecoveryOutputIsBoundedForTest);
            var dependencyOutputLoggingDisabled = panes.Values.All(pane => pane.DependencyOutputLoggingDisabledForTest);

            const string scrollbarTailMarker = "TERMINAL_SCROLLBAR_TAIL_READY";
            var scrollbackAccepted = await activationTarget.SendCommandAsync(
                "1..180 | ForEach-Object { Write-Output ('SCROLLBACK_LINE_' + $_) }; Write-Output 'TERMINAL_SCROLLBAR_TAIL_READY'");
            var scrollbarDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < scrollbarDeadline
                && !activationTarget.GetOutput().Contains(scrollbarTailMarker, StringComparison.Ordinal)) await Task.Delay(120);
            await Dispatcher.Yield(DispatcherPriority.Render);
            activationTarget.UpdateLayout();
            var terminalScrollbarHasRealRange = scrollbackAccepted && activationTarget.TerminalScrollbarHasRangeForTest;
            var terminalScrollbarMovesNativeViewport = activationTarget.ExerciseTerminalScrollbarForTest();
            var terminalScrollbarRebindsReplacement = activationTarget.TerminalScrollbarRebindsReplacementForTest();
            activationTarget.SetPreviousOutputHiddenByDefaultForTest("RECOVERY_DEFAULT_HIDDEN_REGRESSION");
            var recoverySurfaceDefaultsHidden = activationTarget.TerminalSurfaceOwnsViewportForTest;
            activationTarget.SetPreviousOutputForTest("RECOVERY_SURFACE_CLICK_REGRESSION");
            var recoverySurfaceExcludesNativeTerminal = activationTarget.RecoverySurfaceOwnsViewportForTest;
            activationTarget.HidePreviousOutputForTest();
            var terminalSurfaceRestoredExclusively = activationTarget.TerminalSurfaceOwnsViewportForTest;
            var terminalClickAccepted = activationTarget.SimulateTerminalSurfaceClickForTest();
            var composerClickAccepted = activationTarget.FocusCommandInputForTest();
            await Dispatcher.Yield(DispatcherPriority.Input);
            var terminalClicksKeepRecoveryHidden = terminalClickAccepted && composerClickAccepted
                && !activationTarget.RecoveryOverlayVisibleForTest;
            var recoverySurfaceOwnershipStable = recoverySurfaceExcludesNativeTerminal
                && recoverySurfaceDefaultsHidden && terminalSurfaceRestoredExclusively && terminalClicksKeepRecoveryHidden;

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
            activationTarget.SetCommandInputForTest("alpha\nbeta\ngamma");
            activationTarget.SetCommandInputCaretForTest("alpha\nbeta".Length);
            var ctrlUHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.U, ModifierKeys.Control);
            var ctrlUDeletesToLineStart = ctrlUHandled && activationTarget.CommandInputTextForTest == "alpha\n\ngamma"
                && activationTarget.CommandInputCaretIndexForTest == "alpha\n".Length;
            activationTarget.SetCommandInputForTest("alpha\nbeta\ngamma");
            activationTarget.SetCommandInputCaretForTest("alpha\nbe".Length);
            var ctrlKHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.K, ModifierKeys.Control);
            var ctrlKDeletesToLineEnd = ctrlKHandled && activationTarget.CommandInputTextForTest == "alpha\nbe\ngamma"
                && activationTarget.CommandInputCaretIndexForTest == "alpha\nbe".Length;
            activationTarget.SetCommandInputForTest("alpha\nbeta");
            activationTarget.SetCommandInputCaretForTest("alpha".Length);
            var ctrlJHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.J, ModifierKeys.Control);
            var ctrlJAddsLine = ctrlJHandled && activationTarget.CommandInputTextForTest == "alpha\n\nbeta";
            var shiftEnterHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.Enter, ModifierKeys.Shift);
            var shiftEnterAddsLine = shiftEnterHandled && activationTarget.CommandInputTextForTest == "alpha\n\n\nbeta";
            activationTarget.SetCommandInputForTest("first\nsecond\nthird");
            activationTarget.SetCommandInputCaretForTest("first\nsecond".Length);
            activationTarget.FocusCommandInputForTest();
            activationTarget.UpdateLayout();
            var verticalStart = activationTarget.CommandInputCaretIndexForTest;
            var upLineHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.Up, ModifierKeys.None);
            var verticalUp = activationTarget.CommandInputCaretIndexForTest;
            var downLineHandled = await activationTarget.HandleCommandInputKeyForTestAsync(Key.Down, ModifierKeys.None);
            var verticalDown = activationTarget.CommandInputCaretIndexForTest;
            var arrowKeysNavigateComposerLines = upLineHandled && downLineHandled && verticalUp < verticalStart && verticalDown > verticalUp;
            activationTarget.FlushComposerStateForTest();
            var composerFlushBaseline = activationTarget.ComposerStateFlushCountForTest;
            var composerBurstTimer = System.Diagnostics.Stopwatch.StartNew();
            for (var characterCount = 1; characterCount <= 160; characterCount++)
                activationTarget.SetCommandInputDeferredForTest(new string('x', characterCount));
            composerBurstTimer.Stop();
            var composerFlushAfterBurst = activationTarget.ComposerStateFlushCountForTest;
            var composerStateWorkDebounced = composerFlushAfterBurst == composerFlushBaseline
                && activationTarget.ComposerStateTimerEnabledForTest;
            activationTarget.FlushComposerStateForTest();
            var composerFlushAfterIdle = activationTarget.ComposerStateFlushCountForTest;
            composerStateWorkDebounced &= composerFlushAfterIdle == composerFlushBaseline + 1;
            var sustainedTypingFlushBaseline = activationTarget.ComposerStateFlushCountForTest;
            for (var characterCount = 1; characterCount <= 4; characterCount++)
            {
                activationTarget.SetCommandInputDeferredForTest(new string('s', characterCount));
                await Task.Delay(70);
            }
            var sustainedFlushAfterBurst = activationTarget.ComposerStateFlushCountForTest;
            var composerStateDebouncesSustainedTyping = sustainedFlushAfterBurst == sustainedTypingFlushBaseline
                && activationTarget.ComposerStateTimerEnabledForTest;
            activationTarget.FlushComposerStateForTest();
            var sustainedFlushAfterIdle = activationTarget.ComposerStateFlushCountForTest;
            composerStateDebouncesSustainedTyping &= sustainedFlushAfterIdle == sustainedTypingFlushBaseline + 1;
            var realTyping = activationTarget.SimulateFastComposerTypingForTest(320);
            var queuedTyping = await activationTarget.SimulateQueuedComposerTypingForTestAsync(320, 3);
            var humanTyping = await activationTarget.SimulateQueuedComposerTypingForTestAsync(140, 20);
            var composerInputRoutingIsSelective = TerminalPane.ComposerInputRoutingIsSelectiveForTest();
            var agentProbeIntervals = panes.Values.Select(value => value.AgentStatusIntervalForTest).ToArray();
            var agentStatusProbesAreStaggered = agentProbeIntervals.All(value => value >= TimeSpan.FromMilliseconds(1100)
                    && value <= TimeSpan.FromMilliseconds(2000))
                && agentProbeIntervals.Distinct().Count() > 1;
            var saveHistoryBackups = state.Sessions
                .Select(value => (Profile: value, History: value.CommandHistory, Timestamps: value.CommandHistoryTimestampsUtc))
                .ToArray();
            ComposerInputLatencyResult saveConcurrentTyping;
            var asyncWorkspaceSaveKickoff = Stopwatch.StartNew();
            try
            {
                foreach (var value in state.Sessions)
                {
                    value.CommandHistory = Enumerable.Range(0, 80)
                        .Select(index => $"history-{index:D2}-" + new string('h', 4096))
                        .ToList();
                    value.CommandHistoryTimestampsUtc = Enumerable.Range(0, 80)
                        .Select(index => DateTime.UtcNow.AddMinutes(-index))
                        .ToList();
                }
                var saveTask = WorkspaceStore.SaveAsync(state);
                asyncWorkspaceSaveKickoff.Stop();
                saveConcurrentTyping = await activationTarget.SimulateQueuedComposerTypingForTestAsync(120, 20);
                await saveTask;
            }
            finally
            {
                foreach (var backup in saveHistoryBackups)
                {
                    backup.Profile.CommandHistory = backup.History;
                    backup.Profile.CommandHistoryTimestampsUtc = backup.Timestamps;
                }
            }
            await activationTarget.AgeComposerForTestAsync(24, 2400);
            var agedTyping = await activationTarget.SimulateQueuedComposerTypingForTestAsync(140, 20);
            var workspaceSnapshot = WorkspaceStore.CreateSnapshot(state);
            var workspaceSnapshotIsolated = !ReferenceEquals(workspaceSnapshot, state)
                && !ReferenceEquals(workspaceSnapshot.Sessions, state.Sessions)
                && workspaceSnapshot.Sessions.Count == state.Sessions.Count
                && workspaceSnapshot.Sessions.Zip(state.Sessions).All(value => !ReferenceEquals(value.First, value.Second));
            var asyncWorkspaceSaveDoesNotBlockInput = asyncWorkspaceSaveKickoff.Elapsed < TimeSpan.FromMilliseconds(50)
                && saveConcurrentTyping.TextMatches
                // A deliberately multi-megabyte checkpoint may share CPU with
                // the dispatcher, but it must never produce a visible key-repeat
                // stall. The actual edit remains separately bounded below.
                && saveConcurrentTyping.P95DispatchMilliseconds < 60
                && saveConcurrentTyping.MaximumDispatchMilliseconds < 100
                && saveConcurrentTyping.P95EditMilliseconds < 8;
            // The zero-delay 320-character insertion is a catastrophic-backlog guard, not the
            // interactive latency measurement. Hosted Windows runners can spend about 1.5s in
            // this synthetic loop while the human-paced dispatch/edit bounds below stay sub-frame.
            var composerTypingLatencyBounded = composerBurstTimer.Elapsed < TimeSpan.FromSeconds(1)
                && realTyping.Elapsed < TimeSpan.FromMilliseconds(2500)
                && realTyping.ExtractionsDuringTyping == 0 && realTyping.CanonicalTextMatches
                && queuedTyping.TextMatches
                && humanTyping.TextMatches
                && humanTyping.P95DispatchMilliseconds < 24
                && humanTyping.MaximumDispatchMilliseconds < 100
                && humanTyping.P95EditMilliseconds < 8
                // This deliberately abusive retained-state pass remains a
                // catastrophic-regression guard. Normal 50-cps typing above has
                // the strict interactive limit; here the key requirement is that
                // 24 rapid 2,400-character document cycles never recreate the
                // seconds-long backlog reported by the user.
                && agedTyping.P95DispatchMilliseconds < 150
                && agedTyping.MaximumDispatchMilliseconds < 200
                && agedTyping.P95EditMilliseconds < 8
                && composerInputRoutingIsSelective
                && agentStatusProbesAreStaggered
                && workspaceSnapshotIsolated
                && asyncWorkspaceSaveDoesNotBlockInput;
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
            var commandHistoryRecordsSentCommands = activationTarget.CommandHistoryCountForTest >= 3
                && activationTarget.Profile.CommandHistory.TakeLast(3).SequenceEqual([
                    "Write-Output 'QUEUE_NOW'", "Write-Output 'QUEUE_FIRST'", "Write-Output 'QUEUE_SECOND'"
                ])
                && activationTarget.Profile.CommandHistoryTimestampsUtc.Count == activationTarget.Profile.CommandHistory.Count
                && activationTarget.Profile.CommandHistoryTimestampsUtc[^1] > DateTime.UtcNow.AddMinutes(-1);
            var commandHistoryRelativeTimesWork = TerminalPane.FormatRelativeHistoryTime(DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow) == "30s"
                && TerminalPane.FormatRelativeHistoryTime(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow) == "1m"
                && TerminalPane.FormatRelativeHistoryTime(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow) == "1h";
            activationTarget.ShowCommandHistoryForTest();
            var commandHistoryPanelAdapts = activationTarget.CommandHistoryPanelVisibleForTest
                && activationTarget.CommandHistoryVisibleItemCountForTest == activationTarget.CommandHistoryCountForTest;
            var commandHistoryButtonIsFrameless = activationTarget.CommandHistoryButtonIsFramelessForTest;
            activationTarget.RestoreLatestCommandHistoryForTest();
            var commandHistoryRestoresInput = activationTarget.CommandInputTextForTest == "Write-Output 'QUEUE_SECOND'"
                && !activationTarget.CommandHistoryPanelVisibleForTest;
            var historyAttachmentFixture = Path.Combine(Path.GetDirectoryName(reportPath)!, "history-attachment-fixture.png");
            File.WriteAllBytes(historyAttachmentFixture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var staleHistoryAttachmentFixture = Path.Combine(Path.GetDirectoryName(reportPath)!, "stale-history-attachment-fixture.png");
            File.Copy(historyAttachmentFixture, staleHistoryAttachmentFixture, true);
            var historyAttachmentCommand = $"inspect {historyAttachmentFixture}";
            activationTarget.ClearComposerAttachmentsForTest();
            _ = activationTarget.AddComposerAttachmentForTest(staleHistoryAttachmentFixture, true);
            activationTarget.AddCommandHistoryForTest(historyAttachmentCommand);
            activationTarget.ShowCommandHistoryForTest();
            activationTarget.RestoreLatestCommandHistoryForTest();
            var historyAttachmentsRehydrate = activationTarget.CommandInputTextForTest.Contains(historyAttachmentFixture, StringComparison.OrdinalIgnoreCase)
                && activationTarget.ComposerAttachmentCountForTest == 1
                && !activationTarget.CommandInputTextForTest.Contains(staleHistoryAttachmentFixture, StringComparison.OrdinalIgnoreCase)
                && activationTarget.AttachmentStripVisibleForTest
                && activationTarget.ComposerTokensMatchCanonicalPathsForTest
                && !activationTarget.CommandHistoryPanelVisibleForTest;
            activationTarget.ClearComposerAttachmentsForTest();
            try { File.Delete(historyAttachmentFixture); } catch { }
            try { File.Delete(staleHistoryAttachmentFixture); } catch { }
            var composerSendSettingsMenuReady = activationTarget.RunCommandSettingsMenuReadyForTest();
            var originalPressEnterAfterSend = activationTarget.Profile.PressEnterAfterComposerSend;
            activationTarget.SetPressEnterAfterComposerSendForTest(true);
            var automaticSubmitMarker = $"COMPOSER_AUTO_ENTER_EXECUTED_{Guid.NewGuid():N}";
            var automaticSubmitAccepted = await activationTarget.SendComposerCommandAsync($"Write-Output '{automaticSubmitMarker}'");
            var automaticSubmitDeadline = DateTime.UtcNow.AddSeconds(5);
            while (automaticSubmitAccepted && DateTime.UtcNow < automaticSubmitDeadline
                   && !activationTarget.GetOutput().Contains(automaticSubmitMarker, StringComparison.Ordinal)) await Task.Delay(25);
            var composerAutomaticEnterSubmits = automaticSubmitAccepted
                && activationTarget.GetOutput().Contains(automaticSubmitMarker, StringComparison.Ordinal)
                && activationTarget.ComposerAutomaticEnterDelayForTest >= TimeSpan.FromMilliseconds(150);
            WorkspaceStore.Save(state);
            var persistedHistoryProfile = WorkspaceStore.Load(terminalProfile).Sessions.First(value => value.Id == activationTarget.Profile.Id);
            var commandHistoryPersists = persistedHistoryProfile.CommandHistory.SequenceEqual(activationTarget.Profile.CommandHistory)
                && persistedHistoryProfile.CommandHistoryTimestampsUtc.SequenceEqual(activationTarget.Profile.CommandHistoryTimestampsUtc);
            var composerSendBehaviorPersists = persistedHistoryProfile.PressEnterAfterComposerSend;
            activationTarget.SetPressEnterAfterComposerSendForTest(originalPressEnterAfterSend);
            var commandHistoryIsPerTerminal = panes[added[1].Id].Profile.CommandHistory.Count == 0;
            var historyCountBeforeClear = activationTarget.CommandHistoryCountForTest;
            var clearHistoryRequiresConfirmation = !activationTarget.ClearCommandHistoryForTest(false)
                && activationTarget.CommandHistoryCountForTest == historyCountBeforeClear;
            var clearHistoryButtonReady = activationTarget.ClearCommandHistoryButtonReadyForTest;
            var clearHistoryWorks = activationTarget.ClearCommandHistoryForTest(true)
                && activationTarget.CommandHistoryVisibleItemCountForTest == 0;
            WorkspaceStore.Save(state);
            var clearHistoryPersists = WorkspaceStore.Load(terminalProfile).Sessions
                .First(value => value.Id == activationTarget.Profile.Id).CommandHistory.Count == 0;
            activationTarget.SetCommandInputForTest(string.Empty);

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
            SetLayout("Tabs");
            var tabs = TerminalTabBar.Visibility == Visibility.Visible && TerminalTabList.Items.Count == expectedPanes
                && TerminalHost.Children.OfType<TerminalPane>().Count() == 1;
            WorkspaceSessionTabs.UpdateLayout();
            TerminalTabList.UpdateLayout();
            var workspaceTabContainer = WorkspaceSessionTabs.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var terminalTabContainer = TerminalTabList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var workspaceTabSurface = workspaceTabContainer is null ? null : FindContextMenuSurface(workspaceTabContainer);
            var terminalTabSurface = terminalTabContainer is null ? null : FindContextMenuSurface(terminalTabContainer);
            var terminalTabsShowAgentAndName = terminalTabContainer is not null
                && FindVisualDescendant<System.Windows.Shapes.Ellipse>(terminalTabContainer) is not null
                && FindVisualDescendant<TextBlock>(terminalTabContainer,
                    value => value.Text == ((SessionProfile)TerminalTabList.Items[0]).Name) is not null;
            var tabContextMenusWork = workspaceTabSurface?.ContextMenu is { } workspaceTabMenu
                && workspaceTabMenu.Items.OfType<MenuItem>().Any(value => string.Equals(value.Header?.ToString(), "Edit session", StringComparison.Ordinal))
                && terminalTabSurface?.ContextMenu is { } terminalTabMenu
                && terminalTabMenu.Items.OfType<MenuItem>().Any(value => string.Equals(value.Header?.ToString(), "Edit terminal", StringComparison.Ordinal))
                && OpenCardContextMenu(workspaceTabSurface, System.Windows.Controls.Primitives.PlacementMode.MousePoint)
                && OpenCardContextMenu(terminalTabSurface, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
            if (workspaceTabSurface?.ContextMenu is { } openedWorkspaceTabMenu) openedWorkspaceTabMenu.IsOpen = false;
            if (terminalTabSurface?.ContextMenu is { } openedTerminalTabMenu) openedTerminalTabMenu.IsOpen = false;
            var originalTerminalOrder = activeWorkspaceSession!.TerminalIds.ToArray();
            var tabReorderSource = state.Sessions.First(value => value.Id == originalTerminalOrder[1]);
            var tabReorderTarget = state.Sessions.First(value => value.Id == originalTerminalOrder[0]);
            MoveTerminalToDropPosition(tabReorderSource, tabReorderTarget, false);
            var terminalReorderSynchronizes = activeWorkspaceSession.TerminalIds[0] == tabReorderSource.Id
                && activeSessionTerminals[0].Id == tabReorderSource.Id && ReferenceEquals(TerminalTabList.Items[0], tabReorderSource);
            var moveSourceFixture = new TerminalSession { Name = "Drag source", TerminalIds = ["drag-a", "drag-b"], ActiveTerminalId = "drag-b" };
            var moveTargetFixture = new TerminalSession { Name = "Drag target", TerminalIds = ["drag-c"], ActiveTerminalId = "drag-c" };
            var terminalMovesAcrossSessions = MoveTerminalBetweenSessions(moveSourceFixture, moveTargetFixture, "drag-b", "drag-c", false)
                && moveSourceFixture.TerminalIds.SequenceEqual(["drag-a"])
                && moveSourceFixture.ActiveTerminalId == "drag-a"
                && moveTargetFixture.TerminalIds.SequenceEqual(["drag-b", "drag-c"])
                && moveTargetFixture.ActiveTerminalId == "drag-b"
                && MoveTerminalBetweenSessions(moveTargetFixture, moveTargetFixture, "drag-c", "drag-b", false)
                && moveTargetFixture.TerminalIds.SequenceEqual(["drag-c", "drag-b"]);
            var tmuxBadgeFixture = new SessionProfile();
            var tmuxBadgeTracksManagedState = !tmuxBadgeFixture.IsTmuxTerminal;
            tmuxBadgeFixture.SetTmuxTerminal(true);
            tmuxBadgeTracksManagedState = tmuxBadgeTracksManagedState && tmuxBadgeFixture.IsTmuxTerminal;
            tmuxBadgeFixture.SetTmuxTerminal(false);
            tmuxBadgeTracksManagedState = tmuxBadgeTracksManagedState && !tmuxBadgeFixture.IsTmuxTerminal;
            var terminalDragInteractionReady = TerminalHost.AllowDrop && WorkspaceSessionTabs.AllowDrop
                && terminalDragSessionHoverTimer.Interval == TimeSpan.FromMilliseconds(500)
                && activationTarget.HeaderDragContractPassesForTest;
            activeWorkspaceSession.TerminalIds = originalTerminalOrder.ToList();
            RefreshActiveTerminalList();
            SelectPane(tabReorderTarget.Id, false);
            var originalTerminalAccent = tabReorderTarget.AccentColor;
            var originalSessionAccent = activeWorkspaceSession.AccentColor;
            tabReorderTarget.AccentColor = "#2DD4BF";
            activeWorkspaceSession.AccentColor = "#FB7185";
            panes[tabReorderTarget.Id].RefreshProfileDisplay(tabReorderTarget);
            RefreshWorkspaceSessionViews();
            var accentColorsApply = panes[tabReorderTarget.Id].AccentAppliedForTest
                && WorkspaceAccentPalette.Normalize(tabReorderTarget.AccentColor, WorkspaceAccentPalette.DefaultTerminal) == "#2DD4BF"
                && WorkspaceAccentPalette.Normalize(activeWorkspaceSession.AccentColor, WorkspaceAccentPalette.DefaultSession) == "#FB7185";
            WorkspaceStore.Save(state);
            var persistedAccentWorkspace = WorkspaceStore.Load(terminalProfile);
            accentColorsApply = accentColorsApply
                && persistedAccentWorkspace.Sessions.First(value => value.Id == tabReorderTarget.Id).AccentColor == "#2DD4BF"
                && persistedAccentWorkspace.TerminalSessions.First(value => value.Id == activeWorkspaceSession.Id).AccentColor == "#FB7185"
                && persistedAccentWorkspace.TerminalSessions.First(value => value.Id == activeWorkspaceSession.Id).Layout == "Tabs";
            tabReorderTarget.AccentColor = originalTerminalAccent;
            activeWorkspaceSession.AccentColor = originalSessionAccent;
            panes[tabReorderTarget.Id].RefreshProfileDisplay(tabReorderTarget);
            RefreshWorkspaceSessionViews();
            var terminalHoverOrigin = activePane!;
            var terminalHoverCandidate = activeSessionTerminals.First(value => value.Id != terminalHoverOrigin.Profile.Id);
            BeginTerminalTabHoverPreviewForTest(terminalHoverCandidate);
            var terminalTabPreviewWaitsForDelay = !TerminalTabHoverPreviewActiveForTest;
            CompleteTerminalTabHoverDelayForTest();
            var terminalTabHoverPreviews = terminalTabPreviewWaitsForDelay && TerminalTabHoverDelayConfiguredForTest
                && TerminalTabHoverPreviewActiveForTest && activePane?.Profile.Id == terminalHoverCandidate.Id
                && state.ActiveSessionId == terminalHoverOrigin.Profile.Id
                && TerminalHost.Children.OfType<TerminalPane>().SingleOrDefault()?.Profile.Id == terminalHoverCandidate.Id;
            EndTerminalTabHoverPreviewForTest();
            var terminalTabHoverRestores = !TerminalTabHoverPreviewActiveForTest
                && ReferenceEquals(activePane, terminalHoverOrigin) && state.ActiveSessionId == terminalHoverOrigin.Profile.Id
                && TerminalHost.Children.OfType<TerminalPane>().SingleOrDefault()?.Profile.Id == terminalHoverOrigin.Profile.Id;
            SetLayout("Grid");

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
            var hoverPreviewWaitsForDelay = !WorkspaceSessionHoverPreviewActiveForTest;
            await Task.Delay(30);
            CompleteWorkspaceSessionHoverDelayForTest();
            var hoverPreviewSwitchesAfterDelay = hoverPreviewWaitsForDelay && WorkspaceSessionHoverDelayConfiguredForTest
                && WorkspaceSessionHoverPreviewActiveForTest
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
            var layoutsStayPerSession = alternateWorkspaceSession.Layout == "Columns" && primaryWorkspaceSession.Layout == "Rows"
                && string.Equals(ColumnsLayoutButton.Tag as string, "Active", StringComparison.Ordinal)
                && !string.Equals(RowsLayoutButton.Tag as string, "Active", StringComparison.Ordinal);
            WorkspaceStore.Save(state);
            var persistedWorkspace = WorkspaceStore.Load(terminalProfile);
            var sessionContainersPersist = persistedWorkspace.Version == 8
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
            var dailyRule = new AutomationRule { Command = "Write-Output daily", TargetSessionId = "*", ScheduleType = "Daily", DailyTime = "20:36", LastRunUtc = scheduleNow.AddDays(-1).ToUniversalTime() };
            var repeatedDailyRule = new AutomationRule { Command = "Write-Output daily", TargetSessionId = "*", ScheduleType = "Daily", DailyTime = "20:36", LastRunUtc = scheduleNow.ToUniversalTime() };
            var onceRule = new AutomationRule { Command = "Write-Output once", TargetSessionId = "*", ScheduleType = "Once", ScheduledDate = "2026-07-12", DailyTime = "20:36", LastRunUtc = scheduleNow.ToUniversalTime() };
            var futureOnceRule = new AutomationRule { Command = "Write-Output once", TargetSessionId = "*", ScheduleType = "Once", ScheduledDate = "2026-07-12", DailyTime = "20:37", LastRunUtc = scheduleNow.ToUniversalTime() };
            var scheduleLogic = dailyRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && !repeatedDailyRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && onceRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow) && !futureOnceRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow);
            var manualOnlyRule = new AutomationRule { Command = "Write-Output manual", TargetSessionId = AutomationRule.NoTarget, Enabled = true };
            var manualOnlyScheduleStaysDormant = !manualOnlyRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow)
                && manualOnlyRule.GetCountdownText(scheduleNow.ToUniversalTime(), scheduleNow) == "Manual only"
                && AutomationTargets(manualOnlyRule).Count == 0;
            var noScheduleRule = new AutomationRule { Command = "Write-Output manual", TargetSessionId = "*", ScheduleType = AutomationRule.NoSchedule, Enabled = true };
            var explicitNoScheduleStaysDormant = !noScheduleRule.IsDue(scheduleNow.ToUniversalTime(), scheduleNow)
                && noScheduleRule.GetNextRunLocal(scheduleNow.ToUniversalTime(), scheduleNow) is null
                && noScheduleRule.GetCountdownText(scheduleNow.ToUniversalTime(), scheduleNow) == "Manual only"
                && noScheduleRule.Subtitle == "No schedule";
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

            terminalAutomationFixture = new AutomationRule
            {
                Name = "Terminal footer fixture",
                Command = "Write-Output 'AUTOMATION_FOOTER'",
                TargetSessionId = AutomationRule.NoTarget,
                ClearLine = true,
                Enabled = true
            };
            state.Automations.Add(terminalAutomationFixture);
            var terminalStartsWithoutAutomations = activationTarget.AssignedAutomationCountForTest == 0;
            var automationButtonReady = activationTarget.AutomationButtonReadyForTest;
            var automationCanBeAddedPerTerminal = activationTarget.AddFirstAutomationForTest()
                && activationTarget.AssignedAutomationCountForTest == 1;
            var automationCanAutoInsert = activationTarget.ConfigureFirstAutomationForTest(true, true)
                && activationTarget.AppendAutomationTextForTest("hello") == "hello\nWrite-Output 'AUTOMATION_FOOTER'";
            var automationCanBeDisabled = activationTarget.ConfigureFirstAutomationForTest(false, true)
                && activationTarget.AppendAutomationTextForTest("hello") == "hello";
            activationTarget.ConfigureFirstAutomationForTest(true, true);
            var automationMenuContractReady = activationTarget.AutomationMenuContractForTest();
            var automationClearLineWorks = TerminalPane.AutomationClearLineInputWorksForTest();
            OpenAutomationEditor(terminalAutomationFixture);
            var automationEditorSupportsManualTargetAndClearLine = Equals(AutomationTargetEdit.SelectedValue, AutomationRule.NoTarget)
                && AutomationTypeEdit.SelectedIndex == 0
                && AutomationIntervalPanel.Visibility == Visibility.Collapsed
                && AutomationExactPanel.Visibility == Visibility.Collapsed
                && AutomationClearLineEdit.IsChecked == true;
            HideEditor();

            var originalLiveDirectory = activationTarget.Profile.LiveWorkingDirectory;
            var originalLiveDirectoryIsSsh = activationTarget.Profile.LiveWorkingDirectoryIsSsh;
            activationTarget.SetWorkingDirectoryForTest(@"D:\Dev\live-folder", false);
            var localDirectoryUpdates = activationTarget.Profile.Subtitle == @"D:\Dev\live-folder"
                && activationTarget.Profile.DirectoryPrefix.Length == 0;
            activationTarget.SetWorkingDirectoryForTest("/home/ubuntu/illest.bot", true);
            var sshDirectoryUpdates = activationTarget.Profile.Subtitle == "/home/ubuntu/illest.bot"
                && activationTarget.Profile.DirectoryPrefix == "SSH · ";
            var workingDirectoryMarkersParse = TerminalPane.WorkingDirectoryMarkerParsingWorksForTest();
            var localDirectoryHook = TerminalPane.BuildPowerShellDirectoryHook();
            var localDirectoryHookReady = localDirectoryHook.Contains("]9;9;", StringComparison.Ordinal)
                && localDirectoryHook.Contains("__PowerShellPlusOriginalPrompt", StringComparison.Ordinal)
                && localDirectoryHook.Contains("[char]27", StringComparison.OrdinalIgnoreCase)
                && !localDirectoryHook.Contains("`e", StringComparison.Ordinal);
            var terminalProtocolTextSanitized = TerminalTextSanitizer.RegressionCasesPassForTest();
            var interactiveSshWrapperForcesPty = SshLaunchStore.InteractiveWrapperForcesPtyForTest();
            var sshDirectoryHook = SshLaunchStore.BuildRemoteInteractiveShellCommand("fixture-pane");
            var sshDirectoryHookReady = sshDirectoryHook.Contains("PROMPT_COMMAND", StringComparison.Ordinal)
                && sshDirectoryHook.Contains("$PWD", StringComparison.Ordinal)
                && sshDirectoryHook.Contains("]9;9;", StringComparison.Ordinal)
                && sshDirectoryHook.Contains("\\033Ptmux;", StringComparison.Ordinal)
                && sshDirectoryHook.Contains("tmux display-message", StringComparison.Ordinal);
            var directSshDirectoryHook = SshLaunchStore.BuildRemoteInteractiveShellCommand("fixture-pane-direct", false);
            var terminalTmuxChoiceWorks = !directSshDirectoryHook.Contains("tmux new-session", StringComparison.Ordinal)
                && directSshDirectoryHook.Contains("$PWD", StringComparison.Ordinal)
                && new SessionProfile().UseRemoteTmux;
            var localTmuxPolicyFixture = new SessionProfile
            {
                CommandLine = "powershell.exe",
                WorkingDirectory = activationTarget.Profile.WorkingDirectory,
                UseRemoteTmux = false
            };
            var localTmuxPolicyDoesNotRestart = !TerminalEditRequiresRestart(localTmuxPolicyFixture,
                localTmuxPolicyFixture.CommandLine, localTmuxPolicyFixture.WorkingDirectory, true);
            var localTmuxPolicyRequiresRestart = TerminalEditRequiresRestart(localTmuxPolicyFixture,
                localTmuxPolicyFixture.CommandLine, localTmuxPolicyFixture.WorkingDirectory, false, true);
            localTmuxPolicyFixture.LiveWorkingDirectoryIsSsh = true;
            var activeSshTmuxPolicyRestarts = TerminalEditRequiresRestart(localTmuxPolicyFixture,
                localTmuxPolicyFixture.CommandLine, localTmuxPolicyFixture.WorkingDirectory, true);
            localTmuxPolicyFixture.SetTmuxTerminal(true);
            var tmuxEditorStatusIsTruthful = TerminalTmuxEditorStatus(new SessionProfile(), true).Contains("Remote policy armed", StringComparison.Ordinal)
                && TerminalTmuxEditorStatus(new SessionProfile(), false).StartsWith("Disabled", StringComparison.Ordinal)
                && TerminalTmuxEditorStatus(localTmuxPolicyFixture, true).Contains("Active on the SSH host", StringComparison.Ordinal)
                && TerminalTmuxEditorStatus(localTmuxPolicyFixture, false).Contains("Active now", StringComparison.Ordinal)
                && LocalTmuxEditorStatus(new SessionProfile(), true).Contains("Requires WSL", StringComparison.Ordinal)
                && LocalTmuxEditorStatus(new SessionProfile(), false).StartsWith("Off", StringComparison.Ordinal);
            var localTmuxLaunchContract = LocalTmuxSession.ContractPassesForTest();
            var bareSshResumePlan = SshRecovery.BuildResumePlan(new SessionRecoveryEntry
            {
                SessionId = "directory-hook-fixture",
                SshWasActive = true,
                SshConnectionArguments = ["ubuntu@example.test"]
            });
            var bareSshRecoveryKeepsDirectoryHook = bareSshResumePlan?.Arguments.LastOrDefault() is { } bareSshRemoteCommand
                && SshRecovery.TryDecodePowerShellSafeRemoteCommand(bareSshRemoteCommand, out var decodedBareSshRemoteCommand)
                && decodedBareSshRemoteCommand.Contains("PROMPT_COMMAND", StringComparison.Ordinal)
                && decodedBareSshRemoteCommand.Contains("]9;9;", StringComparison.Ordinal);
            var originalUseRemoteTmux = activationTarget.Profile.UseRemoteTmux;
            var originalUseLocalTmux = activationTarget.Profile.UseLocalTmux;
            var originalLocalTmuxDistribution = activationTarget.Profile.LocalTmuxDistribution;
            activationTarget.Profile.UseRemoteTmux = false;
            activationTarget.Profile.UseLocalTmux = true;
            activationTarget.Profile.LocalTmuxDistribution = "Ubuntu";
            WorkspaceStore.Save(state);
            var automationPersistenceWorkspace = WorkspaceStore.Load(terminalProfile);
            var automationPersistenceProfile = automationPersistenceWorkspace.Sessions.First(value => value.Id == activationTarget.Profile.Id);
            var automationPersistenceRule = automationPersistenceWorkspace.Automations.First(value => value.Id == terminalAutomationFixture.Id);
            var terminalAutomationStatePersists = automationPersistenceProfile.AutomationBindings.Count == 1
                && automationPersistenceProfile.AutomationBindings[0].AutomationId == terminalAutomationFixture.Id
                && automationPersistenceProfile.AutomationBindings[0].Enabled
                && automationPersistenceProfile.AutomationBindings[0].AutoInsertAtEnd
                && automationPersistenceRule.TargetSessionId == AutomationRule.NoTarget
                && automationPersistenceRule.ClearLine
                && automationPersistenceProfile.LiveWorkingDirectory == "/home/ubuntu/illest.bot"
                && automationPersistenceProfile.LiveWorkingDirectoryIsSsh;
            var terminalTmuxChoicePersists = !automationPersistenceProfile.UseRemoteTmux;
            var localTmuxChoicePersists = automationPersistenceProfile.UseLocalTmux
                && automationPersistenceProfile.LocalTmuxDistribution == "Ubuntu";
            activationTarget.Profile.UseRemoteTmux = originalUseRemoteTmux;
            activationTarget.Profile.UseLocalTmux = originalUseLocalTmux;
            activationTarget.Profile.LocalTmuxDistribution = originalLocalTmuxDistribution;
            WorkspaceStore.Save(state);
            activationTarget.Profile.LiveWorkingDirectory = originalLiveDirectory;
            activationTarget.Profile.LiveWorkingDirectoryIsSsh = originalLiveDirectoryIsSsh;
            activationTarget.Profile.NotifyDirectoryChanged();

            ShowSection(SessionsPanel);
            SelectPane(activationTarget.Profile.Id, false);
            selectedEditableValue = activationTarget.Profile;
            var f2OpensTerminalEditor = TryOpenSelectedEditor() && editorMode == EditorMode.Terminal
                && ReferenceEquals(editingValue, activationTarget.Profile) && EditorOverlay.Visibility == Visibility.Visible;
            var terminalTmuxEditorToggleReflectsProfile = SessionUseTmuxEdit.IsChecked == activationTarget.Profile.UseRemoteTmux
                && SessionUseLocalTmuxEdit.IsChecked == activationTarget.Profile.UseLocalTmux;
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

            // Run the destructive restart regression only after all established
            // live-process assertions so it cannot alter their process identity.
            SelectPane(activationTarget.Profile.Id, true);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            activationTarget.UpdateLayout();
            await activationTarget.RestartAsync();
            const string resumedScrollbarMarker = "TERMINAL_SCROLLBAR_RESUME_READY";
            var resumedScrollbackAccepted = await activationTarget.SendCommandAsync(
                "1..90 | ForEach-Object { Write-Output ('RESUMED_SCROLLBACK_LINE_' + $_) }; Write-Output 'TERMINAL_SCROLLBAR_RESUME_READY'");
            var resumedScrollbarDeadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < resumedScrollbarDeadline
                && !activationTarget.GetOutput().Contains(resumedScrollbarMarker, StringComparison.Ordinal)) await Task.Delay(120);
            var resumedScrollbarRangeDeadline = DateTime.UtcNow.AddSeconds(4);
            do
            {
                await Task.Delay(100);
                await Dispatcher.Yield(DispatcherPriority.Render);
                activationTarget.UpdateLayout();
            }
            while (DateTime.UtcNow < resumedScrollbarRangeDeadline && !activationTarget.TerminalScrollbarHasRangeForTest);
            var terminalScrollbarSurvivesRestart = resumedScrollbackAccepted
                && activationTarget.TerminalScrollbarHasRangeForTest && activationTarget.ExerciseTerminalScrollbarForTest();

            var paneCommandSystem = paneCommandInputTakesFocus && handoffButtonReady && commandBarCollapses && commandBarStatePersists && commandBarExpands && queueAddsCommands && queueStatePersists && currentCommandRuns
                && nextQueuedCommandPromoted && upArrowBrowsesQueue && firstQueuedCommandRuns && queueAdvances && secondQueuedCommandRuns && queueDrains
                && quickAccessFiltersCommands && quickAccessTogglePersists && quickAccessPopulatesInput && queueCommandsExecuted && queueMenuListsCommands
                && ctrlEnterQueues && queueButtonOpensQueue && commandInputAutoGrows && composerChromeStaysCompact && textPasteWorks && cursorBarEnforced
                && rendererVtStreamTransparent && remoteVtStreamTransparent
                && shiftModifierRoutesAll && sendAllVisualFeedback && modifierCanBeDisabled && modifierCanBeRemapped && sendAllSettingsPersist && commandReachedAllPanes
                && commandHistoryRecordsSentCommands && commandHistoryRelativeTimesWork && commandHistoryPanelAdapts && commandHistoryButtonIsFrameless
                && commandHistoryRestoresInput && historyAttachmentsRehydrate && commandHistoryPersists && commandHistoryIsPerTerminal
                && composerSendSettingsMenuReady && composerSendBehaviorPersists && composerAutomaticEnterSubmits && shiftClickQuickCreatesTerminal && automaticTerminalColorsWork
                && clearHistoryRequiresConfirmation && clearHistoryButtonReady && clearHistoryWorks && clearHistoryPersists
                && ctrlUDeletesToLineStart && ctrlKDeletesToLineEnd && ctrlJAddsLine && shiftEnterAddsLine
                && arrowKeysNavigateComposerLines && composerStateWorkDebounced && composerStateDebouncesSustainedTyping && composerTypingLatencyBounded;
            var success = inputReady && outputReady && recoveryCapturesOutput && recoverySnapshotsAvoidUiThread
                && recoveryOutputBuffersBounded && dependencyOutputLoggingDisabled
                && terminalScrollbarsThemed && terminalScrollbarsInteractive && terminalScrollbarBridgesStable
                && inactiveTerminalsStartEagerly && startupFailureOffersRetry && startupManualRetryWorks
                && tmuxScrollbackBridgeContract && localTmuxScrollbarRoutingContract && persistentTmuxScrollChannelContract && trayLifecycleContract
                && terminalScrollbarHasRealRange && terminalScrollbarMovesNativeViewport && terminalScrollbarRebindsReplacement && terminalScrollbarSurvivesRestart && recoverySurfaceOwnershipStable
                && settingsScrollbarThemed && updateUiContractReady && themeCatalogContract && themePickerReady && liveThemeSwitchWorks && themeSelectionPersists && themeStartupPersistenceWorks && gradientThemeReady && customThemeEditorReady && settingsThemeColorPickerOpens && customThemeLivePreview && customThemeCancelRestores && themeRestored
                && startupLoadingScreenReady && layoutControlsInSidebar && layoutHoverPreviewsReady && layoutPreviewGeometryWorks && layoutTransitionContractReady
                && sidebarCollapses && sidebarExpands && sidebarStatePersists && sidebarCardsUseSingleFrame && sidebarCardHoverStylesReady && sidebarCardSelectionVisible && workspaceCardMenuReliable && terminalCardMenuReliable
                && terminalSurfaceHooked && terminalInputRouterPrecedesConPty && terminalTabQueuesInsideConPty && remoteImagePasteIndicatorReady && remoteImageShortcutInterceptReady && remoteImagePasteModesWork && remoteSshPasteConsumesAllClipboardKinds && threadMessagePasteInterceptsBeforeConPty && remoteImagePasteIndicatorStatesWork
                && tmuxControlKeysReachConPty && tmuxOwnsCursorSequences
                && composerAttachmentAdded && secondComposerAttachmentAdded && composerImagePreviewOpens && composerDraftTracksAttachments
                && composerTypingAvoidsPillRebuild
                && composerTokensMatchCanonicalPaths && composerBlankSpacePreservesTokens && attachmentPillReorderUpdatesCommand && composerScrollbarThemed && perTerminalFontZoomPersists
                && composerFileDropAddsAttachment && composerFileDropIndicatorsWork && attachmentPillDropReplacesFile
                && profileStartupWatchdogWorks
                && attachmentPreviewKindsWork && removingPathRemovesPill && composerSshPathsRewrite
                && terminalSurfaceActivatesPane && terminalSurfaceTakesKeyboardFocus && windowIconLoaded && executableIconEmbedded
                && rows && columns && focus && grid && tabs && terminalTabsShowAgentAndName && tabContextMenusWork && terminalReorderSynchronizes
                && terminalMovesAcrossSessions && tmuxBadgeTracksManagedState && terminalDragInteractionReady
                && accentColorsApply && hoverPreviewSwitchesAfterDelay && hoverPreviewRestoresOnLeave && terminalTabHoverPreviews && terminalTabHoverRestores
                && sessionSwitchShowsOwnedTerminals && layoutsStayPerSession && sessionContainersPersist && legacySessionsMigrateWithoutLosingTerminals
                && agentWorkingStateVisible && agentWaitingStateVisible && agentIdleStateVisible && plainPowerShellHeaderVisible && terminalTabAgentStateMirrorsPane
                && agentNotificationsUseExactTransitions && notificationSettingsPersist && perTerminalNotificationsPersist
                && customNotificationToastReady && newTerminalPersistenceDefaults
                && inputEchoDoesNotActivateAgent && codexTurnEventsDriveAgent && codexActivityGrowthScanBounded && bracketedPasteSubmissionContract
                && codexInteractivePromptsDriveWaiting && agentActivityClassificationExact
                && hermesActivityTransitionsExact && remoteCodexActivityProbeBounded
                && scheduleLogic && countdownLogic && automationHoverContainerStable && manualOnlyScheduleStaysDormant && explicitNoScheduleStaysDormant
                && terminalStartsWithoutAutomations && automationButtonReady && automationCanBeAddedPerTerminal
                && automationCanAutoInsert && automationCanBeDisabled && automationMenuContractReady && automationClearLineWorks
                && automationEditorSupportsManualTargetAndClearLine && terminalAutomationStatePersists
                && localDirectoryUpdates && sshDirectoryUpdates && workingDirectoryMarkersParse && localDirectoryHookReady && sshDirectoryHookReady
                && terminalTmuxChoiceWorks && terminalTmuxChoicePersists && terminalTmuxEditorToggleReflectsProfile
                && localTmuxPolicyDoesNotRestart && localTmuxPolicyRequiresRestart && activeSshTmuxPolicyRestarts
                && tmuxEditorStatusIsTruthful && localTmuxLaunchContract && localTmuxChoicePersists
                && terminalProtocolTextSanitized && interactiveSshWrapperForcesPty && bareSshRecoveryKeepsDirectoryHook
                && terminalRenamePreservesLiveState
                && f2OpensSelectedEditors && editorCardKeepsEditorOpen && backdropDismissesEditor && paneCommandSystem
                && composerInputRoutingIsSelective && workspaceSnapshotIsolated && asyncWorkspaceSaveDoesNotBlockInput;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Native panes accepted responsive input, hover-previewed Session containers, per-Session layouts, agent state animation, compact multiline composition, and scheduler behavior.\nInputReady={inputReady}\nOutputReady={outputReady}\nRecoveryCapturesOutput={recoveryCapturesOutput}\nRecoverySnapshotsAvoidUiThread={recoverySnapshotsAvoidUiThread}\nRecoveryOutputBuffersBounded={recoveryOutputBuffersBounded}\nDependencyOutputLoggingDisabled={dependencyOutputLoggingDisabled}\nTerminalScrollbarsThemed={terminalScrollbarsThemed}\nTerminalScrollbarsInteractive={terminalScrollbarsInteractive}\nLayoutControlsInSidebar={layoutControlsInSidebar}\nLayoutHoverPreviewsReady={layoutHoverPreviewsReady}\nLayoutPreviewGeometryWorks={layoutPreviewGeometryWorks}\nLayoutTransitionContractReady={layoutTransitionContractReady}\nSidebarCollapses={sidebarCollapses}\nSidebarExpands={sidebarExpands}\nSidebarStatePersists={sidebarStatePersists}\nPaneCommandInputTakesFocus={paneCommandInputTakesFocus}\nTerminalSurfaceHooked={terminalSurfaceHooked}\nTerminalSurfaceActivatesPane={terminalSurfaceActivatesPane}\nTerminalSurfaceTakesKeyboardFocus={terminalSurfaceTakesKeyboardFocus}\nCommandInputAutoGrows={commandInputAutoGrows}\nComposerChromeStaysCompact={composerChromeStaysCompact}\nAgentWorkingStateVisible={agentWorkingStateVisible}\nAgentWaitingStateVisible={agentWaitingStateVisible}\nHoverPreviewSwitchesAfterDelay={hoverPreviewSwitchesAfterDelay}\nHoverPreviewRestoresOnLeave={hoverPreviewRestoresOnLeave}\nSessionSwitchShowsOwnedTerminals={sessionSwitchShowsOwnedTerminals}\nLayoutsStayPerSession={layoutsStayPerSession}\nSessionContainersPersist={sessionContainersPersist}\nLegacySessionsMigrateWithoutLosingTerminals={legacySessionsMigrateWithoutLosingTerminals}\nTextPasteWorks={textPasteWorks}\nCursorTransformConfigured={cursorTransformConfigured}\nRendererVtStreamTransparent={rendererVtStreamTransparent}\nCursorSequenceAccepted={cursorSequenceAccepted}\nCursorCommandCompleted={cursorCommandCompleted}\nLastBarCursor={lastBarCursor}\nLastUnderlineCursor={lastUnderlineCursor}\nCursorBarEnforced={cursorBarEnforced}\nCommandBarCollapses={commandBarCollapses}\nCommandBarStatePersists={commandBarStatePersists}\nCommandBarExpands={commandBarExpands}\nQueueAddsCommands={queueAddsCommands}\nQueueMenuListsCommands={queueMenuListsCommands}\nQueueStatePersists={queueStatePersists}\nCtrlEnterQueues={ctrlEnterQueues}\nQueueButtonOpensQueue={queueButtonOpensQueue}\nCurrentCommandRuns={currentCommandRuns}\nNextQueuedCommandPromoted={nextQueuedCommandPromoted}\nUpArrowBrowsesQueue={upArrowBrowsesQueue}\nQueueAdvances={queueAdvances}\nQueueDrains={queueDrains}\nQuickAccessFiltersCommands={quickAccessFiltersCommands}\nQuickAccessTogglePersists={quickAccessTogglePersists}\nQuickAccessPopulatesInput={quickAccessPopulatesInput}\nQueueCommandsExecuted={queueCommandsExecuted}\nShiftModifierRoutesAll={shiftModifierRoutesAll}\nSendAllVisualFeedback={sendAllVisualFeedback}\nModifierCanBeDisabled={modifierCanBeDisabled}\nModifierCanBeRemapped={modifierCanBeRemapped}\nSendAllSettingsPersist={sendAllSettingsPersist}\nCommandReachedAllPanes={commandReachedAllPanes}\nWindowIconLoaded={windowIconLoaded}\nExecutableIconEmbedded={executableIconEmbedded}\nGrid={grid}\nRows={rows}\nColumns={columns}\nFocus={focus}\nExactSchedules={scheduleLogic}\nCountdownFormatting={countdownLogic}\nAutomationHoverContainerStable={automationHoverContainerStable}");
            File.AppendAllText(reportPath, $"\nInputEchoDoesNotActivateAgent={inputEchoDoesNotActivateAgent}\nCodexTurnEventsDriveAgent={codexTurnEventsDriveAgent}\nCodexActivityGrowthScanBounded={codexActivityGrowthScanBounded}\nBracketedPasteSubmissionContract={bracketedPasteSubmissionContract}\nRendererVtStreamTransparent={rendererVtStreamTransparent}\nRemoteVtStreamTransparent={remoteVtStreamTransparent}\nCodexInteractivePromptsDriveWaiting={codexInteractivePromptsDriveWaiting}\nHermesActivityTransitionsExact={hermesActivityTransitionsExact}\nRemoteCodexActivityProbeBounded={remoteCodexActivityProbeBounded}");
            File.AppendAllText(reportPath, $"\nAgentNotificationsUseExactTransitions={agentNotificationsUseExactTransitions}\nNotificationSettingsPersist={notificationSettingsPersist}\nPerTerminalNotificationsPersist={perTerminalNotificationsPersist}\nCustomNotificationToastReady={customNotificationToastReady}\nNewTerminalPersistenceDefaults={newTerminalPersistenceDefaults}\nTmuxControlKeysReachConPty={tmuxControlKeysReachConPty}\nTmuxOwnsCursorSequences={tmuxOwnsCursorSequences}");
            File.AppendAllText(reportPath, $"\nSettingsScrollbarThemed={settingsScrollbarThemed}\nTabsLayout={tabs}\nTerminalTabsShowAgentAndName={terminalTabsShowAgentAndName}\nTerminalTabAgentStateMirrorsPane={terminalTabAgentStateMirrorsPane}\nTerminalTabHoverPreviews={terminalTabHoverPreviews}\nTerminalTabHoverRestores={terminalTabHoverRestores}\nTerminalReorderSynchronizes={terminalReorderSynchronizes}\nTerminalMovesAcrossSessions={terminalMovesAcrossSessions}\nTmuxBadgeTracksManagedState={tmuxBadgeTracksManagedState}\nTerminalDragInteractionReady={terminalDragInteractionReady}\nAccentColorsApply={accentColorsApply}\nAgentIdleStateVisible={agentIdleStateVisible}\nPlainPowerShellHeaderVisible={plainPowerShellHeaderVisible}\nAgentActivityClassificationExact={agentActivityClassificationExact}");
            File.AppendAllText(reportPath, $"\nUpdateUiContractReady={updateUiContractReady}");
            File.AppendAllText(reportPath, $"\nThemeCatalogContract={themeCatalogContract}\nThemePickerReady={themePickerReady}\nLiveThemeSwitchWorks={liveThemeSwitchWorks}\nThemeSelectionPersists={themeSelectionPersists}\nThemeStartupPersistenceWorks={themeStartupPersistenceWorks}\nGradientThemeReady={gradientThemeReady}\nCustomThemeEditorReady={customThemeEditorReady}\nSettingsThemeColorPickerOpens={settingsThemeColorPickerOpens}\nCustomThemeLivePreview={customThemeLivePreview}\nCustomThemeCancelRestores={customThemeCancelRestores}\nThemeRestored={themeRestored}");
            File.AppendAllText(reportPath, $"\nStartupLoadingScreenReady={startupLoadingScreenReady}");
            File.AppendAllText(reportPath, $"\nInactiveTerminalsStartEagerly={inactiveTerminalsStartEagerly}\nStartupFailureOffersRetry={startupFailureOffersRetry}\nStartupManualRetryWorks={startupManualRetryWorks}");
            File.AppendAllText(reportPath, $"\nSidebarCardsUseSingleFrame={sidebarCardsUseSingleFrame}\nSidebarCardHoverStylesReady={sidebarCardHoverStylesReady}\nSidebarCardSelectionVisible={sidebarCardSelectionVisible}\nWorkspaceCardMenuReliable={workspaceCardMenuReliable}\nTerminalCardMenuReliable={terminalCardMenuReliable}\nTabContextMenusWork={tabContextMenusWork}");
            File.AppendAllText(reportPath, $"\nCommandHistoryRecordsSentCommands={commandHistoryRecordsSentCommands}\nCommandHistoryRelativeTimesWork={commandHistoryRelativeTimesWork}\nCommandHistoryPanelAdapts={commandHistoryPanelAdapts}\nCommandHistoryButtonIsFrameless={commandHistoryButtonIsFrameless}\nCommandHistoryRestoresInput={commandHistoryRestoresInput}\nHistoryAttachmentsRehydrate={historyAttachmentsRehydrate}\nCommandHistoryPersists={commandHistoryPersists}\nCommandHistoryIsPerTerminal={commandHistoryIsPerTerminal}\nComposerSendSettingsMenuReady={composerSendSettingsMenuReady}\nComposerSendBehaviorPersists={composerSendBehaviorPersists}\nComposerAutomaticEnterSubmits={composerAutomaticEnterSubmits}\nShiftClickQuickCreatesTerminal={shiftClickQuickCreatesTerminal}\nAutomaticTerminalColorsWork={automaticTerminalColorsWork}\nClearHistoryRequiresConfirmation={clearHistoryRequiresConfirmation}\nClearHistoryButtonReady={clearHistoryButtonReady}\nClearHistoryWorks={clearHistoryWorks}\nClearHistoryPersists={clearHistoryPersists}");
            File.AppendAllText(reportPath, $"\nCtrlUDeletesToLineStart={ctrlUDeletesToLineStart}\nCtrlKDeletesToLineEnd={ctrlKDeletesToLineEnd}\nCtrlJAddsLine={ctrlJAddsLine}\nShiftEnterAddsLine={shiftEnterAddsLine}\nArrowKeysNavigateComposerLines={arrowKeysNavigateComposerLines}\nComposerStateWorkDebounced={composerStateWorkDebounced}\nComposerFlushBaseline={composerFlushBaseline}\nComposerFlushAfterBurst={composerFlushAfterBurst}\nComposerFlushAfterIdle={composerFlushAfterIdle}\nComposerStateDebouncesSustainedTyping={composerStateDebouncesSustainedTyping}\nSustainedFlushBaseline={sustainedTypingFlushBaseline}\nSustainedFlushAfterBurst={sustainedFlushAfterBurst}\nSustainedFlushAfterIdle={sustainedFlushAfterIdle}\nComposerTypingLatencyBounded={composerTypingLatencyBounded}\nComposerBurstMilliseconds={composerBurstTimer.Elapsed.TotalMilliseconds:F1}\nRealTypingMilliseconds={realTyping.Elapsed.TotalMilliseconds:F1}\nCanonicalExtractionsDuringTyping={realTyping.ExtractionsDuringTyping}\nQueuedTypingMilliseconds={queuedTyping.Total.TotalMilliseconds:F1}\nQueuedTypingP50DispatchMilliseconds={queuedTyping.P50DispatchMilliseconds:F2}\nQueuedTypingP95DispatchMilliseconds={queuedTyping.P95DispatchMilliseconds:F2}\nQueuedTypingMaxDispatchMilliseconds={queuedTyping.MaximumDispatchMilliseconds:F2}\nQueuedTypingP95EditMilliseconds={queuedTyping.P95EditMilliseconds:F2}\nQueuedTypingMaxEditMilliseconds={queuedTyping.MaximumEditMilliseconds:F2}\nQueuedTypingLayoutUpdates={queuedTyping.LayoutUpdates}\nHumanTypingMilliseconds={humanTyping.Total.TotalMilliseconds:F1}\nHumanTypingP50DispatchMilliseconds={humanTyping.P50DispatchMilliseconds:F2}\nHumanTypingP95DispatchMilliseconds={humanTyping.P95DispatchMilliseconds:F2}\nHumanTypingMaxDispatchMilliseconds={humanTyping.MaximumDispatchMilliseconds:F2}\nHumanTypingP95EditMilliseconds={humanTyping.P95EditMilliseconds:F2}\nHumanTypingMaxEditMilliseconds={humanTyping.MaximumEditMilliseconds:F2}\nHumanTypingLayoutUpdates={humanTyping.LayoutUpdates}");
            File.AppendAllText(reportPath, $"\nAgedTypingMilliseconds={agedTyping.Total.TotalMilliseconds:F1}\nAgedTypingP50DispatchMilliseconds={agedTyping.P50DispatchMilliseconds:F2}\nAgedTypingP95DispatchMilliseconds={agedTyping.P95DispatchMilliseconds:F2}\nAgedTypingMaxDispatchMilliseconds={agedTyping.MaximumDispatchMilliseconds:F2}\nAgedTypingP95EditMilliseconds={agedTyping.P95EditMilliseconds:F2}\nAgedTypingMaxEditMilliseconds={agedTyping.MaximumEditMilliseconds:F2}\nAgedTypingLayoutUpdates={agedTyping.LayoutUpdates}\nComposerCanUndoAfterAging={activationTarget.ComposerCanUndoForTest}\nComposerUndoLimit={activationTarget.ComposerUndoLimitForTest}");
            File.AppendAllText(reportPath, $"\nHumanTypingSlowOperations={humanTyping.SlowOperations}\nAgedTypingSlowOperations={agedTyping.SlowOperations}\nSaveConcurrentTypingSlowOperations={saveConcurrentTyping.SlowOperations}");
            File.AppendAllText(reportPath, $"\nComposerInputRoutingIsSelective={composerInputRoutingIsSelective}\nAgentStatusProbesAreStaggered={agentStatusProbesAreStaggered}\nAgentStatusProbeIntervals={string.Join(',', agentProbeIntervals.Select(value => value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)))}\nWorkspaceSnapshotIsolated={workspaceSnapshotIsolated}\nAsyncWorkspaceSaveDoesNotBlockInput={asyncWorkspaceSaveDoesNotBlockInput}\nAsyncWorkspaceSaveKickoffMilliseconds={asyncWorkspaceSaveKickoff.Elapsed.TotalMilliseconds:F2}\nSaveConcurrentTypingP95DispatchMilliseconds={saveConcurrentTyping.P95DispatchMilliseconds:F2}\nSaveConcurrentTypingMaxDispatchMilliseconds={saveConcurrentTyping.MaximumDispatchMilliseconds:F2}\nSaveConcurrentTypingP95EditMilliseconds={saveConcurrentTyping.P95EditMilliseconds:F2}");
            File.AppendAllText(reportPath, $"\nManualOnlyScheduleStaysDormant={manualOnlyScheduleStaysDormant}\nExplicitNoScheduleStaysDormant={explicitNoScheduleStaysDormant}\nTerminalStartsWithoutAutomations={terminalStartsWithoutAutomations}\nAutomationButtonReady={automationButtonReady}\nAutomationCanBeAddedPerTerminal={automationCanBeAddedPerTerminal}\nAutomationCanAutoInsert={automationCanAutoInsert}\nAutomationCanBeDisabled={automationCanBeDisabled}\nAutomationMenuContractReady={automationMenuContractReady}\nAutomationClearLineWorks={automationClearLineWorks}\nAutomationEditorSupportsManualTargetAndClearLine={automationEditorSupportsManualTargetAndClearLine}\nTerminalAutomationStatePersists={terminalAutomationStatePersists}");
            File.AppendAllText(reportPath, $"\nLocalDirectoryUpdates={localDirectoryUpdates}\nSshDirectoryUpdates={sshDirectoryUpdates}\nWorkingDirectoryMarkersParse={workingDirectoryMarkersParse}\nLocalDirectoryHookReady={localDirectoryHookReady}\nSshDirectoryHookReady={sshDirectoryHookReady}\nTerminalTmuxChoiceWorks={terminalTmuxChoiceWorks}\nTerminalTmuxChoicePersists={terminalTmuxChoicePersists}\nLocalTmuxChoicePersists={localTmuxChoicePersists}\nTerminalTmuxEditorToggleReflectsProfile={terminalTmuxEditorToggleReflectsProfile}\nLocalTmuxPolicyDoesNotRestart={localTmuxPolicyDoesNotRestart}\nLocalTmuxPolicyRequiresRestart={localTmuxPolicyRequiresRestart}\nActiveSshTmuxPolicyRestarts={activeSshTmuxPolicyRestarts}\nLocalTmuxLaunchContract={localTmuxLaunchContract}\nTmuxEditorStatusIsTruthful={tmuxEditorStatusIsTruthful}\nTerminalProtocolTextSanitized={terminalProtocolTextSanitized}\nInteractiveSshWrapperForcesPty={interactiveSshWrapperForcesPty}\nBareSshRecoveryKeepsDirectoryHook={bareSshRecoveryKeepsDirectoryHook}");
            File.AppendAllText(reportPath, $"\nTerminalRenamePreservesLiveState={terminalRenamePreservesLiveState}\nF2OpensSelectedEditors={f2OpensSelectedEditors}\nEditorCardKeepsEditorOpen={editorCardKeepsEditorOpen}\nBackdropDismissesEditor={backdropDismissesEditor}\nTerminalInputRouterPrecedesConPty={terminalInputRouterPrecedesConPty}\nThreadMessagePasteInterceptsBeforeConPty={threadMessagePasteInterceptsBeforeConPty}\nTerminalTabQueuesInsideConPty={terminalTabQueuesInsideConPty}\nRemoteImagePasteIndicatorReady={remoteImagePasteIndicatorReady}\nRemoteImageShortcutInterceptReady={remoteImageShortcutInterceptReady}\nRemoteImagePasteModesWork={remoteImagePasteModesWork}\nRemoteSshPasteConsumesAllClipboardKinds={remoteSshPasteConsumesAllClipboardKinds}\nRemoteImagePasteIndicatorStatesWork={remoteImagePasteIndicatorStatesWork}\nComposerAttachmentAdded={composerAttachmentAdded}\nComposerImagePreviewOpens={composerImagePreviewOpens}\nComposerSshPathsRewrite={composerSshPathsRewrite}");
            File.AppendAllText(reportPath, $"\nComposerTypingAvoidsPillRebuild={composerTypingAvoidsPillRebuild}");
            File.AppendAllText(reportPath, $"\nComposerDraftTracksAttachments={composerDraftTracksAttachments}\nAttachmentPreviewKindsWork={attachmentPreviewKindsWork}\nRemovingPathRemovesPill={removingPathRemovesPill}");
            File.AppendAllText(reportPath, $"\nPlainTextPathPromoted={plainTextPathPromoted}\nSecondComposerAttachmentAdded={secondComposerAttachmentAdded}\nComposerTokensMatchCanonicalPaths={composerTokensMatchCanonicalPaths}\nComposerBlankSpacePreservesTokens={composerBlankSpacePreservesTokens}\nAttachmentPillReorderUpdatesCommand={attachmentPillReorderUpdatesCommand}\nComposerScrollbarThemed={composerScrollbarThemed}\nPerTerminalFontZoomPersists={perTerminalFontZoomPersists}");
            File.AppendAllText(reportPath, $"\nComposerFileDropAddsAttachment={composerFileDropAddsAttachment}\nComposerFileDropIndicatorsWork={composerFileDropIndicatorsWork}\nAttachmentPillDropReplacesFile={attachmentPillDropReplacesFile}");
            File.AppendAllText(reportPath, $"\nProfileStartupWatchdogWorks={profileStartupWatchdogWorks}");
            File.AppendAllText(reportPath, $"\nTerminalScrollbarBridgesStable={terminalScrollbarBridgesStable}\nTmuxScrollbackBridgeContract={tmuxScrollbackBridgeContract}\nLocalTmuxScrollbarRoutingContract={localTmuxScrollbarRoutingContract}\nPersistentTmuxScrollChannelContract={persistentTmuxScrollChannelContract}\nTrayLifecycleContract={trayLifecycleContract}\nTerminalScrollbarHasRealRange={terminalScrollbarHasRealRange}\nTerminalScrollbarMovesNativeViewport={terminalScrollbarMovesNativeViewport}\nTerminalScrollbarRebindsReplacement={terminalScrollbarRebindsReplacement}\nTerminalScrollbarSurvivesRestart={terminalScrollbarSurvivesRestart}\nRecoverySurfaceDefaultsHidden={recoverySurfaceDefaultsHidden}\nRecoverySurfaceExcludesNativeTerminal={recoverySurfaceExcludesNativeTerminal}\nTerminalSurfaceRestoredExclusively={terminalSurfaceRestoredExclusively}\nTerminalClicksKeepRecoveryHidden={terminalClicksKeepRecoveryHidden}\nRecoverySurfaceOwnershipStable={recoverySurfaceOwnershipStable}");
            if (!terminalScrollbarBridgesStable)
                foreach (var pane in panes.Values) File.AppendAllText(reportPath, $"\nTerminalScrollbarBridge[{pane.Profile.Name}]={pane.TerminalScrollbarBridgeDiagnosticForTest}");
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
            if (terminalAutomationFixture is not null)
            {
                foreach (var profile in state.Sessions) profile.AutomationBindings.RemoveAll(value => value.AutomationId == terminalAutomationFixture.Id);
                state.Automations.Remove(terminalAutomationFixture);
            }
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
    private void TerminalCardToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionProfile profile, ToolTip: ToolTip toolTip }
            || !panes.TryGetValue(profile.Id, out var pane)) return;
        var rootProcessId = pane.GetRootProcessId();
        var sshProcess = rootProcessId is int pid ? ProcessTreeInspector.FindSshProcess(pid) : default;
        var sshLaunch = SshLaunchStore.Load(profile.Id);
        loadedRecovery.Sessions.TryGetValue(profile.Id, out var recovery);
        string[]? sshArguments = null;
        if (sshProcess.IsActive)
        {
            sshArguments = sshLaunch?.IsActive == true && sshLaunch.ShellProcessId == rootProcessId
                ? sshLaunch.ConnectionArguments
                : recovery?.SshWasActive == true ? recovery.SshConnectionArguments : null;
        }
        var codexLaunch = CodexLaunchStore.Load(profile.Id);
        if (codexLaunch?.IsActive != true || codexLaunch.ShellProcessId != rootProcessId) codexLaunch = null;
        var details = TerminalHoverDetailsBuilder.Build(profile, rootProcessId, pane.DetectedAgentKind,
            pane.AgentActivityStateForTest, pane.GetCodexProcessState().IsActive, sshProcess.IsActive,
            sshArguments, recovery, codexLaunch);
        toolTip.Content = CreateTerminalDetailsContent(details);
    }
    private static FrameworkElement CreateTerminalDetailsContent(TerminalHoverDetails details)
    {
        var panel = new StackPanel { Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = details.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(205, 214, 244)),
            Margin = new Thickness(0, 1, 0, 8)
        });
        foreach (var row in details.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock
            {
                Text = row.Label,
                Foreground = new SolidColorBrush(Color.FromRgb(137, 180, 250)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top
            });
            var value = new TextBlock
            {
                Text = row.Value,
                Foreground = new SolidColorBrush(Color.FromRgb(186, 194, 222)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            panel.Children.Add(grid);
        }
        return panel;
    }
    private void NewSessionClick(object sender, RoutedEventArgs e)
    {
        if (ShouldQuickCreateTerminal(Keyboard.Modifiers))
        {
            CreateDefaultTerminal();
            return;
        }
        OpenSessionEditor(null);
    }

    internal static bool ShouldQuickCreateTerminal(ModifierKeys modifiers)
        => modifiers.HasFlag(ModifierKeys.Shift);
    private void WorkspaceSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (workspaceSessionSelectionSync || sender is not ListBox list || list.SelectedItem is not TerminalSession value) return;
        CancelWorkspaceSessionHoverPreview(false);
        CancelTerminalTabHoverPreview(false);
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
        if (terminalDragSourceId is not null) return;
        CancelTerminalTabHoverPreview(true);
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
    internal void CompleteWorkspaceSessionHoverDelayForTest() => WorkspaceSessionHoverTimerTick(null, EventArgs.Empty);
    internal bool WorkspaceSessionHoverDelayConfiguredForTest => workspaceSessionHoverTimer.Interval == TimeSpan.FromMilliseconds(500);
    internal void EndWorkspaceSessionHoverPreviewForTest() => CancelWorkspaceSessionHoverPreview(true);
    internal bool WorkspaceSessionHoverPreviewActiveForTest => workspaceSessionHoverPreviewActive;

    private void TerminalTabMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionProfile profile }
            || activeWorkspaceSession?.Layout != "Tabs" || terminalDragSourceId is not null || e.LeftButton == MouseButtonState.Pressed) return;
        terminalTabHoverTimer.Stop();
        terminalTabHoverCandidate = profile;
        if (profile.Id == activePane?.Profile.Id) return;
        terminalTabHoverTimer.Start();
    }

    private void TerminalTabMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionProfile profile } && terminalTabHoverCandidate != profile
            && (!terminalTabHoverPreviewActive || activePane?.Profile != profile)) return;
        CancelTerminalTabHoverPreview(true);
    }

    private void TerminalTabHoverTimerTick(object? sender, EventArgs e)
    {
        terminalTabHoverTimer.Stop();
        var candidate = terminalTabHoverCandidate;
        if (candidate is null || candidate.Id == activePane?.Profile.Id || activeWorkspaceSession?.Layout != "Tabs"
            || !activeSessionTerminals.Contains(candidate) || !panes.TryGetValue(candidate.Id, out var pane)) return;
        terminalTabHoverOrigin = activePane;
        if (terminalTabHoverOrigin is null) return;
        terminalTabHoverPreviewActive = true;
        DisplayTerminalTabPreview(pane);
    }

    private void DisplayTerminalTabPreview(TerminalPane pane)
    {
        activePane = pane;
        foreach (var value in panes.Values) value.SetActive(value == pane);
        ApplyLayout(false);
        UpdateStatus($"Previewing {pane.Profile.Name} · move away to return");
    }

    private void CancelTerminalTabHoverPreview(bool restoreOrigin)
    {
        terminalTabHoverTimer.Stop();
        var origin = terminalTabHoverOrigin;
        var wasPreviewing = terminalTabHoverPreviewActive;
        terminalTabHoverCandidate = null;
        terminalTabHoverOrigin = null;
        terminalTabHoverPreviewActive = false;
        if (!restoreOrigin || !wasPreviewing || origin is null) return;
        DisplayTerminalTabPreview(origin);
        UpdateStatus(activeWorkspaceSession is null ? origin.Profile.Name : $"{activeWorkspaceSession.Name} · {origin.Profile.Name}");
    }

    internal void BeginTerminalTabHoverPreviewForTest(SessionProfile profile)
    {
        terminalTabHoverTimer.Stop();
        terminalTabHoverCandidate = profile;
        terminalTabHoverTimer.Start();
    }
    internal void CompleteTerminalTabHoverDelayForTest() => TerminalTabHoverTimerTick(null, EventArgs.Empty);
    internal void EndTerminalTabHoverPreviewForTest() => CancelTerminalTabHoverPreview(true);
    internal bool TerminalTabHoverPreviewActiveForTest => terminalTabHoverPreviewActive;
    internal bool TerminalTabHoverDelayConfiguredForTest => terminalTabHoverTimer.Interval == TimeSpan.FromMilliseconds(500);
    private void NewWorkspaceSessionClick(object sender, RoutedEventArgs e)
    {
        var session = new TerminalSession { Name = $"Session {state.TerminalSessions.Count + 1}" };
        state.TerminalSessions.Add(session);
        SelectWorkspaceSession(session.Id, false);
        var terminal = CreateDefaultTerminalProfile();
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
        SetWorkspaceEditorAccent(value.AccentColor);
        ShowEditor(WorkspaceSessionEditor);
        WorkspaceSessionNameEdit.Focus();
        WorkspaceSessionNameEdit.SelectAll();
    }
    private void WorkspaceSessionRenameClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSender<TerminalSession>(sender) is { } value) OpenWorkspaceSessionEditor(value);
    }
    private async void WorkspaceSessionRemoveClick(object sender, RoutedEventArgs e)
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
        CaptureRecoverySnapshot();
        var recoverySnapshot = SessionRecoveryStore.Load();
        foreach (var terminalId in value.TerminalIds.ToArray())
        {
            if (state.Sessions.FirstOrDefault(item => item.Id == terminalId) is not { } profile) continue;
            var removed = profile.UseLocalTmux
                ? await StopLocalAndRemoveAsync(profile)
                : recoverySnapshot.Sessions.TryGetValue(profile.Id, out var recovery)
                    && recovery.SshWasActive && recovery.RemoteTmuxManaged
                    ? await StopRemoteAndRemoveAsync(profile, recovery)
                    : RemoveSession(profile, true);
            if (!removed)
            {
                RefreshWorkspaceSessionViews();
                ScheduleSave();
                return;
            }
        }
        state.TerminalSessions.Remove(value);
        var next = state.TerminalSessions.First();
        SelectWorkspaceSession(next.Id, false);
        RefreshWorkspaceSessionViews();
        ScheduleSave();
    }
    private void EditSessionClick(object sender, RoutedEventArgs e) { if (SessionList.SelectedItem is SessionProfile value) OpenSessionEditor(value); }
    private async void RestartSessionClick(object sender, RoutedEventArgs e) { if (activePane is not null) await activePane.RestartAsync(); }
    private async void RemoveSessionClick(object sender, RoutedEventArgs e) { if (SessionList.SelectedItem is SessionProfile value) await CloseTerminalAsync(value); }
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

    private void TerminalTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (terminalTabSelectionSync || TerminalTabList.SelectedItem is not SessionProfile value) return;
        CancelTerminalTabHoverPreview(false);
        SelectPane(value.Id, false);
        selectedEditableValue = value;
    }

    private void TerminalOrderDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.ChangedButton != MouseButton.Left) return;
        terminalOrderDragStart = e.GetPosition(list);
        terminalOrderDragId = (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext is SessionProfile profile
            ? profile.Id
            : null;
    }

    private void TerminalOrderDragEnd(object sender, MouseButtonEventArgs e)
    {
        terminalOrderDragStart = null;
        terminalOrderDragId = null;
    }

    private void TerminalOrderDragMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || terminalOrderDragStart is not Point start || terminalOrderDragId is not { } terminalId
            || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(list);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        BeginTerminalDrag(terminalId);
        terminalOrderDragStart = null;
        terminalOrderDragId = null;
    }

    private void BeginTerminalDrag(string terminalId)
    {
        if (terminalDragSourceId is not null || !panes.ContainsKey(terminalId)) return;
        var owner = state.TerminalSessions.FirstOrDefault(value => value.TerminalIds.Contains(terminalId, StringComparer.Ordinal));
        if (owner is null) return;
        CancelWorkspaceSessionHoverPreview(true);
        terminalDragSourceId = terminalId;
        terminalDragOriginSessionId = owner.Id;
        terminalDragMoveCompleted = false;
        var data = new DataObject(TerminalDragDataFormat, terminalId);
        try { DragDrop.DoDragDrop(this, data, DragDropEffects.Move); }
        finally
        {
            terminalDragSessionHoverTimer.Stop();
            terminalDragHoverSessionId = null;
            SetTerminalDragSessionIndicator(null);
            SetTerminalDropIndicator(null, false);
            if (!terminalDragMoveCompleted && terminalDragOriginSessionId is { } originId
                && !string.Equals(state.ActiveTerminalSessionId, originId, StringComparison.Ordinal))
                SelectWorkspaceSession(originId, false);
            terminalDragSourceId = null;
            terminalDragOriginSessionId = null;
        }
    }

    private void TerminalOrderDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TerminalDragDataFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TerminalOrderDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || e.Data.GetData(TerminalDragDataFormat) is not string sourceId
            || activeWorkspaceSession is null || state.Sessions.FirstOrDefault(value => value.Id == sourceId) is not { } source) return;
        var container = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = container?.DataContext as SessionProfile;
        var position = container is null ? default : e.GetPosition(container);
        var after = container is not null && (ReferenceEquals(list, TerminalTabList)
            ? position.X >= container.ActualWidth / 2
            : position.Y >= container.ActualHeight / 2);
        terminalDragMoveCompleted = MoveTerminalToSessionPosition(source, activeWorkspaceSession, target, after);
        e.Effects = terminalDragMoveCompleted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void MoveTerminalToDropPosition(SessionProfile source, SessionProfile? target, bool after)
    {
        if (activeWorkspaceSession is not null) MoveTerminalToSessionPosition(source, activeWorkspaceSession, target, after);
    }

    private bool MoveTerminalToSessionPosition(SessionProfile source, TerminalSession targetSession, SessionProfile? target, bool after)
    {
        var sourceSession = state.TerminalSessions.FirstOrDefault(value => value.TerminalIds.Contains(source.Id, StringComparer.Ordinal));
        if (sourceSession is null || !MoveTerminalBetweenSessions(sourceSession, targetSession, source.Id, target?.Id, after)) return false;
        if (!ReferenceEquals(activeWorkspaceSession, targetSession)) SelectWorkspaceSession(targetSession.Id, false);
        RefreshActiveTerminalList();
        RefreshWorkspaceSessionViews();
        ApplyLayout();
        SelectPane(source.Id, false);
        ScheduleSave();
        UpdateStatus(ReferenceEquals(sourceSession, targetSession)
            ? $"Reordered {source.Name}"
            : $"Moved {source.Name} to {targetSession.Name}");
        return true;
    }

    private static bool MoveTerminalBetweenSessions(TerminalSession sourceSession, TerminalSession targetSession,
        string terminalId, string? targetTerminalId, bool after)
    {
        var current = sourceSession.TerminalIds.IndexOf(terminalId);
        if (current < 0 || targetSession.TerminalIds.Contains(terminalId, StringComparer.Ordinal)
            && !ReferenceEquals(sourceSession, targetSession)) return false;
        var insertion = targetTerminalId is null ? targetSession.TerminalIds.Count : targetSession.TerminalIds.IndexOf(targetTerminalId);
        if (insertion < 0) return false;
        if (targetTerminalId is not null && after) insertion++;
        sourceSession.TerminalIds.RemoveAt(current);
        if (ReferenceEquals(sourceSession, targetSession) && current < insertion) insertion--;
        insertion = Math.Clamp(insertion, 0, targetSession.TerminalIds.Count);
        targetSession.TerminalIds.Insert(insertion, terminalId);
        if (sourceSession.ActiveTerminalId == terminalId && !ReferenceEquals(sourceSession, targetSession))
            sourceSession.ActiveTerminalId = sourceSession.TerminalIds.FirstOrDefault();
        targetSession.ActiveTerminalId = terminalId;
        return true;
    }

    private void WorkspaceSessionTerminalDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || e.Data.GetData(TerminalDragDataFormat) is not string
            || ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem container
            || container.DataContext is not TerminalSession targetSession)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        SetTerminalDragSessionIndicator(container);
        if (!string.Equals(terminalDragHoverSessionId, targetSession.Id, StringComparison.Ordinal))
        {
            terminalDragSessionHoverTimer.Stop();
            terminalDragHoverSessionId = targetSession.Id;
            if (!string.Equals(state.ActiveTerminalSessionId, targetSession.Id, StringComparison.Ordinal))
                terminalDragSessionHoverTimer.Start();
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void WorkspaceSessionTerminalDragLeave(object sender, DragEventArgs e)
    {
        terminalDragSessionHoverTimer.Stop();
        terminalDragHoverSessionId = null;
        SetTerminalDragSessionIndicator(null);
        e.Handled = true;
    }

    private void WorkspaceSessionTerminalDrop(object sender, DragEventArgs e)
    {
        terminalDragSessionHoverTimer.Stop();
        if (sender is ListBox list && e.Data.GetData(TerminalDragDataFormat) is string sourceId
            && state.Sessions.FirstOrDefault(value => value.Id == sourceId) is { } source
            && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem container
            && container.DataContext is TerminalSession targetSession)
        {
            terminalDragMoveCompleted = MoveTerminalToSessionPosition(source, targetSession, null, true);
            e.Effects = terminalDragMoveCompleted ? DragDropEffects.Move : DragDropEffects.None;
        }
        SetTerminalDragSessionIndicator(null);
        terminalDragHoverSessionId = null;
        e.Handled = true;
    }

    private void TerminalDragSessionHoverTimerTick(object? sender, EventArgs e)
    {
        terminalDragSessionHoverTimer.Stop();
        if (terminalDragSourceId is null || terminalDragHoverSessionId is not { } sessionId
            || string.Equals(state.ActiveTerminalSessionId, sessionId, StringComparison.Ordinal)) return;
        SelectWorkspaceSession(sessionId, false);
        if (state.Sessions.FirstOrDefault(value => value.Id == terminalDragSourceId) is { } profile
            && state.TerminalSessions.FirstOrDefault(value => value.Id == sessionId) is { } target)
            UpdateStatus($"Move {profile.Name} to {target.Name} · choose its position");
    }

    private void SetTerminalDragSessionIndicator(ListBoxItem? container)
    {
        if (ReferenceEquals(terminalDragHoverSessionContainer, container)) return;
        if (terminalDragHoverSessionContainer is not null) terminalDragHoverSessionContainer.Tag = null;
        terminalDragHoverSessionContainer = container;
        if (container is not null) container.Tag = true;
    }

    private void TerminalLayoutDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TerminalDragDataFormat) is not string || activeWorkspaceSession is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var (target, after) = ResolveTerminalLayoutDropTarget(e.GetPosition(TerminalHost));
        SetTerminalDropIndicator(target, after);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TerminalLayoutDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(TerminalHost);
        if (point.X >= 0 && point.Y >= 0 && point.X <= TerminalHost.ActualWidth && point.Y <= TerminalHost.ActualHeight) return;
        SetTerminalDropIndicator(null, false);
        e.Handled = true;
    }

    private void TerminalLayoutDrop(object sender, DragEventArgs e)
    {
        var (targetPane, after) = ResolveTerminalLayoutDropTarget(e.GetPosition(TerminalHost));
        if (e.Data.GetData(TerminalDragDataFormat) is string sourceId && activeWorkspaceSession is not null
            && state.Sessions.FirstOrDefault(value => value.Id == sourceId) is { } source)
            terminalDragMoveCompleted = MoveTerminalToSessionPosition(source, activeWorkspaceSession, targetPane?.Profile, after);
        SetTerminalDropIndicator(null, false);
        e.Effects = terminalDragMoveCompleted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private (TerminalPane? Pane, bool After) ResolveTerminalLayoutDropTarget(Point point)
    {
        var candidates = TerminalHost.Children.OfType<TerminalPane>()
            .Where(value => value.Visibility == Visibility.Visible && value.ActualWidth > 0 && value.ActualHeight > 0)
            .Select(value => (Pane: value, Origin: value.TranslatePoint(new Point(), TerminalHost)))
            .ToArray();
        if (candidates.Length == 0) return (null, false);
        var candidate = candidates.FirstOrDefault(value => new Rect(value.Origin, value.Pane.RenderSize).Contains(point));
        if (candidate.Pane is null)
            candidate = candidates.MinBy(value => Math.Pow(point.X - value.Origin.X - value.Pane.ActualWidth / 2, 2)
                + Math.Pow(point.Y - value.Origin.Y - value.Pane.ActualHeight / 2, 2));
        var local = new Point(point.X - candidate.Origin.X, point.Y - candidate.Origin.Y);
        var after = activeWorkspaceSession?.Layout == "Rows"
            ? local.Y >= candidate.Pane.ActualHeight / 2
            : local.X >= candidate.Pane.ActualWidth / 2;
        return (candidate.Pane, after);
    }

    private void SetTerminalDropIndicator(TerminalPane? pane, bool after)
    {
        if (terminalDropIndicatorPane is not null && !ReferenceEquals(terminalDropIndicatorPane, pane))
            terminalDropIndicatorPane.SetDropTargetIndicator(false, false);
        terminalDropIndicatorPane = pane;
        pane?.SetDropTargetIndicator(true, after);
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
    private void CardRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement surface || !OpenCardContextMenu(surface, System.Windows.Controls.Primitives.PlacementMode.MousePoint)) return;
        e.Handled = true;
    }
    private bool OpenCardContextMenu(FrameworkElement surface, System.Windows.Controls.Primitives.PlacementMode placement)
    {
        if (surface.ContextMenu is not { } menu) return false;
        SelectCard(surface.DataContext);
        menu.PlacementTarget = surface;
        menu.Placement = placement;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;
        return menu.IsOpen;
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
    private async void SessionItemRestartClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value && panes.TryGetValue(value.Id, out var pane)) { SessionList.SelectedItem = value; if (value.IsRemoteDetached) await ReattachRemoteTerminalAsync(pane, true); else await pane.RestartAsync(); } }
    private async void SessionItemDetachRemoteClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) { SessionList.SelectedItem = value; await DetachRemoteTerminalAsync(value); } }
    private async void SessionItemToggleTmuxClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) await ToggleTmuxPersistenceAsync(value); }
    private void SessionItemNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSender<SessionProfile>(sender) is not { } value) return;
        value.AgentNotificationsEnabled = sender is MenuItem item ? item.IsChecked : !value.AgentNotificationsEnabled;
        ScheduleSave();
        UpdateStatus($"Agent notifications {(value.AgentNotificationsEnabled ? "enabled" : "disabled")} for {value.Name}");
    }
    private async void TmuxBadgeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionProfile profile }) await ToggleTmuxPersistenceAsync(profile);
        e.Handled = true;
    }
    private async Task ToggleTmuxPersistenceAsync(SessionProfile profile)
    {
        if (!panes.TryGetValue(profile.Id, out var pane) || remoteDetachOperations.Contains(profile.Id)) return;
        var ssh = profile.LiveWorkingDirectoryIsSsh || pane.HasActiveSshConnection
            || loadedRecovery.Sessions.TryGetValue(profile.Id, out var recovery) && recovery.SshWasActive;
        var currentlyManaged = profile.IsTmuxTerminal;
        var enable = !currentlyManaged;
        if (state.Settings.ShowTmuxToggleWarning)
        {
            var location = ssh ? "the SSH host" : "WSL on this computer";
            var action = enable ? "move this terminal into" : "remove this terminal from";
            var choice = PowerShellPlusDialog.ConfirmWithDontShowAgain(this,
                $"PowerShellPlus will {action} tmux on {location}. The terminal must restart to change its live backend. A foreground command that is not already protected by tmux may be interrupted.\n\nThe terminal's verified SSH and agent recovery information will be preserved.",
                enable ? "Enable tmux persistence?" : "Disable tmux persistence?",
                enable ? "Enable tmux" : "Disable tmux", primaryIsDangerous: !enable);
            if (choice.DontShowAgain)
            {
                state.Settings.ShowTmuxToggleWarning = false;
                SettingsTmuxToggleWarning.IsChecked = false;
                ScheduleSave();
            }
            if (!choice.Accepted) return;
        }
        try
        {
            string? distribution = profile.LocalTmuxDistribution;
            if (!ssh && enable)
            {
                var status = await LocalTmuxSession.ProbeAsync(distribution, profile.Id);
                if (!status.CommandSucceeded || !status.WslAvailable || !status.TmuxAvailable)
                    throw new InvalidOperationException(status.Message);
                distribution = status.Distribution;
            }
            await ApplyTerminalEditAsync(profile, profile.Name, profile.CommandLine, profile.WorkingDirectory, profile.AutoStart,
                profile.AccentColor, ssh ? enable : null, ssh ? null : enable, distribution);
            ScheduleSave();
            RefreshActiveTerminalList();
            UpdateStatus($"{(enable ? "Enabled" : "Disabled")} tmux persistence for {profile.Name}");
        }
        catch (Exception exception)
        {
            LogNativeError("Toggle tmux persistence", exception);
            PowerShellPlusDialog.ShowMessage(this, exception.Message + "\n\nThe terminal was restored to its previous backend.",
                "Tmux persistence was not changed", PowerShellPlusDialogKind.Error);
        }
    }
    private void SessionItemOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSender<SessionProfile>(sender) is not { } profile || !panes.TryGetValue(profile.Id, out var pane)) return;
        if (profile.LiveWorkingDirectoryIsSsh || pane.HasActiveSshConnection)
        {
            UpdateStatus("Open file location is available for local terminals only");
            return;
        }
        var directory = Directory.Exists(profile.LiveWorkingDirectory) ? profile.LiveWorkingDirectory : profile.WorkingDirectory;
        try
        {
            var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
            UpdateStatus($"Opened {directory}");
        }
        catch (Exception exception)
        {
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "File location could not be opened", PowerShellPlusDialogKind.Error);
        }
    }
    private void SessionItemUpClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) MoveSession(value, -1); }
    private void SessionItemDownClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) MoveSession(value, 1); }
    private async void SessionItemRemoveClick(object sender, RoutedEventArgs e) { if (ItemFromSender<SessionProfile>(sender) is { } value) { SessionList.SelectedItem = value; await CloseTerminalAsync(value); } }
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
    private void TabsLayoutClick(object sender, RoutedEventArgs e) => SetLayout("Tabs");
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
    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T rootMatch && predicate(rootMatch)) return rootMatch;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindVisualDescendant(VisualTreeHelper.GetChild(root, index), predicate);
            if (match is not null) return match;
        }
        return null;
    }
    private static FrameworkElement? FindContextMenuSurface(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: not null } surface) return surface;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindContextMenuSurface(VisualTreeHelper.GetChild(root, index));
            if (match is not null) return match;
        }
        return null;
    }
    private void UpdateAutomationScheduleEditor()
    {
        if (AutomationIntervalPanel is null) return;
        var interval = AutomationTypeEdit.SelectedIndex == 1;
        var exact = AutomationTypeEdit.SelectedIndex is 2 or 3;
        AutomationIntervalPanel.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;
        AutomationExactPanel.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
        AutomationDatePanel.Visibility = AutomationTypeEdit.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        AutomationTimeLabel.Text = AutomationTypeEdit.SelectedIndex == 3 ? "Run at" : "Run every day at";
    }
    private void BrowseDirectoryClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { InitialDirectory = SessionDirectoryEdit.Text }; if (dialog.ShowDialog(this) == true) SessionDirectoryEdit.Text = dialog.FolderName; }
    private void SettingsSectionClick(object sender, RoutedEventArgs e) => ShowSection(SettingsPanel);

    private bool settingsUiReady;
    private void RefreshThemeList(string? selectedId = null)
    {
        var ready = settingsUiReady;
        settingsUiReady = false;
        SettingsThemeList.ItemsSource = null;
        SettingsThemeList.ItemsSource = AppThemeCatalog.Themes;
        SettingsThemeList.SelectedItem = AppThemeCatalog.Resolve(selectedId ?? state.Settings.ApplicationTheme);
        settingsUiReady = ready;
        UpdateCustomThemeButtons();
    }

    private void UpdateCustomThemeButtons()
    {
        var theme = SettingsThemeList.SelectedItem as AppThemeDefinition;
        EditCustomThemeButton.IsEnabled = theme is not null;
        DeleteCustomThemeButton.IsEnabled = theme?.IsCustom == true;
    }

    private void NewCustomThemeClick(object sender, RoutedEventArgs e)
    {
        var basis = SettingsThemeList.SelectedItem as AppThemeDefinition ?? AppThemeCatalog.Resolve(state.Settings.ApplicationTheme);
        customThemePreviewOriginal = AppThemeCatalog.Resolve(state.Settings.ApplicationTheme);
        editingCustomThemeId = null;
        customThemeDraft = new CustomAppThemeState
        {
            Name = "My theme",
            Background = basis.Background,
            Surface = basis.Surface,
            Accent = basis.Accent,
            Text = basis.Text,
            UseGradient = basis.IsGradient,
            GradientEnd = basis.GradientEnd ?? AppThemeCatalog.NormalizeColor(basis.Raised, "#242438"),
            GradientDirection = basis.GradientDirection
        };
        ShowCustomThemeEditor("Create custom theme");
    }

    private void EditCustomThemeClick(object sender, RoutedEventArgs e)
    {
        if (SettingsThemeList.SelectedItem is not AppThemeDefinition selected) return;
        customThemePreviewOriginal = AppThemeCatalog.Resolve(state.Settings.ApplicationTheme);
        var saved = state.Settings.CustomThemes.FirstOrDefault(value => string.Equals(value.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        editingCustomThemeId = saved?.Id;
        customThemeDraft = saved?.Copy() ?? new CustomAppThemeState
        {
            Name = $"{selected.Name} Custom",
            Background = selected.Background,
            Surface = selected.Surface,
            Accent = selected.Accent,
            Text = selected.Text,
            UseGradient = selected.IsGradient,
            GradientEnd = selected.GradientEnd ?? selected.Raised,
            GradientDirection = selected.GradientDirection
        };
        ShowCustomThemeEditor(saved is null ? $"Customize {selected.Name}" : "Edit custom theme");
    }

    private void ShowCustomThemeEditor(string title)
    {
        if (customThemeDraft is null) return;
        CustomThemeEditorTitle.Text = title;
        CustomThemeNameEdit.Text = customThemeDraft.Name;
        CustomThemeValidationText.Visibility = Visibility.Collapsed;
        CustomThemeEditorPanel.Visibility = Visibility.Visible;
        UpdateCustomThemeEditorVisuals();
        CustomThemeEditorPanel.BringIntoView();
        Dispatcher.BeginInvoke(() => CustomThemeNameEdit.Focus(), DispatcherPriority.Loaded);
    }

    private void CancelCustomThemeClick(object sender, RoutedEventArgs e)
    {
        if (customThemePreviewOriginal is { } original) ApplyApplicationThemeVisuals(original);
        customThemePreviewOriginal = null;
        customThemeDraft = null;
        editingCustomThemeId = null;
        CustomThemeEditorPanel.Visibility = Visibility.Collapsed;
        InvalidateVisual();
    }

    private void SaveCustomThemeClick(object sender, RoutedEventArgs e)
    {
        if (customThemeDraft is null) return;
        var name = CustomThemeNameEdit.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            CustomThemeValidationText.Text = "Give this theme a name before saving it.";
            CustomThemeValidationText.Visibility = Visibility.Visible;
            CustomThemeNameEdit.Focus();
            return;
        }

        customThemeDraft.Name = name;
        var saved = AppThemeCatalog.NormalizeCustomTheme(customThemeDraft);
        var existingIndex = state.Settings.CustomThemes.FindIndex(value =>
            string.Equals(value.Id, editingCustomThemeId ?? saved.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            saved.Id = state.Settings.CustomThemes[existingIndex].Id;
            state.Settings.CustomThemes[existingIndex] = saved;
        }
        else state.Settings.CustomThemes.Add(saved);

        AppThemeCatalog.ConfigureCustomThemes(state.Settings.CustomThemes);
        state.Settings.ApplicationTheme = saved.Id;
        customThemePreviewOriginal = null;
        customThemeDraft = null;
        editingCustomThemeId = null;
        CustomThemeEditorPanel.Visibility = Visibility.Collapsed;
        RefreshThemeList(saved.Id);
        ApplySelectedApplicationTheme(AppThemeCatalog.Resolve(saved.Id));
        UpdateStatus($"{saved.Name} custom theme saved and applied");
    }

    private void DeleteCustomThemeClick(object sender, RoutedEventArgs e)
    {
        if (SettingsThemeList.SelectedItem is not AppThemeDefinition { IsCustom: true } selected) return;
        if (!PowerShellPlusDialog.Confirm(this,
                $"Delete the custom theme {selected.Name}? This cannot be undone.",
                "Delete custom theme", PowerShellPlusDialogKind.Warning,
                "Delete", "Cancel", defaultToPrimary: false, primaryIsDangerous: true)) return;
        state.Settings.CustomThemes.RemoveAll(value => string.Equals(value.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        var wasActive = string.Equals(state.Settings.ApplicationTheme, selected.Id, StringComparison.OrdinalIgnoreCase);
        if (wasActive) state.Settings.ApplicationTheme = AppThemeCatalog.DefaultThemeId;
        AppThemeCatalog.ConfigureCustomThemes(state.Settings.CustomThemes);
        RefreshThemeList(state.Settings.ApplicationTheme);
        if (wasActive) ApplySelectedApplicationTheme(AppThemeCatalog.Resolve(state.Settings.ApplicationTheme));
        ScheduleSave();
        UpdateStatus($"{selected.Name} theme deleted");
    }

    private void OpenThemeBackgroundColorClick(object sender, RoutedEventArgs e) => OpenCustomThemeColorPicker(AccentColorPickerTarget.ThemeBackground);
    private void OpenThemeSurfaceColorClick(object sender, RoutedEventArgs e) => OpenCustomThemeColorPicker(AccentColorPickerTarget.ThemeSurface);
    private void OpenThemeAccentColorClick(object sender, RoutedEventArgs e) => OpenCustomThemeColorPicker(AccentColorPickerTarget.ThemeAccent);
    private void OpenThemeTextColorClick(object sender, RoutedEventArgs e) => OpenCustomThemeColorPicker(AccentColorPickerTarget.ThemeText);
    private void OpenThemeGradientEndColorClick(object sender, RoutedEventArgs e) => OpenCustomThemeColorPicker(AccentColorPickerTarget.ThemeGradientEnd);

    private void OpenCustomThemeColorPicker(AccentColorPickerTarget target)
    {
        if (customThemeDraft is null) return;
        var value = target switch
        {
            AccentColorPickerTarget.ThemeBackground => customThemeDraft.Background,
            AccentColorPickerTarget.ThemeSurface => customThemeDraft.Surface,
            AccentColorPickerTarget.ThemeText => customThemeDraft.Text,
            AccentColorPickerTarget.ThemeGradientEnd => customThemeDraft.GradientEnd,
            _ => customThemeDraft.Accent
        };
        OpenAccentColorPicker(target, value);
    }

    private void SetCustomThemeColor(AccentColorPickerTarget target, string value)
    {
        if (customThemeDraft is null) return;
        switch (target)
        {
            case AccentColorPickerTarget.ThemeBackground: customThemeDraft.Background = value; break;
            case AccentColorPickerTarget.ThemeSurface: customThemeDraft.Surface = value; break;
            case AccentColorPickerTarget.ThemeText: customThemeDraft.Text = value; break;
            case AccentColorPickerTarget.ThemeGradientEnd: customThemeDraft.GradientEnd = value; break;
            default: customThemeDraft.Accent = value; break;
        }
        UpdateCustomThemeEditorVisuals();
    }

    private void ThemeGradientChanged(object sender, RoutedEventArgs e)
    {
        if (customThemeDraft is null) return;
        customThemeDraft.UseGradient = ThemeGradientEnabled.IsChecked == true;
        UpdateCustomThemeEditorVisuals();
    }

    private void ThemeGradientDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (customThemeDraft is null || ThemeGradientDirection.SelectedItem is not ComboBoxItem item) return;
        customThemeDraft.GradientDirection = AppThemeCatalog.NormalizeDirection(item.Content?.ToString());
        ApplyCustomThemeDraftPreview();
    }

    private void UpdateCustomThemeEditorVisuals()
    {
        if (customThemeDraft is null) return;
        ThemeBackgroundSwatch.Background = AppThemeCatalog.Brush(customThemeDraft.Background);
        ThemeSurfaceSwatch.Background = AppThemeCatalog.Brush(customThemeDraft.Surface);
        ThemeAccentSwatch.Background = AppThemeCatalog.Brush(customThemeDraft.Accent);
        ThemeTextSwatch.Background = AppThemeCatalog.Brush(customThemeDraft.Text);
        ThemeGradientEndSwatch.Background = AppThemeCatalog.Brush(customThemeDraft.GradientEnd);
        ThemeBackgroundHex.Text = customThemeDraft.Background;
        ThemeSurfaceHex.Text = customThemeDraft.Surface;
        ThemeAccentHex.Text = customThemeDraft.Accent;
        ThemeTextHex.Text = customThemeDraft.Text;
        ThemeGradientEndHex.Text = customThemeDraft.GradientEnd;
        ThemeGradientEnabled.IsChecked = customThemeDraft.UseGradient;
        ThemeGradientOptions.Visibility = customThemeDraft.UseGradient ? Visibility.Visible : Visibility.Collapsed;
        ThemeGradientDirection.SelectedIndex = customThemeDraft.GradientDirection switch
        {
            "Horizontal" => 1,
            "Vertical" => 2,
            "Reverse diagonal" => 3,
            _ => 0
        };
        ApplyCustomThemeDraftPreview();
    }

    private void ApplyCustomThemeDraftPreview()
    {
        if (customThemeDraft is null) return;
        ApplyApplicationThemeVisuals(AppThemeCatalog.CreateCustomTheme(customThemeDraft));
    }

    private void ApplyApplicationThemeVisuals(AppThemeDefinition theme)
    {
        AppThemeCatalog.Apply(theme);
        var appearance = EffectiveAppearance(theme);
        foreach (var pane in panes.Values) pane.ApplyAppearance(appearance);
        InvalidateVisual();
    }

    private void ApplySelectedApplicationTheme(AppThemeDefinition theme)
    {
        ApplyApplicationThemeVisuals(theme);
        ScheduleSave();
    }

    private void PopulateSettingsUi()
    {
        settingsUiReady = false;
        var settings = state.Settings;
        RefreshThemeList(settings.ApplicationTheme);
        SettingsFontFace.Text = settings.FontFace ?? string.Empty;
        SettingsFontSize.ItemsSource = new[] { "Windows Terminal default" }.Concat(Enumerable.Range(8, 25).Select(size => size.ToString(CultureInfo.InvariantCulture))).ToList();
        SettingsFontSize.SelectedIndex = settings.FontSize is int size && size >= 8 && size <= 32 ? size - 7 : 0;
        SettingsCursorStyle.SelectedIndex = settings.CursorStyle switch { "Block" => 1, "Underline" => 2, _ => 0 };
        SettingsCursorBlink.IsChecked = settings.CursorBlink;
        SettingsDefaultShell.Text = settings.DefaultCommandLine ?? string.Empty;
        SettingsDefaultDirectory.Text = settings.DefaultWorkingDirectory ?? string.Empty;
        SettingsAutomaticTerminalColors.IsChecked = settings.AutomaticallySetTerminalColor;
        SettingsConfirmRemove.IsChecked = settings.ConfirmBeforeRemove;
        SettingsKeepSessionsInTray.IsChecked = settings.KeepSessionsRunningInTray;
        SettingsRestoreAfterRestart.IsChecked = settings.RestoreSessionsAfterRestart;
        SettingsSaveTranscripts.IsChecked = settings.SaveTerminalTranscripts;
        SettingsSendAllModifierEnabled.IsChecked = settings.SendToAllModifierEnabled;
        SettingsSendAllModifier.SelectedIndex = settings.SendToAllModifier == "Alt" ? 1 : 0;
        SettingsSendAllModifier.IsEnabled = settings.SendToAllModifierEnabled;
        SettingsAutomaticUpdates.IsChecked = settings.CheckForUpdatesAutomatically;
        SettingsAgentNotifications.IsChecked = settings.AgentNotificationsEnabled;
        SettingsNotificationSound.SelectedIndex = settings.NotificationSound switch { "Custom" => 1, "Silent" => 2, _ => 0 };
        SettingsCustomSoundPath.Text = settings.CustomNotificationSoundPath ?? string.Empty;
        SettingsTmuxToggleWarning.IsChecked = settings.ShowTmuxToggleWarning;
        UpdateNotificationSettingsVisibility();
        SettingsInstalledVersion.Text = $"PowerShellPlus {ApplicationUpdater.CurrentVersionText}";
        SettingsUpdateStatus.Text = settings.CheckForUpdatesAutomatically
            ? "Automatic update notifications are enabled."
            : "Automatic notifications are off. Manual checks still work.";
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

    private void SettingsThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomThemeButtons();
        if (!settingsUiReady || SettingsThemeList.SelectedItem is not AppThemeDefinition theme) return;
        if (string.Equals(state.Settings.ApplicationTheme, theme.Id, StringComparison.OrdinalIgnoreCase)) return;
        state.Settings.ApplicationTheme = theme.Id;
        ApplySelectedApplicationTheme(theme);
        UpdateStatus($"{theme.Name} theme applied");
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

    private void SettingsAutomaticTerminalColorsChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.AutomaticallySetTerminalColor = SettingsAutomaticTerminalColors.IsChecked == true;
        ScheduleSave();
        UpdateStatus(state.Settings.AutomaticallySetTerminalColor
            ? "New terminals will receive a random color"
            : "New terminals will use the default color");
    }

    private void SettingsBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.ConfirmBeforeRemove = SettingsConfirmRemove.IsChecked == true;
        state.Settings.KeepSessionsRunningInTray = SettingsKeepSessionsInTray.IsChecked == true;
        state.Settings.RestoreSessionsAfterRestart = SettingsRestoreAfterRestart.IsChecked == true;
        state.Settings.SaveTerminalTranscripts = SettingsSaveTranscripts.IsChecked == true;
        if (state.Settings.RestoreSessionsAfterRestart && !automationMode && terminalStartupRecoverySettled) recoveryTimer.Start(); else recoveryTimer.Stop();
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

    private void SettingsNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.AgentNotificationsEnabled = SettingsAgentNotifications.IsChecked == true;
        state.Settings.NotificationSound = SettingsNotificationSound.SelectedIndex switch { 1 => "Custom", 2 => "Silent", _ => "System" };
        state.Settings.ShowTmuxToggleWarning = SettingsTmuxToggleWarning.IsChecked == true;
        UpdateNotificationSettingsVisibility();
        ScheduleSave();
        UpdateStatus(state.Settings.AgentNotificationsEnabled ? "Agent notifications enabled" : "Agent notifications disabled");
    }

    private void UpdateNotificationSettingsVisibility()
    {
        if (SettingsCustomSoundPanel is null) return;
        SettingsCustomSoundPanel.Visibility = SettingsNotificationSound.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsTestNotificationButton.IsEnabled = SettingsAgentNotifications.IsChecked == true;
    }

    private void SettingsBrowseNotificationSoundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a notification sound",
            Filter = "Audio files|*.wav;*.mp3;*.wma;*.m4a|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        state.Settings.CustomNotificationSoundPath = dialog.FileName;
        state.Settings.NotificationSound = "Custom";
        SettingsCustomSoundPath.Text = dialog.FileName;
        SettingsNotificationSound.SelectedIndex = 1;
        UpdateNotificationSettingsVisibility();
        ScheduleSave();
        PlayConfiguredNotificationSound();
    }

    private void SettingsTestNotificationClick(object sender, RoutedEventArgs e)
    {
        var profile = activePane?.Profile ?? state.Sessions.FirstOrDefault();
        if (profile is null) return;
        ShowAgentNotification(profile, AgentKind.Codex, AgentActivityState.Waiting, test: true);
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
