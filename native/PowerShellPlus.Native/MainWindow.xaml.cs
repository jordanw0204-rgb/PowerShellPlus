using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PowerShellPlus.Native;

public partial class MainWindow : Window
{
    private enum EditorMode { Session, Snippet, Automation }
    private readonly WindowsTerminalProfile terminalProfile;
    private readonly WorkspaceState state;
    private readonly Dictionary<string, TerminalPane> panes = [];
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer automationTimer;
    private readonly DispatcherTimer recoveryTimer;
    private readonly bool automationMode;
    private readonly SessionRecoverySnapshot loadedRecovery;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private bool explicitShutdown;
    private bool shutdownComplete;
    private bool trayNoticeShown;
    private EditorMode editorMode;
    private object? editingValue;
    private TerminalPane? activePane;
    private string? activeLayoutSizeKey;
    private bool automationCheckRunning;

    public MainWindow(bool automationMode = false)
    {
        this.automationMode = automationMode;
        terminalProfile = WindowsTerminalProfile.Load();
        state = WorkspaceStore.Load(terminalProfile);
        loadedRecovery = automationMode || !state.Settings.RestoreSessionsAfterRestart ? new SessionRecoverySnapshot() : SessionRecoveryStore.Load();
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart) ReconcileCodexRecovery();
        InitializeComponent();
        SessionList.ItemsSource = state.Sessions;
        SnippetList.ItemsSource = state.Snippets;
        AutomationList.ItemsSource = state.Automations;
        InitializeAutomationTimeUi();
        PopulateSettingsUi();

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        automationTimer.Tick += async (_, _) => { RefreshAutomationCountdowns(); await CheckAutomationsAsync(); };
        automationTimer.Start();
        recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        recoveryTimer.Tick += (_, _) => CaptureRecoverySnapshot();
        if (!automationMode && state.Settings.RestoreSessionsAfterRestart) recoveryTimer.Start();

        foreach (var profile in state.Sessions) CreatePane(profile);
        var activeId = state.ActiveSessionId ?? state.Sessions.FirstOrDefault()?.Id;
        if (activeId is not null) SelectPane(activeId, false);
        ApplyLayout();
        UpdateStatus("Native Windows Terminal renderer ready");
        Closing += WindowClosing;
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
        saveTimer.Stop();
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

    private void CaptureRecoverySnapshot()
    {
        if (automationMode || !state.Settings.RestoreSessionsAfterRestart || shutdownComplete) return;
        try
        {
            var previous = SessionRecoveryStore.Load();
            var snapshot = new SessionRecoverySnapshot();
            var usedCodexSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pane in panes.Values)
            {
                previous.Sessions.TryGetValue(pane.Profile.Id, out var oldEntry);
                var transcriptFile = state.Settings.SaveTerminalTranscripts
                    ? SessionRecoveryStore.SaveTranscript(pane.Profile.Id, pane.GetOutput()) ?? oldEntry?.TranscriptFile
                    : null;
                var codex = pane.GetCodexProcessState();
                var launch = CodexLaunchStore.Load(pane.Profile.Id);
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
                if (codexSessionId is null && launch?.IsActive == true && CodexSessionLocator.IsSafeCodexId(launch.SessionId))
                {
                    codexSessionId = launch.SessionId;
                    codexDirectory = launch.WorkingDirectory;
                    codexModel = launch.Model;
                    codexSandboxMode = launch.SandboxMode;
                    codexApprovalPolicy = launch.ApprovalPolicy;
                }
                if (codexSessionId is null && codexIsActive && launch is null && oldEntry?.CodexWasActive == true && CodexSessionLocator.IsSafeCodexId(oldEntry.CodexSessionId))
                {
                    codexSessionId = oldEntry.CodexSessionId;
                    codexDirectory = oldEntry.WorkingDirectory;
                    codexModel = oldEntry.CodexModel;
                    codexSandboxMode = oldEntry.CodexSandboxMode;
                    codexApprovalPolicy = oldEntry.CodexApprovalPolicy;
                }
                if (codexMatch is not null && launch?.IsActive == true) CodexLaunchStore.Confirm(launch, codexMatch);
                if (codexSessionId is not null)
                {
                    usedCodexSessionIds.Add(codexSessionId);
                    codexModel = (codexMatch is null ? CodexSessionLocator.FindLatestModel(codexSessionId)?.Model : codexMatch.Model)
                        ?? codexModel ?? oldEntry?.CodexModel;
                    var latestPermissions = codexMatch is not null && CodexSessionLocator.IsSafeCodexPermissions(codexMatch.SandboxMode, codexMatch.ApprovalPolicy)
                        ? new CodexSessionPermissions(codexMatch.SandboxMode!, codexMatch.ApprovalPolicy!, codexMatch.FileModifiedUtc)
                        : CodexSessionLocator.FindLatestPermissions(codexSessionId);
                    if (latestPermissions is not null)
                    {
                        codexSandboxMode = latestPermissions.SandboxMode;
                        codexApprovalPolicy = latestPermissions.ApprovalPolicy;
                    }
                    else if (!CodexSessionLocator.IsSafeCodexPermissions(codexSandboxMode, codexApprovalPolicy)
                        && CodexSessionLocator.IsSafeCodexPermissions(oldEntry?.CodexSandboxMode, oldEntry?.CodexApprovalPolicy))
                    {
                        codexSandboxMode = oldEntry!.CodexSandboxMode;
                        codexApprovalPolicy = oldEntry.CodexApprovalPolicy;
                    }
                }
                snapshot.Sessions[pane.Profile.Id] = new SessionRecoveryEntry
                {
                    SessionId = pane.Profile.Id,
                    WorkingDirectory = codexDirectory ?? (codexIsActive && launch is not null ? launch.WorkingDirectory : pane.Profile.WorkingDirectory),
                    TranscriptFile = transcriptFile,
                    CodexWasActive = codexIsActive,
                    CodexSessionId = codexSessionId,
                    CodexModel = CodexSessionLocator.IsSafeCodexModel(codexModel) ? codexModel : null,
                    CodexSandboxMode = CodexSessionLocator.IsSafeCodexPermissions(codexSandboxMode, codexApprovalPolicy) ? codexSandboxMode : null,
                    CodexApprovalPolicy = CodexSessionLocator.IsSafeCodexPermissions(codexSandboxMode, codexApprovalPolicy) ? codexApprovalPolicy : null,
                    CapturedUtc = DateTime.UtcNow
                };
            }
            SessionRecoveryStore.Save(snapshot);
        }
        catch (Exception exception)
        {
            try
            {
                Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
                File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] Recovery snapshot: {exception}\n");
            }
            catch { }
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
                    || !string.Equals(entry.CodexApprovalPolicy, latestPermissions.ApprovalPolicy, StringComparison.Ordinal)))
                {
                    entry.CodexSandboxMode = latestPermissions.SandboxMode;
                    entry.CodexApprovalPolicy = latestPermissions.ApprovalPolicy;
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
            }
            else if (CodexSessionLocator.IsSafeCodexPermissions(match?.SandboxMode, match?.ApprovalPolicy))
            {
                entry.CodexSandboxMode = match!.SandboxMode;
                entry.CodexApprovalPolicy = match.ApprovalPolicy;
            }
            else if (CodexSessionLocator.IsSafeCodexPermissions(launch.SandboxMode, launch.ApprovalPolicy))
            {
                entry.CodexSandboxMode = launch.SandboxMode;
                entry.CodexApprovalPolicy = launch.ApprovalPolicy;
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

    private void CreatePane(SessionProfile profile)
    {
        loadedRecovery.Sessions.TryGetValue(profile.Id, out var recovery);
        var previousOutput = state.Settings.SaveTerminalTranscripts ? SessionRecoveryStore.ReadTranscript(recovery) : string.Empty;
        var pane = new TerminalPane(profile, EffectiveAppearance(), recovery, previousOutput,
            () => state.Snippets, ScheduleSave, SendCommandToAllAsync,
            () => state.Settings.SendToAllModifierEnabled, () => SendToAllModifier);
        // A native terminal click already gives its HWND keyboard focus. Only
        // update application selection here so WPF does not steal that focus.
        pane.Activated += (_, _) => SelectPane(profile.Id, false);
        pane.CloseRequested += (_, _) => RemoveSession(profile);
        pane.EditRequested += (_, _) => OpenSessionEditor(profile);
        panes[profile.Id] = pane;
    }

    private void SelectPane(string sessionId, bool focus = true)
    {
        if (!panes.TryGetValue(sessionId, out var pane)) return;
        activePane = pane;
        state.ActiveSessionId = sessionId;
        foreach (var value in panes.Values) value.SetActive(value == pane);
        SessionList.SelectedItem = pane.Profile;
        if (state.Layout == "Focus") ApplyLayout();
        if (focus) pane.Focus();
        ScheduleSave();
    }

    private void ApplyLayout()
    {
        CaptureLayoutSizing();
        TerminalHost.Children.Clear(); TerminalHost.RowDefinitions.Clear(); TerminalHost.ColumnDefinitions.Clear();
        var ordered = state.Sessions.Where(value => panes.ContainsKey(value.Id)).Select(value => panes[value.Id]).ToList();
        activeLayoutSizeKey = null;
        if (ordered.Count == 0) { UpdateCounts(); return; }
        foreach (var pane in ordered) pane.Visibility = Visibility.Visible;

        if (state.Layout == "Focus")
        {
            TerminalHost.RowDefinitions.Add(new RowDefinition()); TerminalHost.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (var pane in ordered) if (pane != activePane) pane.Visibility = Visibility.Collapsed;
            if (activePane is not null) TerminalHost.Children.Add(activePane);
        }
        else
        {
            int columns, rows;
            if (state.Layout == "Rows") { columns = 1; rows = ordered.Count; }
            else if (state.Layout == "Columns") { columns = ordered.Count; rows = 1; }
            else { columns = (int)Math.Ceiling(Math.Sqrt(ordered.Count)); rows = (int)Math.Ceiling((double)ordered.Count / columns); }
            activeLayoutSizeKey = $"{state.Layout}:{ordered.Count}:{rows}x{columns}";
            state.LayoutSizes.TryGetValue(activeLayoutSizeKey, out var savedSizing);
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
        UpdateCounts(); ScheduleSave();
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
        state.LayoutSizes[activeLayoutSizeKey] = new PaneLayoutSizing
        {
            Rows = TerminalHost.RowDefinitions.Where((_, index) => index % 2 == 0).Select(value => Math.Max(1, value.ActualHeight)).ToList(),
            Columns = TerminalHost.ColumnDefinitions.Where((_, index) => index % 2 == 0).Select(value => Math.Max(1, value.ActualWidth)).ToList()
        };
    }

    private void SetLayout(string layout) { CaptureLayoutSizing(); state.Layout = layout; ApplyLayout(); UpdateStatus($"{layout} layout - drag the dividers to resize panes"); }

    private void OpenSessionEditor(SessionProfile? profile)
    {
        editorMode = EditorMode.Session; editingValue = profile;
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
        SessionEditor.Visibility = Visibility.Collapsed; SnippetEditor.Visibility = Visibility.Collapsed; AutomationEditor.Visibility = Visibility.Collapsed;
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

    private async void SaveEditorClick(object sender, RoutedEventArgs e)
    {
        if (editorMode == EditorMode.Session)
        {
            if (string.IsNullOrWhiteSpace(SessionNameEdit.Text) || string.IsNullOrWhiteSpace(SessionCommandEdit.Text) || !Directory.Exists(SessionDirectoryEdit.Text)) { UpdateStatus("Session fields are incomplete"); return; }
            if (editingValue is SessionProfile existing)
            {
                existing.Name = SessionNameEdit.Text.Trim(); existing.CommandLine = SessionCommandEdit.Text.Trim(); existing.WorkingDirectory = SessionDirectoryEdit.Text.Trim(); existing.AutoStart = SessionAutoStartEdit.IsChecked == true;
                activePane = panes[existing.Id]; activePane.ApplyProfile(existing); await activePane.RestartAsync(); SessionList.Items.Refresh();
            }
            else
            {
                var created = new SessionProfile { Name = SessionNameEdit.Text.Trim(), CommandLine = SessionCommandEdit.Text.Trim(), WorkingDirectory = SessionDirectoryEdit.Text.Trim(), AutoStart = SessionAutoStartEdit.IsChecked == true };
                state.Sessions.Add(created); CreatePane(created); SelectPane(created.Id, false); ApplyLayout();
            }
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

    private void RemoveSession(SessionProfile profile)
    {
        if (state.Settings.ConfirmBeforeRemove && MessageBox.Show(this, $"Remove {profile.Name}?", "PowerShellPlus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var pane = panes[profile.Id]; pane.Stop(); TerminalHost.Children.Remove(pane); panes.Remove(profile.Id); state.Sessions.Remove(profile); SessionRecoveryStore.DeleteSession(profile.Id);
        activePane = panes.Values.FirstOrDefault(); if (activePane is not null) SelectPane(activePane.Profile.Id, false); ApplyLayout(); ScheduleSave();
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
    private void UpdateCounts() => CountText.Text = $"{panes.Count} native terminal{(panes.Count == 1 ? string.Empty : "s")} · {terminalProfile.SchemeName}";

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

        var rootBefore = pane.GetRootProcessId();
        var workspaceTestIsolated = automationMode && WorkspaceStore.DirectoryOverride is not null
            && !Path.GetFullPath(WorkspaceStore.DirectoryPath).Equals(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerShellPlus")), StringComparison.OrdinalIgnoreCase);
        var profile = new SessionProfile { CommandLine = "powershell.exe", WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var normalScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = false }));
        const string savedModel = "gpt-5.3-codex-spark";
        const string savedSandboxMode = "danger-full-access";
        const string savedApprovalPolicy = "never";
        var codexScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = savedModel, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy }));
        var pickerScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true }));
        var unsafeModelScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexModel = "gpt'; Write-Output unsafe; #" }));
        var unsafePermissionsScript = TerminalPane.DecodePowerShellStartupScript(TerminalPane.BuildCommandLine(profile, new SessionRecoveryEntry { CodexWasActive = true, CodexSessionId = "11111111-2222-3333-4444-555555555555", CodexSandboxMode = "danger-full-access'; Write-Output unsafe; #", CodexApprovalPolicy = savedApprovalPolicy }));
        var normalDoesNotResumeCodex = !normalScript.Contains("codex resume", StringComparison.OrdinalIgnoreCase);
        var codexResumesExactSession = codexScript.Contains("codex resume '11111111-2222-3333-4444-555555555555'", StringComparison.OrdinalIgnoreCase);
        var codexResumesSavedModel = codexScript.Contains($"--model '{savedModel}'", StringComparison.Ordinal);
        var codexResumesSavedPermissions = codexScript.Contains($"--sandbox '{savedSandboxMode}' --ask-for-approval '{savedApprovalPolicy}'", StringComparison.Ordinal);
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
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "codex-recovery-fixture");
        Directory.CreateDirectory(fixtureRoot);
        var fixtureId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var fixtureStarted = DateTime.UtcNow;
        var actualCodexDirectory = Path.GetDirectoryName(reportPath)!;
        var fixture = new { timestamp = fixtureStarted.ToString("O"), type = "session_meta", payload = new { session_id = fixtureId, id = Guid.NewGuid().ToString(), timestamp = fixtureStarted.ToString("O"), cwd = actualCodexDirectory } };
        var earlierTurn = new { timestamp = fixtureStarted.AddSeconds(1).ToString("O"), type = "turn_context", payload = new { model = "gpt-5.2-codex", approval_policy = "on-request", sandbox_policy = new { type = "workspace-write", network_access = false } } };
        var modelChange = new { timestamp = fixtureStarted.AddSeconds(2).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { model = savedModel } } };
        var permissionsChange = new { timestamp = fixtureStarted.AddSeconds(3).ToString("O"), type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { active_permission_profile = new { id = ":danger-full-access" }, approval_policy = savedApprovalPolicy } } };
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
            && latestPermissions?.SandboxMode == savedSandboxMode && latestPermissions.ApprovalPolicy == savedApprovalPolicy;
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
            && confirmedLaunch.SandboxMode == savedSandboxMode && confirmedLaunch.ApprovalPolicy == savedApprovalPolicy;
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
            && wrapperScript.Contains("EndedUtc", StringComparison.Ordinal);
        try { Directory.Delete(launchRoot, true); } catch { }
        var recoveryRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "session-recovery-fixture");
        var transcriptFile = SessionRecoveryStore.SaveTranscript("test-session", "previous terminal output", recoveryRoot);
        var recoveryFixture = new SessionRecoverySnapshot();
        recoveryFixture.Sessions["test-session"] = new SessionRecoveryEntry { SessionId = "test-session", TranscriptFile = transcriptFile, CodexWasActive = true, CodexSessionId = fixtureId, CodexModel = savedModel, CodexSandboxMode = savedSandboxMode, CodexApprovalPolicy = savedApprovalPolicy };
        SessionRecoveryStore.Save(recoveryFixture, recoveryRoot);
        var reloadedFixture = SessionRecoveryStore.Load(recoveryRoot);
        var recoveryRoundTrip = reloadedFixture.Sessions.TryGetValue("test-session", out var reloadedEntry)
            && reloadedEntry.CodexWasActive && reloadedEntry.CodexSessionId == fixtureId && reloadedEntry.CodexModel == savedModel
            && reloadedEntry.CodexSandboxMode == savedSandboxMode && reloadedEntry.CodexApprovalPolicy == savedApprovalPolicy
            && SessionRecoveryStore.ReadTranscript(reloadedEntry, recoveryRoot) == "previous terminal output";
        try { Directory.Delete(recoveryRoot, true); } catch { }
        var legacyRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "legacy-recovery-fixture");
        var legacyFixture = new SessionRecoverySnapshot { Version = 1 };
        legacyFixture.Sessions["legacy-session"] = new SessionRecoveryEntry { SessionId = "legacy-session", CodexWasActive = true, CodexSessionId = "99999999-8888-7777-6666-555555555555", WorkingDirectory = profile.WorkingDirectory };
        SessionRecoveryStore.Save(legacyFixture, legacyRoot);
        var migratedLegacy = SessionRecoveryStore.Load(legacyRoot);
        var unsafeLegacyIdDiscarded = migratedLegacy.Version == 5 && migratedLegacy.Sessions["legacy-session"].CodexSessionId is null;
        try { Directory.Delete(legacyRoot, true); } catch { }

        HideToTray();
        await Task.Delay(300);
        var hidden = !IsVisible;
        var rootWhileHidden = pane.GetRootProcessId();
        RestoreWindow(false);
        await Task.Delay(300);
        var restored = IsVisible;
        var rootAfter = pane.GetRootProcessId();
        var sameLiveProcess = rootBefore is not null && rootBefore == rootWhileHidden && rootBefore == rootAfter;
        var success = workspaceTestIsolated && hidden && restored && sameLiveProcess && normalDoesNotResumeCodex && codexResumesExactSession && codexResumesSavedModel && codexResumesSavedPermissions && unsafeModelRejected && unsafePermissionsRejected && ambiguousCodexUsesPicker && powershellWrapperInstalled && codexSessionMapped && latestModelMapped && latestPermissionsMapped && partialRolloutIgnored && changedDirectoryRestored && inTuiResumeRebound && liveRolloutSharedRead && launchTimeFallbackRebound && exactLaunchBindingPersisted && normalCodexExitRecorded && wrapperRecordsPaneAndLifecycle && recoveryRoundTrip && unsafeLegacyIdDiscarded;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Live panes survived hide/restore and recovery commands resumed Codex with its exact thread, saved model, and saved permission level.\nWorkspaceTestIsolated={workspaceTestIsolated}\nHidden={hidden}\nRestored={restored}\nSameLiveProcess={sameLiveProcess}\nNormalDoesNotResumeCodex={normalDoesNotResumeCodex}\nCodexResumesExactSession={codexResumesExactSession}\nCodexResumesSavedModel={codexResumesSavedModel}\nCodexResumesSavedPermissions={codexResumesSavedPermissions}\nUnsafeModelRejected={unsafeModelRejected}\nUnsafePermissionsRejected={unsafePermissionsRejected}\nAmbiguousCodexUsesPicker={ambiguousCodexUsesPicker}\nPowerShellWrapperInstalled={powershellWrapperInstalled}\nCodexSessionMappedAcrossChangedDirectory={codexSessionMapped}\nLatestModelMapped={latestModelMapped}\nLatestPermissionsMapped={latestPermissionsMapped}\nPartialRolloutIgnored={partialRolloutIgnored}\nChangedDirectoryRestored={changedDirectoryRestored}\nInTuiResumeRebound={inTuiResumeRebound}\nLiveRolloutSharedRead={liveRolloutSharedRead}\nLaunchTimeFallbackRebound={launchTimeFallbackRebound}\nExactLaunchBindingPersisted={exactLaunchBindingPersisted}\nNormalCodexExitRecorded={normalCodexExitRecorded}\nWrapperRecordsPaneAndLifecycle={wrapperRecordsPaneAndLifecycle}\nRecoveryRoundTrip={recoveryRoundTrip}\nUnsafeLegacyIdDiscarded={unsafeLegacyIdDiscarded}");
        return success;
    }

    public async Task<bool> RunMultiPaneSmokeTestAsync(string reportPath)
    {
        var originalLayout = state.Layout;
        var originalSendAllEnabled = state.Settings.SendToAllModifierEnabled;
        var originalSendAllModifier = state.Settings.SendToAllModifier;
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
                added.Add(profile); state.Sessions.Add(profile); CreatePane(profile);
            }
            quickAccessFixture = new CommandSnippet { Name = "Queue smoke", Category = "Test", Command = "Write-Output 'QUICK_ACCESS_READY'", ShowInQuickAccess = true };
            state.Snippets.Add(quickAccessFixture);
            SetLayout("Grid");
            await Task.Delay(2600);
            var originalWindowWidth = Width;
            var root = (FrameworkElement)Content;
            root.UpdateLayout();
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
            var terminalSurfaceHooked = activationTarget.HasTerminalSurfaceActivationHook;
            var terminalClickSent = activationTarget.SimulateTerminalSurfaceClickForTest();
            var terminalSurfaceActivatesPane = terminalClickSent && ReferenceEquals(activePane, activationTarget)
                && ReferenceEquals(SessionList.SelectedItem, activationTarget.Profile);
            var terminalSurfaceTakesKeyboardFocus = activationTarget.HasNativeKeyboardFocus();
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
            state.Settings.SendToAllModifier = "Ctrl";
            var modifierCanBeRemapped = activationTarget.SendToAllActiveForTest(ModifierKeys.Control)
                && !activationTarget.SendToAllActiveForTest(ModifierKeys.Shift);
            WorkspaceStore.Save(state);
            var sendAllSettingsPersist = WorkspaceStore.Load(terminalProfile).Settings is { SendToAllModifierEnabled: true, SendToAllModifier: "Ctrl" };
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

            var paneCommandSystem = paneCommandInputTakesFocus && commandBarCollapses && commandBarStatePersists && commandBarExpands && queueAddsCommands && queueStatePersists && currentCommandRuns
                && nextQueuedCommandPromoted && upArrowBrowsesQueue && firstQueuedCommandRuns && queueAdvances && secondQueuedCommandRuns && queueDrains
                && quickAccessFiltersCommands && quickAccessTogglePersists && quickAccessPopulatesInput && queueCommandsExecuted && queueMenuListsCommands
                && shiftModifierRoutesAll && sendAllVisualFeedback && modifierCanBeDisabled && modifierCanBeRemapped && sendAllSettingsPersist && commandReachedAllPanes;
            var titleLayoutControlsCentered = initiallyCentered && centeredAfterResize;
            var success = inputReady && outputReady && scrollbarsHidden && titleLayoutControlsCentered && terminalSurfaceHooked && terminalSurfaceActivatesPane && terminalSurfaceTakesKeyboardFocus && windowIconLoaded && executableIconEmbedded && rows && columns && focus && grid && scheduleLogic && countdownLogic && automationHoverContainerStable && paneCommandSystem;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, $"{(success ? "PASS" : "FAIL")} Native panes accepted pane-local command input and queues, modifier-routed commands to all panes, resized in every layout, and preserved scheduler behavior.\nInputReady={inputReady}\nOutputReady={outputReady}\nScrollbarsHidden={scrollbarsHidden}\nTitleLayoutControlsCentered={titleLayoutControlsCentered}\nPaneCommandInputTakesFocus={paneCommandInputTakesFocus}\nTerminalSurfaceHooked={terminalSurfaceHooked}\nTerminalSurfaceActivatesPane={terminalSurfaceActivatesPane}\nTerminalSurfaceTakesKeyboardFocus={terminalSurfaceTakesKeyboardFocus}\nCommandBarCollapses={commandBarCollapses}\nCommandBarStatePersists={commandBarStatePersists}\nCommandBarExpands={commandBarExpands}\nQueueAddsCommands={queueAddsCommands}\nQueueMenuListsCommands={queueMenuListsCommands}\nQueueStatePersists={queueStatePersists}\nCurrentCommandRuns={currentCommandRuns}\nNextQueuedCommandPromoted={nextQueuedCommandPromoted}\nUpArrowBrowsesQueue={upArrowBrowsesQueue}\nQueueAdvances={queueAdvances}\nQueueDrains={queueDrains}\nQuickAccessFiltersCommands={quickAccessFiltersCommands}\nQuickAccessTogglePersists={quickAccessTogglePersists}\nQuickAccessPopulatesInput={quickAccessPopulatesInput}\nQueueCommandsExecuted={queueCommandsExecuted}\nShiftModifierRoutesAll={shiftModifierRoutesAll}\nSendAllVisualFeedback={sendAllVisualFeedback}\nModifierCanBeDisabled={modifierCanBeDisabled}\nModifierCanBeRemapped={modifierCanBeRemapped}\nSendAllSettingsPersist={sendAllSettingsPersist}\nCommandReachedAllPanes={commandReachedAllPanes}\nWindowIconLoaded={windowIconLoaded}\nExecutableIconEmbedded={executableIconEmbedded}\nGrid={grid}\nRows={rows}\nColumns={columns}\nFocus={focus}\nExactSchedules={scheduleLogic}\nCountdownFormatting={countdownLogic}\nAutomationHoverContainerStable={automationHoverContainerStable}");
            return success;
        }
        finally
        {
            if (countdownRefreshFixture is not null) state.Automations.Remove(countdownRefreshFixture);
            if (quickAccessFixture is not null) state.Snippets.Remove(quickAccessFixture);
            state.Settings.SendToAllModifierEnabled = originalSendAllEnabled;
            state.Settings.SendToAllModifier = originalSendAllModifier;
            foreach (var profile in added) { panes[profile.Id].Stop(); TerminalHost.Children.Remove(panes[profile.Id]); panes.Remove(profile.Id); state.Sessions.Remove(profile); }
            state.Layout = originalLayout; activePane = panes.Values.FirstOrDefault(); if (activePane is not null) SelectPane(activePane.Profile.Id, false); ApplyLayout();
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
    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void SessionsSectionClick(object sender, RoutedEventArgs e) => ShowSection(SessionsPanel);
    private void CommandsSectionClick(object sender, RoutedEventArgs e) => ShowSection(CommandsPanel);
    private void AutomationSectionClick(object sender, RoutedEventArgs e) => ShowSection(AutomationPanel);
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs e) { if (SessionList.SelectedItem is SessionProfile value) SelectPane(value.Id, false); }
    private void NewSessionClick(object sender, RoutedEventArgs e) => OpenSessionEditor(null);
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
        var current = state.Sessions.IndexOf(value); var target = current + offset;
        if (current < 0 || target < 0 || target >= state.Sessions.Count) return;
        state.Sessions.Move(current, target); ApplyLayout(); SessionList.SelectedItem = value; ScheduleSave(); UpdateStatus($"Moved {value.Name}");
    }
    private static T? ItemFromSender<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;
    private void SelectCard(object? value)
    {
        switch (value)
        {
            case SessionProfile session: SessionList.SelectedItem = session; break;
            case CommandSnippet snippet: SnippetList.SelectedItem = snippet; break;
            case AutomationRule automation: AutomationList.SelectedItem = automation; break;
        }
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
        SettingsSendAllModifier.SelectedIndex = settings.SendToAllModifier switch { "Ctrl" => 1, "Alt" => 2, _ => 0 };
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
        state.Settings.SendToAllModifier = SettingsSendAllModifier.SelectedIndex switch { 1 => "Ctrl", 2 => "Alt", _ => "Shift" };
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
