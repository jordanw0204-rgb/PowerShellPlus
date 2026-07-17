using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Text;
using System.Runtime.InteropServices;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace PowerShellPlus.Native;

internal sealed record RemoteTerminalSnapshotSource(IntPtr WindowHandle, string FallbackText, int Columns, int Rows);
internal sealed record RemoteTerminalSnapshot(string Text, int Columns, int Rows, int CursorColumn, int CursorRow, bool IsComposed);

public partial class TerminalPane : UserControl
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmKeyDown = 0x0100;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkV = 0x56;
    private const int MaximumQueuedCommands = 100;
    private const int MaximumCommandLength = 32_768;
    private const int MaximumClipboardCharacters = 1_000_000;
    public SessionProfile Profile { get; private set; }
    public event EventHandler? Activated;
    public event EventHandler? CloseRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? DetachRequested;
    public event Action<TerminalPane, string>? RawOutputReceived;
    private SessionRecoveryEntry? startupRecovery;
    private string previousOutput = string.Empty;
    private TerminalContainer? terminalContainer;
    private TermPTY? outputCaptureTerminal;
    private readonly Func<IEnumerable<CommandSnippet>> quickAccessProvider;
    private readonly Action commandStateChanged;
    private readonly Func<string, Task<bool>> sendAllCommand;
    private readonly Func<bool> sendAllModifierEnabled;
    private readonly Func<ModifierKeys> sendAllModifier;
    private int? queueSelectionIndex;
    private string queueNavigationDraft = string.Empty;
    private bool commandExecutionPending;
    private bool? sendButtonShowsAll;
    private char configuredCursorStyleCode;
    private long remoteOutputEventCount;
    private int remoteColumns = 120;
    private int remoteRows = 32;
    private string remoteFontFace = "Cascadia Mono";
    private int remoteFontSize = 12;

    public TerminalPane(SessionProfile profile, TerminalAppearance appearance, SessionRecoveryEntry? recovery = null, string? recoveredOutput = null,
        Func<IEnumerable<CommandSnippet>>? quickAccessProvider = null, Action? commandStateChanged = null,
        Func<string, Task<bool>>? sendAllCommand = null, Func<bool>? sendAllModifierEnabled = null, Func<ModifierKeys>? sendAllModifier = null)
    {
        Profile = profile;
        startupRecovery = recovery;
        previousOutput = recoveredOutput ?? string.Empty;
        this.quickAccessProvider = quickAccessProvider ?? (() => []);
        this.commandStateChanged = commandStateChanged ?? (() => { });
        this.sendAllCommand = sendAllCommand ?? SendCommandAsync;
        this.sendAllModifierEnabled = sendAllModifierEnabled ?? (() => true);
        this.sendAllModifier = sendAllModifier ?? (() => ModifierKeys.Shift);
        remoteFontFace = appearance.FontFace;
        remoteFontSize = appearance.FontSize;
        Profile.PendingCommands ??= [];
        InitializeComponent();
        Terminal.SizeChanged += (_, _) => ScheduleRemoteDimensionRefresh();
        Terminal.Terminal.SizeChanged += (_, _) => ScheduleRemoteDimensionRefresh();
        SetCommandBarExpanded(Profile.CommandBarExpanded, false, false);
        UpdateQueueDisplay();
        UpdateSendButtonVisual(false);
        AttachTerminalActivationHook();
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile, recovery);
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        Terminal.FontSizeWhenSettingTheme = appearance.FontSize;
        Terminal.Theme = appearance.Theme;
        configuredCursorStyleCode = CursorStyleCode(appearance.Theme.CursorStyle);
        AttachTerminalOutputFilter();
        Loaded += async (_, _) =>
        {
            AttachTerminalActivationHook();
            AttachTerminalOutputFilter();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            RefreshRemoteDimensions();
            HideNativeScrollbar();
            StateText.Text = $"  {appearance.ProfileName} · native renderer";
            await Task.Delay(1400);
            if (Terminal.ConPTYTerm?.TermProcIsStarted != true)
            {
                try
                {
                    var term = Terminal.ConPTYTerm;
                    var commandLine = Terminal.StartupCommandLine;
                    await Task.Run(() => term!.Start(commandLine, 100, 30, true));
                }
                catch (Exception exception)
                {
                    StateText.Text = "  Start failed";
                    Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
                    File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] {exception}\n");
                    return;
                }
            }
            StateDot.Fill = new SolidColorBrush(Color.FromRgb(166, 227, 161));
            AttachTerminalOutputFilter();
            RefreshRemoteDimensions();
            ConfigureRecoveryView();
        };
    }

    public void SetActive(bool active)
    {
        PaneBorder.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(137, 180, 250) : Color.FromRgb(49, 50, 68));
        PaneBorder.BorderThickness = active ? new Thickness(1.5) : new Thickness(1);
    }

    public bool HasTerminalSurfaceActivationHook => terminalContainer is not null;

    public bool HasNativeKeyboardFocus()
    {
        AttachTerminalActivationHook();
        if (terminalContainer?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return false;
        var focused = GetFocus();
        return focused != IntPtr.Zero && (focused == hwnd || IsChild(hwnd, focused));
    }

    public bool SimulateTerminalSurfaceClickForTest()
    {
        AttachTerminalActivationHook();
        if (terminalContainer?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return false;
        SendMessage(hwnd, WmLeftButtonDown, new IntPtr(1), IntPtr.Zero);
        SendMessage(hwnd, WmLeftButtonUp, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public async void SendCommand(string command) => await SendCommandAsync(command);

    public async Task<bool> SendCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var restarted = false;
        try
        {
            if (Terminal.ConPTYTerm?.Process?.HasExited == true)
            {
                await Terminal.RestartTerm();
                AttachTerminalOutputFilter();
                restarted = true;
            }
        }
        catch (ArgumentException)
        {
            try { await Terminal.RestartTerm(); AttachTerminalOutputFilter(); restarted = true; } catch { return false; }
        }
        catch (InvalidOperationException)
        {
            try { await Terminal.RestartTerm(); AttachTerminalOutputFilter(); restarted = true; } catch { return false; }
        }
        if (restarted) await Task.Delay(900);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Terminal.ConPTYTerm?.TermProcIsStarted == true)
                {
                    Terminal.ConPTYTerm.WriteToTerm(command + "\r");
                    Terminal.Focus();
                    return true;
                }
            }
            catch (NullReferenceException) { }
            catch (ArgumentException) { }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { }
            await Task.Delay(100);
        }
        return false;
    }

    private void QueueCurrentCommand()
    {
        var command = CommandInput.Text.Trim();
        if (command.Length == 0 || command.Length > MaximumCommandLength || Profile.PendingCommands.Count >= MaximumQueuedCommands) return;
        Profile.PendingCommands.Add(command);
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        CommandInput.Clear();
        UpdateQueueDisplay();
        commandStateChanged();
        CommandInput.Focus();
    }

    private async Task<bool> RunCommandInputAsync(bool sendToAll = false)
    {
        if (commandExecutionPending) return false;
        var command = CommandInput.Text.Trim();
        if (command.Length == 0 || command.Length > MaximumCommandLength) return false;
        commandExecutionPending = true;
        RunCommandButton.IsEnabled = false;
        try
        {
            var queuedIndex = queueSelectionIndex is int selected && selected >= 0 && selected < Profile.PendingCommands.Count
                && string.Equals(Profile.PendingCommands[selected], command, StringComparison.Ordinal)
                    ? selected
                    : (int?)null;
            if (!await (sendToAll ? sendAllCommand(command) : SendCommandAsync(command))) return false;
            if (queuedIndex is int index) Profile.PendingCommands.RemoveAt(index);
            PromoteNextQueuedCommand();
            UpdateQueueDisplay();
            commandStateChanged();
            return true;
        }
        finally
        {
            commandExecutionPending = false;
            RunCommandButton.IsEnabled = true;
        }
    }

    private void PromoteNextQueuedCommand()
    {
        queueNavigationDraft = string.Empty;
        if (Profile.PendingCommands.Count == 0)
        {
            queueSelectionIndex = null;
            CommandInput.Clear();
            return;
        }
        queueSelectionIndex = 0;
        CommandInput.Text = Profile.PendingCommands[0];
        CommandInput.SelectAll();
        CommandInput.Focus();
    }

    private void NavigateQueue(int direction)
    {
        if (Profile.PendingCommands.Count == 0) return;
        if (direction < 0)
        {
            if (queueSelectionIndex is null)
            {
                queueNavigationDraft = CommandInput.Text;
                queueSelectionIndex = Profile.PendingCommands.Count - 1;
            }
            else queueSelectionIndex = Math.Max(0, queueSelectionIndex.Value - 1);
        }
        else
        {
            if (queueSelectionIndex is null) return;
            if (queueSelectionIndex.Value < Profile.PendingCommands.Count - 1) queueSelectionIndex++;
            else
            {
                queueSelectionIndex = null;
                CommandInput.Text = queueNavigationDraft;
                CommandInput.CaretIndex = CommandInput.Text.Length;
                return;
            }
        }
        CommandInput.Text = Profile.PendingCommands[queueSelectionIndex!.Value];
        CommandInput.SelectAll();
    }

    private void UpdateQueueDisplay()
    {
        var count = Profile.PendingCommands.Count;
        QueueCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueCountText.Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        QueueCommandButton.ToolTip = count == 0
            ? "View command queue · Ctrl+Enter adds the current command"
            : $"View queue · {count} pending · Ctrl+Enter adds · Up/Down browses";
    }

    private void ShowQueueMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = QueueCommandButton,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = -4,
            MaxHeight = 300,
            MinWidth = 300,
            Style = TryFindResource("CardContextMenu") as Style
        };
        if (Profile.PendingCommands.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No queued commands",
                IsEnabled = false,
                Style = TryFindResource("CardMenuItem") as Style
            });
        }
        else
        {
            for (var index = 0; index < Profile.PendingCommands.Count; index++)
            {
                var queuedIndex = index;
                var command = Profile.PendingCommands[index];
                var item = new MenuItem
                {
                    Header = AbbreviateCommand(command),
                    InputGestureText = $"{index + 1} / {Profile.PendingCommands.Count}",
                    ToolTip = command,
                    Style = TryFindResource("CardMenuItem") as Style,
                    Icon = new TextBlock { Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Foreground = new SolidColorBrush(Color.FromRgb(137, 180, 250)), FontSize = 10, FontWeight = FontWeights.SemiBold }
                };
                item.Click += (_, _) => SelectQueuedCommand(queuedIndex);
                menu.Items.Add(item);
            }
        }
        QueueCommandButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SelectQueuedCommand(int index)
    {
        if (index < 0 || index >= Profile.PendingCommands.Count) return;
        queueNavigationDraft = CommandInput.Text;
        queueSelectionIndex = index;
        CommandInput.Text = Profile.PendingCommands[index];
        CommandInput.SelectAll();
        CommandInput.Focus();
    }

    private static string AbbreviateCommand(string command)
    {
        var singleLine = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 72 ? singleLine : singleLine[..69] + "…";
    }

    private bool IsSendToAllActive(ModifierKeys modifiers)
    {
        if (!sendAllModifierEnabled()) return false;
        var configured = sendAllModifier();
        return configured != ModifierKeys.None && modifiers.HasFlag(configured);
    }

    private string SendToAllModifierLabel() => sendAllModifier() switch
    {
        ModifierKeys.Control => "Ctrl",
        ModifierKeys.Alt => "Alt",
        _ => "Shift"
    };

    private void UpdateSendButtonVisual(bool sendToAll, bool force = false)
    {
        if (!force && sendButtonShowsAll == sendToAll) return;
        sendButtonShowsAll = sendToAll;
        if (sendToAll)
        {
            RunCommandButton.Content = "⇉";
            RunCommandButton.Foreground = new SolidColorBrush(Color.FromRgb(203, 166, 247));
            RunCommandButton.Background = new SolidColorBrush(Color.FromRgb(59, 49, 84));
            RunCommandButton.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 166, 247));
            RunCommandButton.ToolTip = $"Send to all terminals ({SendToAllModifierLabel()}+Enter)";
        }
        else
        {
            RunCommandButton.Content = "▶";
            RunCommandButton.ClearValue(ForegroundProperty);
            RunCommandButton.ClearValue(BackgroundProperty);
            RunCommandButton.ClearValue(BorderBrushProperty);
            RunCommandButton.ToolTip = sendAllModifierEnabled()
                ? $"Run in this terminal (Enter) · Hold {SendToAllModifierLabel()} for all terminals"
                : "Run in this terminal (Enter)";
        }
    }

    private void RefreshSendButtonVisual() => UpdateSendButtonVisual(IsSendToAllActive(Keyboard.Modifiers));

    private void ShowQuickAccessMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = QuickAccessButton,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = -4,
            Style = TryFindResource("CardContextMenu") as Style
        };
        var commands = quickAccessProvider().Where(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command)).ToList();
        if (commands.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No quick access commands", IsEnabled = false, Style = TryFindResource("CardMenuItem") as Style });
        }
        else
        {
            foreach (var snippet in commands)
            {
                var item = new MenuItem
                {
                    Header = snippet.Name,
                    InputGestureText = snippet.Category,
                    ToolTip = snippet.Command,
                    Style = TryFindResource("CardMenuItem") as Style,
                    Tag = snippet
                };
                item.Click += (_, _) =>
                {
                    queueSelectionIndex = null;
                    queueNavigationDraft = string.Empty;
                    CommandInput.Text = snippet.Command;
                    CommandInput.CaretIndex = CommandInput.Text.Length;
                    CommandInput.Focus();
                };
                menu.Items.Add(item);
            }
        }
        QuickAccessButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SetCommandBarExpanded(bool expanded, bool animate, bool persist)
    {
        Profile.CommandBarExpanded = expanded;
        CommandBarToggle.Content = expanded ? "⌄" : "⌃";
        CommandBarToggle.ToolTip = expanded ? "Hide command bar" : "Show command bar";
        CommandBarContainer.BeginAnimation(HeightProperty, null);
        CommandBarContent.BeginAnimation(OpacityProperty, null);
        if (!animate)
        {
            CommandBarContainer.Visibility = Visibility.Visible;
            CommandBarContainer.Height = expanded ? double.NaN : 16;
            CommandBarContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            CommandBarContent.Opacity = 1;
        }
        else if (expanded)
        {
            CommandBarContainer.Visibility = Visibility.Visible;
            CommandBarContainer.Height = 16;
            CommandBarContent.Visibility = Visibility.Visible;
            CommandBarContent.Opacity = 0;
            var height = new DoubleAnimation(16, 36, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            height.Completed += (_, _) => { CommandBarContainer.BeginAnimation(HeightProperty, null); CommandBarContainer.Height = double.NaN; };
            CommandBarContainer.BeginAnimation(HeightProperty, height);
            CommandBarContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            CommandInput.Focus();
        }
        else
        {
            var height = new DoubleAnimation(CommandBarContainer.ActualHeight, 16, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            height.Completed += (_, _) =>
            {
                CommandBarContainer.BeginAnimation(HeightProperty, null);
                CommandBarContent.BeginAnimation(OpacityProperty, null);
                CommandBarContainer.Height = 16;
                CommandBarContent.Visibility = Visibility.Collapsed;
                CommandBarContent.Opacity = 1;
                Terminal.Focus();
            };
            CommandBarContainer.BeginAnimation(HeightProperty, height);
            CommandBarContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100)));
        }
        if (persist) commandStateChanged();
    }

    public async Task RestartAsync()
    {
        var sshRecovery = startupRecovery?.SshWasActive == true ? startupRecovery : null;
        StateText.Text = sshRecovery is null ? "  Restarting…" : "  Retrying SSH recovery…";
        startupRecovery = sshRecovery;
        Terminal.StartupCommandLine = BuildCommandLine(Profile, sshRecovery);
        await Terminal.RestartTerm();
        AttachTerminalOutputFilter();
        StateText.Text = "  Windows Terminal control";
        Terminal.Focus();
    }

    public void Stop()
    {
        try { Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
        StateText.Text = "  Stopped";
        StateDot.Fill = new SolidColorBrush(Color.FromRgb(108, 112, 134));
    }

    public void SetHandoffPending(bool pending)
    {
        DetachButton.IsEnabled = !pending;
        DetachButton.Content = pending ? "…" : ">_";
        DetachButton.ToolTip = pending ? "Verifying Windows Terminal handoff…" : "Move to Windows Terminal";
    }

    public string GetOutput()
    {
        try { return Terminal.ConPTYTerm?.GetConsoleText() ?? string.Empty; } catch { return string.Empty; }
    }

    public string GetRawOutputForTest()
    {
        try { return Terminal.ConPTYTerm?.GetConsoleText(false) ?? string.Empty; } catch { return string.Empty; }
    }

    internal RemoteTerminalSnapshotSource GetRemoteSnapshotSource()
    {
        AttachTerminalActivationHook();
        RefreshRemoteDimensions();
        var dimensions = GetRemoteDimensions();
        var handle = terminalContainer?.Handle ?? IntPtr.Zero;
        return new RemoteTerminalSnapshotSource(handle, GetOutput(), dimensions.Columns, dimensions.Rows);
    }

    internal static RemoteTerminalSnapshot CaptureRemoteScreen(RemoteTerminalSnapshotSource source)
    {
        var text = string.Empty;
        int? cursorOffset = null;
        var composed = false;
        if (source.WindowHandle != IntPtr.Zero)
        {
            try
            {
                var element = AutomationElement.FromHandle(source.WindowHandle);
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) && patternObject is TextPattern pattern)
                {
                    text = pattern.DocumentRange.GetText(-1);
                    var selections = pattern.GetSelection();
                    if (selections.Length > 0 && selections[0].GetText(-1).Length == 0)
                    {
                        var beforeCursor = pattern.DocumentRange.Clone();
                        beforeCursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
                        cursorOffset = beforeCursor.GetText(-1).Length;
                    }
                    composed = true;
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        }

        if (!composed)
        {
            text = TailFallbackTranscript(source.FallbackText, source.Rows);
            cursorOffset = text.Length;
            composed = false;
        }
        return BuildRemoteSnapshot(text, source.Columns, source.Rows, cursorOffset, composed);
    }

    internal void RequestRemoteRedraw()
    {
        var dimensions = GetRemoteDimensions();
        try { Terminal.ConPTYTerm?.Resize(dimensions.Columns, dimensions.Rows); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
    }

    private static RemoteTerminalSnapshot BuildRemoteSnapshot(string text, int columns, int rows, int? cursorOffset, bool composed)
    {
        text = text.Replace("\0", string.Empty, StringComparison.Ordinal);
        var boundedOffset = Math.Clamp(cursorOffset ?? text.Length, 0, text.Length);
        var beforeCursor = NormalizeRemoteNewlines(text[..boundedOffset]);
        var normalized = NormalizeRemoteNewlines(text);
        var cursorLine = beforeCursor.Count(value => value == '\n');
        var lastNewline = beforeCursor.LastIndexOf('\n');
        var cursorColumn = beforeCursor.Length - lastNewline - 1;
        var totalLines = normalized.Count(value => value == '\n') + 1;
        var viewportStart = Math.Max(0, totalLines - rows);
        var viewportRow = Math.Clamp(cursorLine - viewportStart + 1, 1, rows);
        cursorColumn = Math.Clamp(cursorColumn + 1, 1, columns);
        return new RemoteTerminalSnapshot(
            normalized.Replace("\n", "\r\n", StringComparison.Ordinal),
            columns, rows, cursorColumn, viewportRow, composed);
    }

    private static string TailFallbackTranscript(string text, int rows)
    {
        var normalized = NormalizeRemoteNewlines(text);
        var lines = normalized.Split('\n');
        var keep = Math.Max(rows, rows * 3);
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - keep)));
    }

    private static string NormalizeRemoteNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public bool WriteRemoteInput(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > MaximumCommandLength) return false;
        try
        {
            if (Terminal.ConPTYTerm?.TermProcIsStarted != true) return false;
            Terminal.ConPTYTerm.WriteToTerm(input);
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public (int Columns, int Rows) GetRemoteDimensions() =>
        (Math.Max(2, Volatile.Read(ref remoteColumns)), Math.Max(2, Volatile.Read(ref remoteRows)));

    public (string FontFace, int FontSize) GetRemoteAppearance() =>
        (remoteFontFace, Math.Max(6, Volatile.Read(ref remoteFontSize)));

    public IReadOnlyList<string> GetRemotePendingCommands() => Profile.PendingCommands.ToArray();

    public IReadOnlyList<CommandSnippet> GetRemoteQuickCommands() => quickAccessProvider()
        .Where(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command))
        .ToArray();

    public bool QueueRemoteCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0 || command.Length > MaximumCommandLength || Profile.PendingCommands.Count >= MaximumQueuedCommands) return false;
        Profile.PendingCommands.Add(command);
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        UpdateQueueDisplay();
        commandStateChanged();
        return true;
    }

    public async Task<bool> RunRemoteCommandAsync(string command, int? queuedIndex)
    {
        command = command.Trim();
        if (commandExecutionPending || command.Length == 0 || command.Length > MaximumCommandLength) return false;
        commandExecutionPending = true;
        RunCommandButton.IsEnabled = false;
        try
        {
            if (!await SendCommandAsync(command)) return false;
            if (queuedIndex is int index && index >= 0 && index < Profile.PendingCommands.Count
                && string.Equals(Profile.PendingCommands[index], command, StringComparison.Ordinal))
                Profile.PendingCommands.RemoveAt(index);
            PromoteNextQueuedCommand();
            UpdateQueueDisplay();
            commandStateChanged();
            return true;
        }
        finally
        {
            commandExecutionPending = false;
            RunCommandButton.IsEnabled = true;
        }
    }

    public void EnableRemoteOutputCapture() => AttachTerminalOutputFilter();

    public int? GetRootProcessId()
    {
        try
        {
            var process = Terminal.ConPTYTerm?.Process;
            if (process is null || process.HasExited) return null;
            var type = process.GetType();
            if (type.GetProperty("Pid")?.GetValue(process) is int pid) return pid;
            if (type.GetProperty("Process")?.GetValue(process) is Process wrapped) return wrapped.Id;
            var processInfo = type.GetProperty("ProcessInfo")?.GetValue(process);
            if (processInfo is not null)
            {
                var infoType = processInfo.GetType();
                var value = infoType.GetField("dwProcessId")?.GetValue(processInfo) ?? infoType.GetProperty("dwProcessId")?.GetValue(processInfo);
                if (value is uint unsigned) return checked((int)unsigned);
                if (value is int signed) return signed;
            }
        }
        catch { return null; }
        return null;
    }

    public CodexProcessState GetCodexProcessState()
    {
        var processId = GetRootProcessId();
        return processId is int value ? ProcessTreeInspector.FindCodexProcess(value) : default;
    }

    public void ApplyAppearance(TerminalAppearance appearance)
    {
        // Font properties only take effect when the theme is (re)applied; the
        // Theme setter pushes everything to the native control immediately.
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        Terminal.FontSizeWhenSettingTheme = appearance.FontSize;
        Terminal.Theme = appearance.Theme;
        remoteFontFace = appearance.FontFace;
        Volatile.Write(ref remoteFontSize, appearance.FontSize);
        configuredCursorStyleCode = CursorStyleCode(appearance.Theme.CursorStyle);
        AttachTerminalOutputFilter();
    }

    public void ApplyProfile(SessionProfile profile)
    {
        Profile = profile;
        startupRecovery = null;
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile, null);
    }

    public bool IsNativeScrollbarHidden()
    {
        var scrollbar = FindVisualChild<ScrollBar>(Terminal.Terminal);
        return scrollbar is null || scrollbar.Visibility != Visibility.Visible || scrollbar.ActualWidth == 0;
    }

    public bool FocusCommandInputForTest() => CommandInput.Focus();
    public bool CommandBarExpandedForTest => Profile.CommandBarExpanded && CommandBarContainer.Visibility == Visibility.Visible;
    public int QueuedCommandCountForTest => Profile.PendingCommands.Count;
    public string QueueCountTextForTest => QueueCountText.Text;
    public string CommandInputTextForTest => CommandInput.Text;
    public bool CommandInputAutoGrowsForTest => CommandInput.TextWrapping == TextWrapping.Wrap && CommandInput.MinLines == 1
        && CommandInput.MaxLines == 6 && CommandInput.VerticalContentAlignment == VerticalAlignment.Top;
    public double CommandInputHeightForTest => CommandInput.ActualHeight;
    public bool HandoffButtonReadyForTest => DetachButton.IsEnabled && DetachButton.Content?.ToString() == ">_"
        && DetachButton.ToolTip?.ToString()?.Contains("Windows Terminal", StringComparison.Ordinal) == true;
    public string SendCommandGlyphForTest => RunCommandButton.Content?.ToString() ?? string.Empty;
    public string SendCommandToolTipForTest => RunCommandButton.ToolTip?.ToString() ?? string.Empty;
    public int QuickAccessCommandCountForTest => quickAccessProvider().Count(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command));
    public bool SelectFirstQuickAccessCommandForTest()
    {
        var expected = quickAccessProvider().FirstOrDefault(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command));
        if (expected is null) return false;
        ShowQuickAccessMenu();
        var item = QuickAccessButton.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(value => value.IsEnabled);
        if (item is null) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (QuickAccessButton.ContextMenu is { } menu) menu.IsOpen = false;
        return string.Equals(CommandInput.Text, expected.Command, StringComparison.Ordinal);
    }
    public void SetCommandInputForTest(string value) { queueSelectionIndex = null; queueNavigationDraft = string.Empty; CommandInput.Text = value; }
    public void QueueCommandForTest() => QueueCurrentCommand();
    public void ClearQueuedCommandsForTest()
    {
        Profile.PendingCommands.Clear();
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        CommandInput.Clear();
        UpdateQueueDisplay();
    }
    public Task<bool> RunCommandForTestAsync(bool sendToAll = false) => RunCommandInputAsync(sendToAll);
    public void NavigateQueueForTest(int direction) => NavigateQueue(direction);
    public void SetCommandBarExpandedForTest(bool expanded) => SetCommandBarExpanded(expanded, false, false);
    public bool SendToAllActiveForTest(ModifierKeys modifiers) => IsSendToAllActive(modifiers);
    public void SetSendToAllVisualForTest(bool active) => UpdateSendButtonVisual(active);
    public int OpenQueueMenuForTest()
    {
        ShowQueueMenu();
        return QueueCommandButton.ContextMenu?.Items.Count ?? 0;
    }
    public int ClickQueueButtonForTest()
    {
        QueueCommandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var count = QueueCommandButton.ContextMenu?.Items.Count ?? 0;
        if (QueueCommandButton.ContextMenu is { } menu) menu.IsOpen = false;
        return count;
    }
    public bool SelectQueuedCommandForTest(int index)
    {
        SelectQueuedCommand(index);
        if (QueueCommandButton.ContextMenu is { } menu) menu.IsOpen = false;
        return queueSelectionIndex == index && index >= 0 && index < Profile.PendingCommands.Count
            && string.Equals(CommandInput.Text, Profile.PendingCommands[index], StringComparison.Ordinal);
    }
    public double QueueMenuMaxHeightForTest => QueueCommandButton.ContextMenu?.MaxHeight ?? double.PositiveInfinity;
    public static string FormatClipboardTextForTest(string text) => FormatClipboardText(text);
    public string ForceCursorStyleForTest(string text)
    {
        var buffer = text.ToCharArray();
        var span = buffer.AsSpan();
        EnforceCursorStyle(ref span);
        return new string(buffer);
    }
    public bool PasteTextForTest(string text)
    {
        var terminal = Terminal.ConPTYTerm;
        if (terminal is null) return false;
        terminal.WriteToTerm(FormatClipboardText(text));
        return true;
    }
    public void SubmitTerminalInputForTest() => Terminal.ConPTYTerm?.WriteToTerm("\r");
    public async Task<bool> QueueWithCtrlEnterForTestAsync(string command)
    {
        var before = Profile.PendingCommands.Count;
        SetCommandInputForTest(command);
        var handled = await HandleCommandInputKeyAsync(Key.Enter, ModifierKeys.Control);
        return handled && Profile.PendingCommands.Count == before + 1 && string.IsNullOrEmpty(CommandInput.Text);
    }
    public void RefreshCommandRoutingAppearance() => UpdateSendButtonVisual(IsSendToAllActive(Keyboard.Modifiers), true);

    private void HideNativeScrollbar()
    {
        var scrollbar = FindVisualChild<ScrollBar>(Terminal.Terminal);
        if (scrollbar is null) return;
        scrollbar.Visibility = Visibility.Collapsed;
        scrollbar.Width = 0;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void AttachTerminalActivationHook()
    {
        if (terminalContainer is not null) return;
        terminalContainer = FindVisualChild<TerminalContainer>(Terminal.Terminal);
        if (terminalContainer is not null) terminalContainer.MessageHook += TerminalMessageHook;
    }

    private void AttachTerminalOutputFilter()
    {
        // Applied after the terminal process is ready; assigning the interceptor
        // before TermPTY starts can suppress its initial read loop.
        if (Terminal.ConPTYTerm is not { TermProcIsStarted: true } terminal) return;
        terminal.InterceptOutputToUITerminal = EnforceCursorStyle;
        if (ReferenceEquals(outputCaptureTerminal, terminal)) return;
        if (outputCaptureTerminal is not null) outputCaptureTerminal.TerminalOutput -= CaptureTerminalOutput;
        outputCaptureTerminal = terminal;
        outputCaptureTerminal.TerminalOutput += CaptureTerminalOutput;
    }

    private void CaptureTerminalOutput(object? sender, TerminalOutputEventArgs args)
    {
        Interlocked.Increment(ref remoteOutputEventCount);
        try { RawOutputReceived?.Invoke(this, args.Data); }
        catch { /* A remote viewer must never interrupt the ConPTY read loop. */ }
    }

    public long RemoteOutputEventsForTest => Interlocked.Read(ref remoteOutputEventCount);

    private void ScheduleRemoteDimensionRefresh()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(RefreshRemoteDimensions, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RefreshRemoteDimensions()
    {
        try
        {
            var columns = Terminal.Terminal.Columns;
            var rows = Terminal.Terminal.Rows;
            if (columns >= 2) Volatile.Write(ref remoteColumns, columns);
            if (rows >= 2) Volatile.Write(ref remoteRows, rows);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void EnforceCursorStyle(ref Span<char> output)
    {
        // DECSCUSR is ESC [ Ps SP q. Applications such as TUIs can emit it
        // after the theme is applied, so normalize it to the user's setting.
        for (var index = 0; index <= output.Length - 5; index++)
        {
            if (output[index] == '\u001b' && output[index + 1] == '[' && output[index + 2] is >= '0' and <= '6'
                && output[index + 3] == ' ' && output[index + 4] == 'q')
                output[index + 2] = configuredCursorStyleCode;
        }
    }

    private static char CursorStyleCode(CursorStyle style) => style switch
    {
        CursorStyle.SteadyBlock => '2',
        CursorStyle.BlinkingUnderline => '3',
        CursorStyle.SteadyUnderline => '4',
        CursorStyle.BlinkingBar => '5',
        CursorStyle.SteadyBar => '6',
        _ => '1'
    };

    private IntPtr TerminalMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmLeftButtonDown)
        {
            Activated?.Invoke(this, EventArgs.Empty);
            SetFocus(hwnd);
        }
        else if (message == WmKeyDown && wParam.ToInt32() == VkV && IsKeyDown(VkControl) && !IsKeyDown(VkMenu) && TryPasteClipboardText())
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool TryPasteClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return false;
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (string.IsNullOrEmpty(text)) return false;
            var terminal = Terminal.ConPTYTerm;
            if (terminal is null) return false;
            terminal.WriteToTerm(FormatClipboardText(text));
            return true;
        }
        catch (ExternalException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string FormatClipboardText(string text)
    {
        var safeText = text.Length > MaximumClipboardCharacters ? text[..MaximumClipboardCharacters] : text;
        safeText = safeText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\u001b", string.Empty, StringComparison.Ordinal);
        return $"\u001b[200~{safeText}\u001b[201~";
    }

    private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    public static string BuildCommandLine(SessionProfile profile, SessionRecoveryEntry? recovery)
    {
        var command = Environment.ExpandEnvironmentVariables(profile.CommandLine.Trim());
        var sshResumeCommand = SshRecovery.BuildPowerShellResumeCommand(recovery);
        var resumeSsh = sshResumeCommand is not null;
        var resumeCodex = recovery?.CodexWasActive == true && !resumeSsh;
        var startupDirectory = (resumeCodex || resumeSsh) && !string.IsNullOrWhiteSpace(recovery?.WorkingDirectory) && Directory.Exists(recovery.WorkingDirectory)
            ? recovery.WorkingDirectory
            : profile.WorkingDirectory;
        var validDirectory = !string.IsNullOrWhiteSpace(startupDirectory) && Directory.Exists(startupDirectory);
        var escaped = validDirectory ? startupDirectory.Replace("'", "''") : string.Empty;
        var resumeArgument = resumeCodex && CodexSessionLocator.IsSafeCodexId(recovery?.CodexSessionId) ? $" '{recovery!.CodexSessionId}'" : " --all";
        var modelArgument = resumeCodex && CodexSessionLocator.IsSafeCodexModel(recovery?.CodexModel) ? $" --model '{recovery!.CodexModel}'" : string.Empty;
        var reviewerArgument = resumeCodex && CodexSessionLocator.IsSafeCodexApprovalsReviewer(recovery?.CodexApprovalsReviewer)
            ? $" --config 'approvals_reviewer=\"{recovery!.CodexApprovalsReviewer}\"'"
            : string.Empty;
        var permissionsArgument = resumeCodex && CodexSessionLocator.IsSafeCodexPermissionProfile(recovery?.CodexPermissionProfile)
            && CodexSessionLocator.IsSafeCodexApprovalPolicy(recovery?.CodexApprovalPolicy)
                ? $" --config 'default_permissions=\"{recovery!.CodexPermissionProfile}\"'{reviewerArgument} --ask-for-approval '{recovery.CodexApprovalPolicy}'"
                : resumeCodex && CodexSessionLocator.IsSafeCodexPermissions(recovery?.CodexSandboxMode, recovery?.CodexApprovalPolicy)
                    ? $" --sandbox '{recovery!.CodexSandboxMode}'{reviewerArgument} --ask-for-approval '{recovery.CodexApprovalPolicy}'"
                    : string.Empty;
        if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase) || command.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            var script = validDirectory ? $"Set-Location -LiteralPath '{escaped}'; " : string.Empty;
            script += CodexLaunchStore.BuildPowerShellWrapper(profile.Id);
            script += "; " + SshLaunchStore.BuildPowerShellWrapper(profile.Id);
            if (resumeSsh) script += "; " + sshResumeCommand;
            else if (resumeCodex) script += $"; & codex resume{resumeArgument}{modelArgument}{permissionsArgument}";
            if (script.Length == 0) return command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return $"{command} -NoExit -EncodedCommand {encoded}";
        }
        if (resumeCodex && Path.GetFileNameWithoutExtension(command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty).Equals("codex", StringComparison.OrdinalIgnoreCase))
            return $"codex resume{resumeArgument}{modelArgument}{permissionsArgument}";
        return command;
    }

    public static string DecodePowerShellStartupScript(string commandLine)
    {
        const string marker = "-EncodedCommand ";
        var index = commandLine.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        var encoded = commandLine[(index + marker.Length)..].Trim().Split(' ')[0];
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(encoded)); }
        catch { return string.Empty; }
    }

    public void SetPreviousOutputForTest(string output)
    {
        previousOutput = output;
        ConfigureRecoveryView(true);
    }

    public void HidePreviousOutputForTest() => RecoveryOverlay.Visibility = Visibility.Collapsed;

    private void ConfigureRecoveryView(bool show = false)
    {
        if (string.IsNullOrWhiteSpace(previousOutput)) return;
        PreviousOutputButton.Visibility = Visibility.Visible;
        RecoveryOutputText.Text = previousOutput;
        RecoveryTimestampText.Text = startupRecovery?.CapturedUtc.ToLocalTime().ToString("Recovered MMM d, yyyy 'at' h:mm tt") ?? "Recovered after restart";
        if (show || startupRecovery?.CodexWasActive != true) RecoveryOverlay.Visibility = Visibility.Visible;
    }

    private void ActivatePane(object sender, MouseButtonEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (IsWithin(e.OriginalSource as DependencyObject, CommandBarContainer) || IsWithin(e.OriginalSource as DependencyObject, BottomCommandReveal)) return;
        Terminal.Focus();
    }
    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            try { current = VisualTreeHelper.GetParent(current); }
            catch { current = LogicalTreeHelper.GetParent(current); }
        }
        return false;
    }
    private void QuickAccessClick(object sender, RoutedEventArgs e) { ShowQuickAccessMenu(); e.Handled = true; }
    private void QueueCommandClick(object sender, RoutedEventArgs e) { ShowQueueMenu(); e.Handled = true; }
    private async void RunCommandClick(object sender, RoutedEventArgs e) { await RunCommandInputAsync(IsSendToAllActive(Keyboard.Modifiers)); e.Handled = true; }
    private async void CommandInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        RefreshSendButtonVisual();
        e.Handled = await HandleCommandInputKeyAsync(e.Key, e.KeyboardDevice.Modifiers);
    }
    private async Task<bool> HandleCommandInputKeyAsync(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Enter && modifiers.HasFlag(ModifierKeys.Control)) { QueueCurrentCommand(); return true; }
        if (key == Key.Enter) { await RunCommandInputAsync(IsSendToAllActive(modifiers)); return true; }
        if (key == Key.Up) { NavigateQueue(-1); return true; }
        if (key == Key.Down) { NavigateQueue(1); return true; }
        return false;
    }
    private void CommandInputPreviewKeyUp(object sender, KeyEventArgs e) => Dispatcher.BeginInvoke(RefreshSendButtonVisual, System.Windows.Threading.DispatcherPriority.Input);
    private void RunCommandMouseEnter(object sender, MouseEventArgs e) => RefreshSendButtonVisual();
    private void ToggleCommandBarClick(object sender, RoutedEventArgs e) { SetCommandBarExpanded(!Profile.CommandBarExpanded, true, true); e.Handled = true; }
    private void PreviousOutputClick(object sender, RoutedEventArgs e) { ConfigureRecoveryView(true); RecoveryOverlay.Visibility = Visibility.Visible; }
    private void CloseRecoveryClick(object sender, RoutedEventArgs e) { RecoveryOverlay.Visibility = Visibility.Collapsed; Terminal.Focus(); }
    private void ClearClick(object sender, RoutedEventArgs e) { Terminal.ConPTYTerm?.ClearUITerminal(); Terminal.Focus(); }
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private async void RestartClick(object sender, RoutedEventArgs e) => await RestartAsync();
    private void DetachClick(object sender, RoutedEventArgs e) { DetachRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    private void EditClick(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);
    private void CloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
