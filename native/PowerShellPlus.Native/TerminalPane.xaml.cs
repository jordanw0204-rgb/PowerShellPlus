using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Collections;
using System.Text.RegularExpressions;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using System.Windows.Interop;

namespace PowerShellPlus.Native;

internal sealed record RemoteTerminalSnapshotSource(IntPtr WindowHandle, string FallbackText, int Columns, int Rows);
internal sealed record RemoteTerminalSnapshot(string Text, int Columns, int Rows, int CursorColumn, int CursorRow, bool IsComposed);
internal sealed record ComposerAttachment(string Id, string LocalPath, string DisplayName, bool IsImage, bool IsTemporary);
internal sealed record HistoryDisplayEntry(string Message, DateTime SentUtc)
{
    public string RelativeTime => TerminalPane.FormatRelativeHistoryTime(SentUtc, DateTime.UtcNow);
}
internal enum AttachmentPreviewKind { Image, Media, Text, Generic }
internal enum AgentActivityState { Starting, Idle, Working, Waiting, Stopped, Error }
internal enum AgentKind { Terminal, Codex, Hermes }
internal enum RemoteImagePasteMode { Attachment, FilePath }
internal enum RemoteClipboardPasteContent { Image, Text, Empty }

internal sealed class BracketedPasteModeTracker
{
    private const string EnableSequence = "\u001b[?2004h";
    private const string DisableSequence = "\u001b[?2004l";
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";
    private string sequenceTail = string.Empty;

    public bool Enabled { get; private set; }

    public void RecordOutput(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        var combined = sequenceTail + data;
        var enabledAt = combined.LastIndexOf(EnableSequence, StringComparison.Ordinal);
        var disabledAt = combined.LastIndexOf(DisableSequence, StringComparison.Ordinal);
        if (enabledAt >= 0 || disabledAt >= 0) Enabled = enabledAt > disabledAt;
        var tailLength = Math.Min(EnableSequence.Length - 1, combined.Length);
        sequenceTail = combined[^tailLength..];
    }

    public void Reset()
    {
        Enabled = false;
        sequenceTail = string.Empty;
    }

    public string FormatSubmission(string command)
    {
        if (!Enabled || command.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            return command + "\r";
        var normalized = command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return PasteStart + normalized + PasteEnd + "\r";
    }

    internal static bool SubmissionContractPassesForTest()
    {
        var tracker = new BracketedPasteModeTracker();
        if (tracker.FormatSubmission("hello") != "hello\r") return false;
        tracker.RecordOutput("\u001b[?20");
        tracker.RecordOutput("04h");
        var large = "first line\n" + new string('x', 32_768) + "\nlast line";
        var formatted = tracker.FormatSubmission(large);
        if (!formatted.StartsWith(PasteStart, StringComparison.Ordinal)
            || !formatted.EndsWith(PasteEnd + "\r", StringComparison.Ordinal)
            || !formatted.Contains(large, StringComparison.Ordinal)) return false;
        tracker.RecordOutput("\u001b[?2004l");
        if (tracker.FormatSubmission("plain") != "plain\r") return false;
        tracker.RecordOutput(EnableSequence);
        return tracker.FormatSubmission("unsafe\u001bcommand") == "unsafe\u001bcommand\r";
    }
}

internal sealed class TerminalOutputActivityTracker
{
    private static readonly long InputEchoWindowTicks = TimeSpan.FromMilliseconds(450).Ticks;
    private long lastInputTicks;
    private long lastMeaningfulOutputTicks;

    public void RecordInput(DateTime utcNow) => Volatile.Write(ref lastInputTicks, utcNow.Ticks);

    public bool RecordOutput(string data, DateTime utcNow)
    {
        var inputTicks = Volatile.Read(ref lastInputTicks);
        if (inputTicks > 0 && utcNow.Ticks - inputTicks <= InputEchoWindowTicks) return false;
        if (!HasMeaningfulOutput(data)) return false;
        Volatile.Write(ref lastMeaningfulOutputTicks, utcNow.Ticks);
        return true;
    }

    public bool HasRecentOutput(DateTime utcNow, TimeSpan window)
    {
        var outputTicks = Volatile.Read(ref lastMeaningfulOutputTicks);
        return outputTicks > 0 && utcNow.Ticks - outputTicks < window.Ticks;
    }

    private static bool HasMeaningfulOutput(string data)
    {
        if (string.IsNullOrEmpty(data)) return false;
        var visible = new StringBuilder(Math.Min(data.Length, 256));
        for (var index = 0; index < data.Length; index++)
        {
            var character = data[index];
            if (character == '\u001b')
            {
                if (++index >= data.Length) break;
                if (data[index] == '[')
                {
                    while (++index < data.Length && data[index] is < '@' or > '~') { }
                }
                else if (data[index] == ']')
                {
                    while (++index < data.Length)
                    {
                        if (data[index] == '\a') break;
                        if (data[index] == '\u001b' && index + 1 < data.Length && data[index + 1] == '\\') { index++; break; }
                    }
                }
                continue;
            }
            if (!char.IsControl(character)) visible.Append(character);
        }

        var text = visible.ToString().Trim();
        if (!text.Any(char.IsLetterOrDigit)) return false;
        // Full-screen TUIs commonly repaint only an elapsed-time cell while idle.
        if (text.Length <= 8 && text.All(value => char.IsDigit(value) || char.IsWhiteSpace(value) || value is ':' or '.' or 's' or 'm' or 'h')) return false;
        return true;
    }
}

internal sealed class CodexOutputActivityTracker
{
    private const int MaximumVisibleCharacters = 4096;
    private static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PromptRedrawGuard = TimeSpan.FromMilliseconds(750);
    private static readonly Regex ConfirmationFooter = new(
        @"press\s+enter\s+to\s+(?:confirm|submit|select|continue)|esc\s+to\s+cancel|use\s+(?:the\s+)?(?:up|down|arrow)\s+keys[^\r\n]*(?:enter|select)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ApprovalQuestion = new(
        @"would\s+you\s+like\s+to\s+(?:run|approve|allow)|approval\s+(?:required|request)|allow\s+(?:this|the\s+following)\s+command|approve\s+(?:this|the\s+following)\s+command",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChoiceLine = new(
        @"(?:^|\n)\s*(?:[^\p{L}\p{N}\s]\s*)?\d+[.)]\s+\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly object sync = new();
    private readonly StringBuilder recentVisibleOutput = new();
    private int waiting;
    private long responseGraceUntilTicks;
    private long ignorePromptUntilTicks;

    public AgentActivityState State => StateAt(DateTime.UtcNow);

    public void RecordOutput(string data, DateTime utcNow)
    {
        var visible = TerminalTextSanitizer.ForTranscript(data);
        if (visible.Length == 0) return;
        lock (sync)
        {
            recentVisibleOutput.Append(visible);
            if (recentVisibleOutput.Length > MaximumVisibleCharacters)
                recentVisibleOutput.Remove(0, recentVisibleOutput.Length - MaximumVisibleCharacters);
            if (utcNow.Ticks < Interlocked.Read(ref ignorePromptUntilTicks)) return;
            var text = recentVisibleOutput.ToString();
            if (!LooksLikeInteractivePrompt(text)) return;
            Volatile.Write(ref waiting, 1);
            Interlocked.Exchange(ref responseGraceUntilTicks, 0);
        }
    }

    public void UserResponded(DateTime utcNow)
    {
        Volatile.Write(ref waiting, 0);
        Interlocked.Exchange(ref responseGraceUntilTicks, utcNow.Add(ResponseGrace).Ticks);
        Interlocked.Exchange(ref ignorePromptUntilTicks, utcNow.Add(PromptRedrawGuard).Ticks);
        lock (sync) recentVisibleOutput.Clear();
    }

    public void Reset()
    {
        Volatile.Write(ref waiting, 0);
        Interlocked.Exchange(ref responseGraceUntilTicks, 0);
        Interlocked.Exchange(ref ignorePromptUntilTicks, 0);
        lock (sync) recentVisibleOutput.Clear();
    }

    private AgentActivityState StateAt(DateTime utcNow)
    {
        if (Volatile.Read(ref waiting) != 0) return AgentActivityState.Waiting;
        return utcNow.Ticks < Interlocked.Read(ref responseGraceUntilTicks)
            ? AgentActivityState.Working
            : AgentActivityState.Idle;
    }

    private static bool LooksLikeInteractivePrompt(string text)
    {
        if (!ConfirmationFooter.IsMatch(text)) return false;
        return ApprovalQuestion.IsMatch(text) || ChoiceLine.Matches(text).Count >= 2;
    }

    internal static bool StateTransitionsPassForTest()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new CodexOutputActivityTracker();
        tracker.RecordOutput("I can explain the phrase: Would you like to run the following command?", now);
        if (tracker.StateAt(now) != AgentActivityState.Idle) return false;
        tracker.RecordOutput("\nWould you like to run the following command?\n1. Yes, proceed (y)\n", now.AddMilliseconds(20));
        tracker.RecordOutput("2. No, tell Codex what to do differently (esc)\nPress enter to confirm or esc to cancel", now.AddMilliseconds(40));
        if (tracker.StateAt(now.AddMilliseconds(50)) != AgentActivityState.Waiting) return false;
        tracker.UserResponded(now.AddMilliseconds(60));
        if (tracker.StateAt(now.AddMilliseconds(70)) != AgentActivityState.Working) return false;
        tracker.RecordOutput("\n1. Use the existing design\n2. Create a new design\nPress enter to select", now.AddSeconds(1));
        if (tracker.StateAt(now.AddSeconds(1.1)) != AgentActivityState.Waiting) return false;
        tracker.UserResponded(now.AddSeconds(1.2));
        return tracker.StateAt(now.AddSeconds(8)) == AgentActivityState.Idle;
    }
}

internal sealed class HermesOutputActivityTracker
{
    private static readonly Regex WaitingPrompt = new(
        @"approval(?: required| request)|waiting for (?:your|user) input|do you want to|allow (?:this|command)|\[(?:y/n|Y/n|y/N)\]|once\s*/\s*session\s*/\s*always\s*/\s*deny|clarification",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReadyPrompt = new(
        @"(?:^|\n)\s*[❯>]\s*$|Welcome to Hermes Agent! Type your message|Resume this session with:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private int state = (int)AgentActivityState.Idle;

    public AgentActivityState State => (AgentActivityState)Volatile.Read(ref state);
    public void TurnSubmitted() => Volatile.Write(ref state, (int)AgentActivityState.Working);

    public void RecordOutput(string data, bool meaningful)
    {
        var visible = TerminalTextSanitizer.ForTranscript(data);
        if (visible.Length == 0) return;
        if (WaitingPrompt.IsMatch(visible))
        {
            Volatile.Write(ref state, (int)AgentActivityState.Waiting);
            return;
        }
        if (ReadyPrompt.IsMatch(visible))
        {
            Volatile.Write(ref state, (int)AgentActivityState.Idle);
            return;
        }
        if (meaningful && State != AgentActivityState.Waiting)
            Volatile.Write(ref state, (int)AgentActivityState.Working);
    }

    internal static bool StateTransitionsPassForTest()
    {
        var tracker = new HermesOutputActivityTracker();
        tracker.TurnSubmitted();
        if (tracker.State != AgentActivityState.Working) return false;
        tracker.RecordOutput("Approval required\nonce / session / always / deny", true);
        if (tracker.State != AgentActivityState.Waiting) return false;
        tracker.TurnSubmitted();
        tracker.RecordOutput("streaming model response", true);
        if (tracker.State != AgentActivityState.Working) return false;
        tracker.RecordOutput("\n❯ ", true);
        return tracker.State == AgentActivityState.Idle;
    }
}

public partial class TerminalPane : UserControl
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmSysChar = 0x0106;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkF2 = 0x71;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkV = 0x56;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int MaximumQueuedCommands = 100;
    private const int MaximumCommandHistory = 100;
    private const int MaximumCommandLength = 32_768;
    private const int MaximumClipboardCharacters = 1_000_000;
    private const int MaximumRecoveryOutputCharacters = 512_000;
    private const int RecoveryOutputTrimThreshold = 576_000;
    private const int MaximumComposerAttachments = 10;
    private const int MinimumTerminalFontSize = 6;
    private const int MaximumTerminalFontSize = 36;
    private const int MinimumComposerFontSize = 8;
    private const int MaximumComposerFontSize = 28;
    private static readonly Regex LocalFilePathRegex = new(
        """(?<![A-Za-z0-9_])(?<path>(?:[A-Za-z]:\\|\\\\)[^\r\n"'`<>|?*]+?\.[A-Za-z0-9]{1,32})(?=$|[\s,"'`;:!?)\]}])""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WorkingDirectoryOscRegex = new(
        "\u001b\\]9;9;(?<path>\"[^\"\a\u001b]*\"|[^\a\u001b]*)(?:\a|\u001b\\\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public SessionProfile Profile { get; private set; }
    public event EventHandler? Activated;
    public event EventHandler? CloseRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? DetachRequested;
    public event EventHandler? DragRequested;
    public event Action<TerminalPane, string>? RawOutputReceived;
    private SessionRecoveryEntry? startupRecovery;
    private Point? paneHeaderDragStart;
    private bool paneIsActive;
    private string previousOutput = string.Empty;
    private TerminalContainer? terminalContainer;
    private HwndSourceHook? terminalInternalMessageHook;
    private bool terminalMessageRouterInstalled;
    private readonly WindowSubclassProc terminalWindowSubclassProc;
    private bool terminalWindowSubclassInstalled;
    private bool terminalThreadMessageHookInstalled;
    private TermPTY? outputCaptureTerminal;
    private readonly Func<IEnumerable<CommandSnippet>> quickAccessProvider;
    private readonly Func<IEnumerable<AutomationRule>> automationProvider;
    private readonly Action<AutomationRule> automationEditRequested;
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
    private readonly System.Windows.Threading.DispatcherTimer agentStatusTimer = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly System.Windows.Threading.DispatcherTimer composerStateTimer = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(300) };
    private bool composerStateDirty;
    private int composerStateFlushCount;
    private readonly object agentOutputSync = new();
    private readonly StringBuilder recentAgentOutput = new();
    private readonly StringBuilder recoveryOutput = new();
    private readonly StringBuilder workingDirectoryOscBuffer = new();
    private volatile bool lastRecoverySnapshotReadUsedDispatcher;
    private DateTime lastAgentProbeUtc = DateTime.MinValue;
    private bool agentStatusRefreshPending;
    private bool forceAgentStatusRefreshQueued;
    private readonly TerminalOutputActivityTracker terminalActivity = new();
    private readonly BracketedPasteModeTracker bracketedPasteMode = new();
    private readonly CodexOutputActivityTracker codexOutputActivity = new();
    private readonly HermesOutputActivityTracker hermesActivity = new();
    private string? activeCodexSessionId;
    private AgentKind detectedAgentKind;
    private AgentKind displayedAgentKind = (AgentKind)(-1);
    private bool hermesExitObserved;
    private bool remoteCodexActivityProbePending;
    private DateTime nextRemoteCodexActivityProbeUtc = DateTime.MinValue;
    private CodexTurnActivity remoteCodexActivity;
    private bool remoteImagePastePending;
    private bool suppressRemoteImagePasteVSequence;
    private Func<RemoteImagePasteMode, bool>? remoteClipboardPasteTestOverride;
    private (bool Control, bool Alt, bool Shift)? terminalShortcutTestModifiers;
    private long terminalThreadMessageInterceptCount;
    private long terminalTabInterceptCount;
    private Func<bool>? terminalTabForwardTestOverride;
    private long terminalInternalMessageForwardCount;
    private long remoteImageIndicatorVersion;
    private AgentActivityState agentActivityState = AgentActivityState.Starting;
    private readonly List<ComposerAttachment> composerAttachments = [];
    private readonly Dictionary<Border, Border> attachmentDropIndicators = [];
    private TerminalAppearance currentAppearance;
    private bool synchronizingComposerAttachments;
    private Point? attachmentDragStart;
    private string? attachmentDragId;
    private bool attachmentDragOccurred;
    private Visibility terminalVisibilityBeforeAttachmentPreview = Visibility.Visible;
    private bool startupProfileFallbackAttempted;
    private readonly TaskCompletionSource<bool> startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int attachmentPillRefreshCount;
    private bool commandInputFileDropHandlersInstalled;
    private ScrollBar? nativeScrollbar;
    private TerminalContainer? nativeScrollContainer;
    private Delegate? nativeTerminalScrolledHandler;
    private bool nativeTerminalScrollCallbackObserved;
    private bool terminalScrollbarBridgeAttached;
    private bool terminalScrollbarUpdating;
    private (double Value, double Maximum, double ViewportSize)? terminalScrollbarState;
    private int terminalScrollbarRefreshPending;
    private volatile bool terminalScrollbarRangeObserved;
    private readonly System.Windows.Threading.DispatcherTimer tmuxScrollbarRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly System.Windows.Threading.DispatcherTimer tmuxScrollbarApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(24) };
    private RemoteTmuxScrollbackState tmuxScrollbackState;
    private RemoteTmuxScrollbackClient? tmuxScrollbackClient;
    private bool tmuxScrollbarProbeInFlight;
    private bool tmuxScrollbarProbeRequested;
    private bool tmuxScrollbarApplyInFlight;
    private int? pendingTmuxScrollPosition;
    private long tmuxScrollbarGeneration;
    private static readonly EventInfo? NativeTerminalScrolledEvent = typeof(TerminalContainer).GetEvent("TerminalScrolled", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? NativeTerminalUserScrollMethod = typeof(TerminalContainer).GetMethod("UserScroll", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? NativeTerminalWheelMethod = typeof(TerminalControl).GetMethod("TermControl_UserScrolled", BindingFlags.Instance | BindingFlags.NonPublic);

    public TerminalPane(SessionProfile profile, TerminalAppearance appearance, SessionRecoveryEntry? recovery = null, string? recoveredOutput = null,
        Func<IEnumerable<CommandSnippet>>? quickAccessProvider = null, Action? commandStateChanged = null,
        Func<string, Task<bool>>? sendAllCommand = null, Func<bool>? sendAllModifierEnabled = null, Func<ModifierKeys>? sendAllModifier = null,
        Func<IEnumerable<AutomationRule>>? automationProvider = null, Action<AutomationRule>? automationEditRequested = null)
    {
        Profile = profile;
        Profile.SetTmuxTerminal(recovery is { SshWasActive: true, RemoteTmuxManaged: true });
        currentAppearance = appearance;
        terminalWindowSubclassProc = TerminalWindowSubclassProc;
        startupRecovery = recovery;
        previousOutput = recoveredOutput ?? string.Empty;
        this.quickAccessProvider = quickAccessProvider ?? (() => []);
        this.automationProvider = automationProvider ?? (() => []);
        this.automationEditRequested = automationEditRequested ?? (_ => { });
        this.commandStateChanged = commandStateChanged ?? (() => { });
        this.sendAllCommand = sendAllCommand ?? SendCommandAsync;
        this.sendAllModifierEnabled = sendAllModifierEnabled ?? (() => true);
        this.sendAllModifier = sendAllModifier ?? (() => ModifierKeys.Shift);
        remoteFontFace = appearance.FontFace;
        remoteFontSize = EffectiveTerminalFontSize(appearance);
        Profile.PendingCommands ??= [];
        Profile.CommandHistory ??= [];
        Profile.CommandHistoryTimestampsUtc ??= [];
        Profile.CommandDraft ??= string.Empty;
        Profile.ComposerAttachments ??= [];
        Profile.AutomationBindings ??= [];
        if (recovery?.SshWasActive == true && !string.IsNullOrWhiteSpace(recovery.RemoteCodexWorkingDirectory))
        {
            Profile.LiveWorkingDirectory = recovery.RemoteCodexWorkingDirectory;
            Profile.LiveWorkingDirectoryIsSsh = true;
        }
        InitializeComponent();
        ApplyAccent();
        ConfigureComposerFileDrop();
        RestoreComposerAttachments();
        Profile.CommandDraft = StripRedundantAttachmentQuotes(Profile.CommandDraft, composerAttachments.Select(value => value.LocalPath));
        CommandInput.ApplyComposerFontSize(Profile.CommandFontSize ?? 11);
        CommandInput.Text = Profile.CommandDraft;
        CommandInput.CaretIndex = CommandInput.Text.Length;
        CommandInput.TextChanged += CommandInputTextChanged;
        CommandInput.PlainTextPasted += PromotePastedLocalFiles;
        composerStateTimer.Tick += (_, _) => FlushComposerState();
        RefreshAttachmentPills();
        detectedAgentKind = recovery?.HermesWasActive == true ? AgentKind.Hermes
            : recovery?.CodexWasActive == true || recovery?.RemoteCodexWasActive == true ? AgentKind.Codex : AgentKind.Terminal;
        activeCodexSessionId = recovery?.RemoteCodexWasActive == true ? recovery.RemoteCodexSessionId : recovery?.CodexSessionId;
        SetAgentStatus(detectedAgentKind, AgentActivityState.Starting);
        agentStatusTimer.Tick += (_, _) =>
        {
            RefreshAgentStatus();
            if (CommandHistoryPanel.Visibility == Visibility.Visible) CommandHistoryList.Items.Refresh();
        };
        tmuxScrollbarRefreshTimer.Tick += async (_, _) =>
        {
            tmuxScrollbarRefreshTimer.Stop();
            await RefreshTmuxScrollbarAsync();
        };
        tmuxScrollbarApplyTimer.Tick += async (_, _) =>
        {
            tmuxScrollbarApplyTimer.Stop();
            await ApplyPendingTmuxScrollAsync();
        };
        Terminal.SizeChanged += (_, _) => ScheduleRemoteDimensionRefresh();
        Terminal.Terminal.SizeChanged += (_, _) => ScheduleRemoteDimensionRefresh();
        SetCommandBarExpanded(Profile.CommandBarExpanded, false, false);
        UpdateQueueDisplay();
        UpdateAutomationDisplay();
        RefreshCommandHistoryList();
        UpdateSendButtonVisual(false);
        AttachTerminalActivationHook();
        TitleText.Text = profile.Name;
        startupProfileFallbackAttempted = false;
        Terminal.StartupCommandLine = BuildCommandLine(profile, recovery);
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        Terminal.FontSizeWhenSettingTheme = EffectiveTerminalFontSize(appearance);
        Terminal.Theme = appearance.Theme;
        configuredCursorStyleCode = CursorStyleCode(appearance.Theme.CursorStyle);
        AttachTerminalOutputFilter();
        Loaded += async (_, _) =>
        {
            agentStatusTimer.Start();
            RegisterTerminalThreadMessageHook();
            AttachTerminalActivationHook();
            AttachTerminalOutputFilter();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            RefreshRemoteDimensions();
            ConfigureNativeScrollbar();
            UpdateHeaderStatus();
            await Task.Delay(1400);
            if (Terminal.ConPTYTerm?.TermProcIsStarted != true)
            {
                try
                {
                    var term = Terminal.ConPTYTerm;
                    var commandLine = Terminal.StartupCommandLine;
                    await Task.Run(() => term!.Start(commandLine, 100, 30, false));
                }
                catch (Exception exception)
                {
                    SetAgentStatus(detectedAgentKind, AgentActivityState.Error);
                    Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
                    File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] {exception}\n");
                    startupReady.TrySetResult(false);
                    return;
                }
            }
            RefreshAgentStatus(true);
            AttachTerminalOutputFilter();
            RefreshRemoteDimensions();
            ConfigureRecoveryView();
            await RecoverFromStalledPowerShellProfileAsync();
            QueueTmuxScrollbarRefresh(true);
            startupReady.TrySetResult(Terminal.ConPTYTerm?.TermProcIsStarted == true);
        };
        Unloaded += (_, _) =>
        {
            FlushComposerState();
            agentStatusTimer.Stop();
            tmuxScrollbarRefreshTimer.Stop();
            tmuxScrollbarApplyTimer.Stop();
            Interlocked.Increment(ref tmuxScrollbarGeneration);
            UnregisterTerminalThreadMessageHook();
            DetachNativeTerminalScrollBridge();
        };
    }

    public void SetActive(bool active)
    {
        paneIsActive = active;
        PaneBorder.BorderBrush = active ? WorkspaceAccentPalette.BrushFor(Profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal) : new SolidColorBrush(Color.FromRgb(49, 50, 68));
        PaneBorder.BorderThickness = active ? new Thickness(1.5) : new Thickness(1);
    }

    public void SetDropTargetIndicator(bool visible, bool after)
    {
        PaneDropMarker.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PaneDropMarker.HorizontalAlignment = after ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PaneDropMarker.Margin = after ? new Thickness(0, 3, -8, 3) : new Thickness(-8, 3, 0, 3);
        PaneHeader.Background = visible
            ? new SolidColorBrush(Color.FromRgb(36, 36, 56))
            : new SolidColorBrush(Color.FromRgb(24, 24, 37));
        if (visible)
        {
            PaneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 166, 247));
            PaneBorder.BorderThickness = new Thickness(2);
        }
        else SetActive(paneIsActive);
    }

    internal bool HeaderDragContractPassesForTest => PaneHeader.Cursor == Cursors.SizeAll
        && PaneHeaderActions.Cursor == Cursors.Arrow && PaneDropMarker.IsHitTestVisible == false;

    internal Task<bool> StartupReady => startupReady.Task;

    private void ApplyAccent()
    {
        var accent = WorkspaceAccentPalette.BrushFor(Profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal);
        PaneAccentBar.Background = accent;
        if (PaneBorder.BorderThickness.Left > 1) PaneBorder.BorderBrush = accent;
    }

    public bool HasTerminalSurfaceActivationHook => terminalContainer is not null && terminalMessageRouterInstalled && terminalWindowSubclassInstalled;

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

    public Task<bool> SendCommandAsync(string command) => SendCommandAsync(command, false);

    public Task<bool> SendComposerCommandAsync(string command)
        => SendCommandAsync(command, Profile.PressEnterAfterComposerSend);

    private async Task<bool> SendCommandAsync(string command, bool pressEnterAfterInsert)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var restarted = false;
        try
        {
            if (Terminal.ConPTYTerm?.Process?.HasExited == true)
            {
                bracketedPasteMode.Reset();
                await Terminal.RestartTerm();
                AttachTerminalOutputFilter();
                await RebindNativeScrollbarAfterRestartAsync();
                restarted = true;
            }
        }
        catch (ArgumentException)
        {
            try { bracketedPasteMode.Reset(); await Terminal.RestartTerm(); AttachTerminalOutputFilter(); await RebindNativeScrollbarAfterRestartAsync(); restarted = true; } catch { return false; }
        }
        catch (InvalidOperationException)
        {
            try { bracketedPasteMode.Reset(); await Terminal.RestartTerm(); AttachTerminalOutputFilter(); await RebindNativeScrollbarAfterRestartAsync(); restarted = true; } catch { return false; }
        }
        if (restarted) await Task.Delay(900);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Terminal.ConPTYTerm?.TermProcIsStarted == true)
                {
                    var now = DateTime.UtcNow;
                    terminalActivity.RecordInput(now);
                    if (detectedAgentKind == AgentKind.Codex) codexOutputActivity.UserResponded(now);
                    if (detectedAgentKind == AgentKind.Hermes) hermesActivity.TurnSubmitted();
                    Terminal.ConPTYTerm.WriteToTerm(bracketedPasteMode.FormatSubmission(command));
                    if (pressEnterAfterInsert)
                    {
                        // Some full-screen agents accept a bracketed paste into
                        // their editor without submitting it. A separate Enter is
                        // intentionally delayed so it cannot become part of the
                        // bracketed-paste payload itself.
                        await Task.Delay(35);
                        if (Terminal.ConPTYTerm?.TermProcIsStarted == true)
                            Terminal.ConPTYTerm.WriteToTerm("\r");
                    }
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

    public async Task<bool> RunAutomationAsync(AutomationRule automation)
    {
        if (string.IsNullOrWhiteSpace(automation.Command)) return false;
        if (automation.ClearLine)
        {
            queueSelectionIndex = null;
            queueNavigationDraft = string.Empty;
            CommandInput.Clear();
            FlushComposerState();
        }
        return await SendCommandAsync(BuildAutomationTerminalInput(automation));
    }

    private static string BuildAutomationTerminalInput(AutomationRule automation)
        // Ctrl+A + Ctrl+K clears the active ReadLine/PSReadLine buffer in
        // both common PowerShell and bash Emacs bindings before execution.
        => (automation.ClearLine ? "\u0001\u000b" : string.Empty) + automation.Command;

    private string AppendEnabledAutomationText(string command)
    {
        var additions = (from binding in Profile.AutomationBindings
                         where binding.Enabled && binding.AutoInsertAtEnd
                         join automation in automationProvider() on binding.AutomationId equals automation.Id
                         where !string.IsNullOrWhiteSpace(automation.Command)
                         select automation.Command.Trim()).ToArray();
        return additions.Length == 0 ? command : command.TrimEnd() + "\n" + string.Join("\n", additions);
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

    private void RecordCommandHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        Profile.CommandHistory.Add(command);
        Profile.CommandHistoryTimestampsUtc.Add(DateTime.UtcNow);
        if (Profile.CommandHistory.Count > MaximumCommandHistory)
        {
            Profile.CommandHistory.RemoveRange(0, Profile.CommandHistory.Count - MaximumCommandHistory);
            Profile.CommandHistoryTimestampsUtc.RemoveRange(0, Profile.CommandHistoryTimestampsUtc.Count - MaximumCommandHistory);
        }
        RefreshCommandHistoryList();
    }

    private void RefreshCommandHistoryList()
    {
        NormalizeCommandHistoryTimestamps();
        CommandHistoryList.ItemsSource = Enumerable.Range(0, Profile.CommandHistory.Count)
            .Reverse()
            .Select(index => new HistoryDisplayEntry(Profile.CommandHistory[index], Profile.CommandHistoryTimestampsUtc[index]))
            .ToArray();
        CommandHistoryEmptyText.Visibility = Profile.CommandHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandHistoryButton.IsEnabled = true;
        CommandHistoryButton.Opacity = Profile.CommandHistory.Count > 0 ? 1 : .72;
        ClearCommandHistoryButton.IsEnabled = Profile.CommandHistory.Count > 0;
        ClearCommandHistoryButton.Opacity = Profile.CommandHistory.Count > 0 ? 1 : .55;
    }

    private void ClearCommandHistory()
    {
        Profile.CommandHistory.Clear();
        Profile.CommandHistoryTimestampsUtc.Clear();
        RefreshCommandHistoryList();
        commandStateChanged();
    }

    private void NormalizeCommandHistoryTimestamps()
    {
        Profile.CommandHistoryTimestampsUtc ??= [];
        if (Profile.CommandHistoryTimestampsUtc.Count > Profile.CommandHistory.Count)
            Profile.CommandHistoryTimestampsUtc.RemoveRange(0, Profile.CommandHistoryTimestampsUtc.Count - Profile.CommandHistory.Count);
        if (Profile.CommandHistoryTimestampsUtc.Count < Profile.CommandHistory.Count)
            Profile.CommandHistoryTimestampsUtc.InsertRange(0, Enumerable.Repeat(DateTime.MinValue, Profile.CommandHistory.Count - Profile.CommandHistoryTimestampsUtc.Count));
    }

    internal static string FormatRelativeHistoryTime(DateTime sentUtc, DateTime nowUtc)
    {
        if (sentUtc == DateTime.MinValue) return "saved";
        var elapsed = nowUtc - (sentUtc.Kind == DateTimeKind.Utc ? sentUtc : sentUtc.ToUniversalTime());
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds))}s";
        if (elapsed.TotalMinutes < 60) return $"{(int)Math.Floor(elapsed.TotalMinutes)}m";
        if (elapsed.TotalHours < 24) return $"{(int)Math.Floor(elapsed.TotalHours)}h";
        if (elapsed.TotalDays < 7) return $"{(int)Math.Floor(elapsed.TotalDays)}d";
        return sentUtc.ToLocalTime().ToString("MMM d");
    }

    private void SetCommandHistoryVisible(bool visible)
    {
        CommandHistoryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CommandHistoryIcon.Stroke = new SolidColorBrush(visible ? Color.FromRgb(137, 180, 250) : Color.FromRgb(166, 173, 200));
        CommandHistoryButton.ToolTip = visible ? "Hide history" : "Show history";
    }

    private void RestoreCommandHistory(string command)
    {
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        CommandInput.Text = command;
        var historyPaths = DiscoverExistingLocalFiles(command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleAttachments = composerAttachments
            .Where(value => !historyPaths.Contains(value.LocalPath))
            .ToArray();
        if (staleAttachments.Length > 0) RemoveComposerAttachments(staleAttachments);
        // History is another composer input source. Re-run the canonical
        // attachment discovery so saved paths regain tokens, pills, previews,
        // and SSH-transfer behavior just like pasted or dropped paths.
        PromotePastedLocalFiles(command);
        CommandInput.CaretIndex = CommandInput.Text.Length;
        SetCommandHistoryVisible(false);
        CommandInput.Focus();
    }

    private async Task<bool> RunCommandInputAsync(bool sendToAll = false)
    {
        if (commandExecutionPending) return false;
        var composerCommand = CommandInput.Text.Trim();
        var command = AppendEnabledAutomationText(composerCommand);
        if (command.Length == 0 || command.Length > MaximumCommandLength) return false;
        var referencedAttachments = composerAttachments.Where(value => command.Contains(value.LocalPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sendToAll && referencedAttachments.Length > 0)
        {
            ShowRemoteImageStatus("Choose one terminal", "Attached files are transferred for the current terminal's SSH connection. Send without the all-terminals modifier.", false, true);
            return false;
        }
        commandExecutionPending = true;
        RunCommandButton.IsEnabled = false;
        try
        {
            var queuedIndex = queueSelectionIndex is int selected && selected >= 0 && selected < Profile.PendingCommands.Count
                && string.Equals(Profile.PendingCommands[selected], composerCommand, StringComparison.Ordinal)
                    ? selected
                    : (int?)null;
            var preparedCommand = await PrepareComposerCommandAsync(command, referencedAttachments);
            if (preparedCommand is null) return false;
            if (!await (sendToAll ? sendAllCommand(preparedCommand) : SendComposerCommandAsync(preparedCommand))) return false;
            RecordCommandHistory(command);
            if (queuedIndex is int index) Profile.PendingCommands.RemoveAt(index);
            RemoveComposerAttachments(referencedAttachments);
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

    private async Task<string?> PrepareComposerCommandAsync(string command, IReadOnlyList<ComposerAttachment> attachments)
    {
        if (attachments.Count == 0 || !TryGetActiveSshConnection(out var connectionArguments)) return command;
        ShowRemoteImageStatus($"Uploading {attachments.Count} file{(attachments.Count == 1 ? string.Empty : "s")}…",
            "Securely copying composer attachments through the verified SSH connection", true);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in attachments)
        {
            var result = await RemoteClipboardFileBridge.UploadFileAsync(attachment.LocalPath, connectionArguments);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.RemotePath))
            {
                ShowRemoteImageStatus("Attachment upload failed", result.Error ?? $"Could not upload {attachment.DisplayName}.", false, true);
                return null;
            }
            replacements[attachment.LocalPath] = result.RemotePath;
        }
        ShowRemoteImageStatus("Attachments ready", "Local paths were replaced with private VPS paths", false, true);
        return RewriteAttachmentPaths(command, replacements);
    }

    private static string RewriteAttachmentPaths(string command, IReadOnlyDictionary<string, string> replacements)
    {
        var rewritten = command;
        foreach (var replacement in replacements.OrderByDescending(value => value.Key.Length))
            rewritten = rewritten.Replace(replacement.Key, replacement.Value, StringComparison.OrdinalIgnoreCase);
        return rewritten;
    }

    private void RemoveComposerAttachments(IEnumerable<ComposerAttachment> attachments)
    {
        foreach (var attachment in attachments.ToArray())
        {
            composerAttachments.Remove(attachment);
            if (attachment.IsTemporary)
            {
                try { File.Delete(attachment.LocalPath); } catch { }
            }
        }
        RefreshAttachmentPills();
        PersistComposerAttachments();
        if (composerAttachments.Count == 0) CloseAttachmentPreview();
    }

    private void RestoreComposerAttachments()
    {
        foreach (var saved in Profile.ComposerAttachments.Take(MaximumComposerAttachments))
        {
            try
            {
                var fullPath = Path.GetFullPath(saved.LocalPath);
                if (!File.Exists(fullPath) || !Profile.CommandDraft.Contains(fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                composerAttachments.Add(new ComposerAttachment(Guid.NewGuid().ToString("N"), fullPath,
                    string.IsNullOrWhiteSpace(saved.DisplayName) ? Path.GetFileName(fullPath) : saved.DisplayName,
                    saved.IsImage || IsImageFile(fullPath), saved.IsTemporary));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        PersistComposerAttachments(false);
    }

    private void PersistComposerAttachments(bool notify = true)
    {
        Profile.ComposerAttachments = composerAttachments.Select(value => new ComposerAttachmentState
        {
            LocalPath = value.LocalPath,
            DisplayName = value.DisplayName,
            IsImage = value.IsImage,
            IsTemporary = value.IsTemporary
        }).ToList();
        if (notify) commandStateChanged();
    }

    private void CommandInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (synchronizingComposerAttachments) return;
        composerStateDirty = true;
        // True trailing-edge debounce: persistence and attachment reconciliation
        // run only after typing has gone quiet, never repeatedly inside a burst.
        composerStateTimer.Stop();
        composerStateTimer.Start();
    }

    private void FlushComposerState()
    {
        composerStateTimer.Stop();
        if (!composerStateDirty) return;
        composerStateDirty = false;
        composerStateFlushCount++;
        Profile.CommandDraft = CommandInput.Text;
        var detached = composerAttachments
            .Where(value => !CommandInput.Text.Contains(value.LocalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (detached.Length > 0) RemoveComposerAttachments(detached);
        else
        {
            if (composerAttachments.Count > 1 && SynchronizeComposerAttachmentOrder()) RefreshAttachmentPills();
            commandStateChanged();
        }
    }

    private void ConfigureComposerFileDrop()
    {
        CommandInput.AllowDrop = true;
        CommandInput.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(CommandInputFileDragOver), true);
        CommandInput.AddHandler(DragDrop.DragOverEvent, new DragEventHandler(CommandInputFileDragOver), true);
        CommandInput.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler(CommandInputFileDragLeave), true);
        CommandInput.AddHandler(DragDrop.DropEvent, new DragEventHandler(CommandInputFileDrop), true);
        commandInputFileDropHandlersInstalled = true;
    }

    private void CommandInputFileDragOver(object sender, DragEventArgs e)
    {
        if (!HasShellFileDrop(e.Data)) return;
        var accepted = GetDroppedComposerFiles(e.Data).Count > 0;
        ComposerFileDropIndicator.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void CommandInputFileDragLeave(object sender, DragEventArgs e)
    {
        if (!HasShellFileDrop(e.Data)) return;
        ComposerFileDropIndicator.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void CommandInputFileDrop(object sender, DragEventArgs e)
    {
        if (!HasShellFileDrop(e.Data)) return;
        ComposerFileDropIndicator.Visibility = Visibility.Collapsed;
        var files = GetDroppedComposerFiles(e.Data);
        if (files.Count == 0)
        {
            ShowRemoteImageStatus("File cannot be attached", "Drop a non-empty local file smaller than 100 MB.", false, true);
            e.Effects = DragDropEffects.None;
        }
        else
        {
            AttachDroppedComposerFiles(files);
            e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void AttachDroppedComposerFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, MaximumComposerAttachments - composerAttachments.Count)))
            AddComposerAttachment(path, IsImageFile(path), false, true);
        CommandInput.Focus();
    }

    private static bool HasShellFileDrop(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop, true); }
        catch { return false; }
    }

    private static IReadOnlyList<string> GetDroppedComposerFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, true)
                || data.GetData(DataFormats.FileDrop, true) is not string[] paths) return [];
            var files = new List<string>();
            foreach (var path in paths)
            {
                if (TryNormalizeComposerFile(path, out var fullPath)
                    && !files.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) files.Add(fullPath);
            }
            return files;
        }
        catch { return []; }
    }

    private static bool TryNormalizeComposerFile(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            return file.Exists && file.Length is > 0 and <= RemoteClipboardFileBridge.MaximumFileBytes;
        }
        catch { return false; }
    }

    private void PromotePastedLocalFiles(string pastedText)
    {
        var recognizedPaths = DiscoverExistingLocalFiles(pastedText).ToArray();
        var paths = recognizedPaths
            .Where(path => !composerAttachments.Any(value => value.LocalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, MaximumComposerAttachments - composerAttachments.Count))
            .ToArray();
        foreach (var path in paths) AddComposerAttachment(path, IsImageFile(path), false, false);

        if (recognizedPaths.Length == 0) return;
        var withoutQuotes = StripRedundantAttachmentQuotes(CommandInput.Text, recognizedPaths);
        if (!string.Equals(withoutQuotes, CommandInput.Text, StringComparison.Ordinal))
        {
            CommandInput.Text = withoutQuotes;
            CommandInput.CaretIndex = CommandInput.Text.Length;
        }
        RefreshAttachmentPills();
    }

    private static IEnumerable<string> DiscoverExistingLocalFiles(string text)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exact = NormalizeLocalFileCandidate(text);
        if (exact is not null && discovered.Add(exact)) yield return exact;
        foreach (Match match in LocalFilePathRegex.Matches(text))
        {
            var candidate = NormalizeLocalFileCandidate(match.Groups["path"].Value);
            if (candidate is not null && discovered.Add(candidate)) yield return candidate;
        }
    }

    private static string? NormalizeLocalFileCandidate(string value)
    {
        var candidate = value.Trim().Trim('"', '\'', '`');
        if (candidate.Length == 0 || !Path.IsPathFullyQualified(candidate)) return null;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string StripRedundantAttachmentQuotes(string command, IEnumerable<string> paths)
    {
        foreach (var path in paths.OrderByDescending(value => value.Length))
        {
            command = command.Replace($"\"{path}\"", path, StringComparison.OrdinalIgnoreCase)
                .Replace($"'{path}'", path, StringComparison.OrdinalIgnoreCase)
                .Replace($"`{path}`", path, StringComparison.OrdinalIgnoreCase);
        }
        return command;
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
            RunCommandButton.ToolTip = $"Send to all terminals ({SendToAllModifierLabel()}+Enter) · Right-click for send behavior";
        }
        else
        {
            RunCommandButton.Content = "▶";
            RunCommandButton.ClearValue(ForegroundProperty);
            RunCommandButton.ClearValue(BackgroundProperty);
            RunCommandButton.ClearValue(BorderBrushProperty);
            var behavior = Profile.PressEnterAfterComposerSend ? " · extra Enter enabled" : " · right-click for send behavior";
            RunCommandButton.ToolTip = (sendAllModifierEnabled()
                ? $"Run in this terminal (Enter) · Hold {SendToAllModifierLabel()} for all terminals"
                : "Run in this terminal (Enter)") + behavior;
        }
    }

    private void RefreshSendButtonVisual() => UpdateSendButtonVisual(IsSendToAllActive(Keyboard.Modifiers));

    private ContextMenu BuildRunCommandSettingsMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = RunCommandButton,
            Placement = PlacementMode.Top,
            VerticalOffset = -4,
            Style = TryFindResource("CardContextMenu") as Style
        };
        menu.Items.Add(new MenuItem
        {
            Header = "SEND BEHAVIOR",
            IsEnabled = false,
            Style = TryFindResource("CardMenuItem") as Style
        });
        var pressEnter = new MenuItem
        {
            Header = "Press Enter automatically",
            ToolTip = "After inserting the composer message, send one separate Enter key to submit it in full-screen agents.",
            IsCheckable = true,
            IsChecked = Profile.PressEnterAfterComposerSend,
            Style = TryFindResource("CardMenuItem") as Style
        };
        pressEnter.Click += (_, _) =>
        {
            Profile.PressEnterAfterComposerSend = pressEnter.IsChecked == true;
            commandStateChanged();
            UpdateSendButtonVisual(sendButtonShowsAll == true, true);
        };
        menu.Items.Add(pressEnter);
        return menu;
    }

    private void ShowRunCommandSettingsMenu()
    {
        RunCommandButton.ContextMenu = BuildRunCommandSettingsMenu();
        RunCommandButton.ContextMenu.IsOpen = true;
    }

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

    private void UpdateAutomationDisplay()
    {
        var availableIds = automationProvider().Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var count = Profile.AutomationBindings.Count(value => availableIds.Contains(value.AutomationId));
        AutomationCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationCountText.Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutomationButton.ToolTip = count == 0 ? "Terminal automations · none added" : $"Terminal automations · {count} added";
    }

    private MenuItem AutomationMenuItem(string header, string? gesture = null)
        => new()
        {
            Header = header,
            InputGestureText = gesture ?? string.Empty,
            Style = TryFindResource("CardMenuItem") as Style
        };

    private void ShowAutomationMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = AutomationButton,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = -4,
            MinWidth = 290,
            MaxHeight = 420,
            Style = TryFindResource("CardContextMenu") as Style
        };
        var automations = automationProvider().Where(value => !string.IsNullOrWhiteSpace(value.Command)).ToList();
        var automationById = automations.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var assigned = Profile.AutomationBindings
            .Where(value => automationById.ContainsKey(value.AutomationId))
            .ToList();
        if (assigned.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No automations added to this terminal",
                IsEnabled = false,
                Style = TryFindResource("CardMenuItem") as Style
            });
        }
        else
        {
            foreach (var binding in assigned)
            {
                var automation = automationById[binding.AutomationId];
                var automationItem = AutomationMenuItem(automation.Name, binding.Enabled ? "Enabled" : "Disabled");
                automationItem.ToolTip = automation.Command;

                var runItem = AutomationMenuItem("Run now", automation.ClearLine ? "Clears line" : null);
                runItem.Icon = new TextBlock { Text = "▶", Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161)), FontSize = 11 };
                runItem.Click += async (_, _) => await RunAutomationAsync(automation);
                automationItem.Items.Add(runItem);

                var enabledItem = AutomationMenuItem("Enabled");
                enabledItem.IsCheckable = true;
                enabledItem.IsChecked = binding.Enabled;
                enabledItem.Click += (_, _) =>
                {
                    binding.Enabled = enabledItem.IsChecked;
                    commandStateChanged();
                    UpdateAutomationDisplay();
                };
                automationItem.Items.Add(enabledItem);

                var insertItem = AutomationMenuItem("Auto insert at end of input");
                insertItem.IsCheckable = true;
                insertItem.IsChecked = binding.AutoInsertAtEnd;
                insertItem.ToolTip = "Append this automation's text on a new line whenever this terminal's composer sends";
                insertItem.Click += (_, _) =>
                {
                    binding.AutoInsertAtEnd = insertItem.IsChecked;
                    commandStateChanged();
                };
                automationItem.Items.Add(insertItem);
                automationItem.Items.Add(new Separator());

                var editItem = AutomationMenuItem("Edit automation");
                editItem.Click += (_, _) => automationEditRequested(automation);
                automationItem.Items.Add(editItem);
                var removeItem = new MenuItem { Header = "Remove from terminal", Style = TryFindResource("CardDangerMenuItem") as Style };
                removeItem.Click += (_, _) =>
                {
                    Profile.AutomationBindings.Remove(binding);
                    commandStateChanged();
                    UpdateAutomationDisplay();
                };
                automationItem.Items.Add(removeItem);
                menu.Items.Add(automationItem);
            }
        }

        menu.Items.Add(new Separator());
        var addItem = AutomationMenuItem("Add automation");
        var assignedIds = assigned.Select(value => value.AutomationId).ToHashSet(StringComparer.Ordinal);
        foreach (var automation in automations.Where(value => !assignedIds.Contains(value.Id)))
        {
            var candidate = AutomationMenuItem(automation.Name, automation.Subtitle);
            candidate.ToolTip = automation.Command;
            candidate.Click += (_, _) =>
            {
                Profile.AutomationBindings.Add(new TerminalAutomationBinding { AutomationId = automation.Id });
                commandStateChanged();
                UpdateAutomationDisplay();
            };
            addItem.Items.Add(candidate);
        }
        if (addItem.Items.Count == 0)
            addItem.Items.Add(new MenuItem { Header = automations.Count == 0 ? "Create an automation in Automate first" : "Every automation is already added", IsEnabled = false, Style = TryFindResource("CardMenuItem") as Style });
        menu.Items.Add(addItem);
        AutomationButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SetCommandBarExpanded(bool expanded, bool animate, bool persist)
    {
        if (!expanded) SetCommandHistoryVisible(false);
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

    public async Task RestartAsync(SessionRecoveryEntry? recoveryOverride = null)
    {
        var sshRecovery = recoveryOverride?.SshWasActive == true ? recoveryOverride
            : startupRecovery?.SshWasActive == true ? startupRecovery : null;
        SetAgentStatus(detectedAgentKind, AgentActivityState.Starting);
        startupRecovery = sshRecovery;
        codexOutputActivity.Reset();
        Profile.SetTmuxTerminal(sshRecovery is { SshWasActive: true, RemoteTmuxManaged: true });
        DisposeTmuxScrollbackClient();
        Interlocked.Increment(ref tmuxScrollbarGeneration);
        tmuxScrollbackState = default;
        pendingTmuxScrollPosition = null;
        hermesExitObserved = false;
        Terminal.StartupCommandLine = BuildCommandLine(Profile, sshRecovery);
        bracketedPasteMode.Reset();
        await Terminal.RestartTerm();
        AttachTerminalOutputFilter();
        await RebindNativeScrollbarAfterRestartAsync();
        agentActivityState = AgentActivityState.Starting;
        RefreshAgentStatus(true);
        QueueTmuxScrollbarRefresh(true);
        Terminal.Focus();
    }

    public void Stop()
    {
        ReleaseAuxiliaryResources();
        try { Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
        SetAgentStatus(detectedAgentKind, AgentActivityState.Stopped);
    }

    internal void ReleaseAuxiliaryResources() => DisposeTmuxScrollbackClient();

    public void SetHandoffPending(bool pending)
    {
        DetachButton.IsEnabled = !pending;
        DetachButton.Content = pending ? "…" : ">_";
        DetachButton.ToolTip = pending ? "Verifying Windows Terminal handoff…" : "Move to Windows Terminal";
    }

    public string GetOutput()
    {
        try
        {
            var raw = GetCapturedRawOutput();
            return TerminalTextSanitizer.ForTranscript(raw);
        }
        catch { return string.Empty; }
    }

    internal string GetRecoveryOutputForSnapshot()
    {
        lastRecoverySnapshotReadUsedDispatcher = Dispatcher.CheckAccess();
        return GetOutput();
    }

    private string GetCapturedRawOutput()
    {
        lock (agentOutputSync) return recoveryOutput.ToString();
    }

    private async Task RecoverFromStalledPowerShellProfileAsync()
    {
        if (startupProfileFallbackAttempted
            || !IsPowerShellCommand(Profile.CommandLine)
            || Profile.CommandLine.Contains("-NoProfile", StringComparison.OrdinalIgnoreCase)) return;
        // First-run package validation can make prompt helpers legitimately slow.
        // Give the real profile enough time to finish before considering recovery.
        await Task.Delay(10000);
        if (!IsLoaded || Terminal.ConPTYTerm?.TermProcIsStarted != true || GetRootProcessId() is not int processId) return;
        IReadOnlyList<ConsoleDescendantProcess> descendants;
        try { descendants = ProcessTreeInspector.FindDescendantProcesses(processId); }
        catch { return; }
        if (!ShouldRecoverStalledProfile(GetOutput(), descendants)) return;

        startupProfileFallbackAttempted = true;
        SetAgentStatus(detectedAgentKind, AgentActivityState.Starting);
        try
        {
            Terminal.StartupCommandLine = BuildCommandLine(Profile, startupRecovery, true);
            bracketedPasteMode.Reset();
            await Terminal.RestartTerm();
            AttachTerminalOutputFilter();
            await RebindNativeScrollbarAfterRestartAsync();
            await Task.Delay(600);
            RefreshRemoteDimensions();
            RefreshAgentStatus(true);
        }
        catch (Exception exception)
        {
            SetAgentStatus(detectedAgentKind, AgentActivityState.Error);
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] PowerShell profile fallback: {exception}\n");
        }
    }

    private static bool ShouldRecoverStalledProfile(string output, IReadOnlyList<ConsoleDescendantProcess> descendants)
    {
        return string.IsNullOrWhiteSpace(output) && HasKnownStalledPromptHelper(descendants);
    }

    private static bool HasKnownStalledPromptHelper(IReadOnlyList<ConsoleDescendantProcess> descendants)
        => descendants.Any(value => IsKnownPromptHelper(value.Name));

    private static bool IsKnownPromptHelper(string name)
        => name.Equals("oh-my-posh", StringComparison.OrdinalIgnoreCase) || name.Equals("starship", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellCommand(string commandLine)
    {
        var command = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var executable = command.StartsWith('"')
            ? command[1..].Split('"', 2)[0]
            : command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
    }

    public string GetRawOutputForTest()
    {
        try { return GetCapturedRawOutput(); } catch { return string.Empty; }
    }

    internal bool RecoveryOutputIsBoundedForTest
    {
        get { lock (agentOutputSync) return recoveryOutput.Length <= RecoveryOutputTrimThreshold; }
    }
    internal bool DependencyOutputLoggingDisabledForTest => Terminal.ConPTYTerm?.ConsoleOutputLog is null;
    internal bool LastRecoverySnapshotReadUsedDispatcherForTest => lastRecoverySnapshotReadUsedDispatcher;

    internal static bool BoundedRecoveryOutputWorksForTest()
    {
        var buffer = new StringBuilder();
        AppendBoundedRecoveryOutput(buffer, new string('a', RecoveryOutputTrimThreshold));
        const string tail = "RECOVERY_BUFFER_TAIL";
        AppendBoundedRecoveryOutput(buffer, new string('b', 1024) + tail);
        return buffer.Length <= MaximumRecoveryOutputCharacters && buffer.ToString().EndsWith(tail, StringComparison.Ordinal);
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
            var now = DateTime.UtcNow;
            terminalActivity.RecordInput(now);
            if (detectedAgentKind == AgentKind.Codex && input.IndexOfAny(['\r', '\n', '\u001b']) >= 0)
                codexOutputActivity.UserResponded(now);
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
        currentAppearance = appearance;
        // Font properties only take effect when the theme is (re)applied; the
        // Theme setter pushes everything to the native control immediately.
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        var terminalFontSize = EffectiveTerminalFontSize(appearance);
        Terminal.FontSizeWhenSettingTheme = terminalFontSize;
        Terminal.Theme = appearance.Theme;
        remoteFontFace = appearance.FontFace;
        Volatile.Write(ref remoteFontSize, terminalFontSize);
        CommandInput.ApplyComposerFontSize(Profile.CommandFontSize ?? 11);
        configuredCursorStyleCode = CursorStyleCode(appearance.Theme.CursorStyle);
        AttachTerminalOutputFilter();
    }

    private int EffectiveTerminalFontSize(TerminalAppearance appearance)
        => Math.Clamp(Profile.TerminalFontSize ?? appearance.FontSize, MinimumTerminalFontSize, MaximumTerminalFontSize);

    private void TerminalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            AdjustTerminalFontSize(e.Delta);
            e.Handled = true;
            return;
        }
        if (TryScrollTerminalViewport(e.Delta)) e.Handled = true;
    }

    private void CommandInputPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        AdjustComposerFontSize(e.Delta);
        e.Handled = true;
    }

    private void AdjustComposerFontSize(int wheelDelta)
    {
        var current = Profile.CommandFontSize ?? (int)Math.Round(CommandInput.FontSize);
        var next = Math.Clamp(current + Math.Sign(wheelDelta), MinimumComposerFontSize, MaximumComposerFontSize);
        if (next != current)
        {
            Profile.CommandFontSize = next;
            CommandInput.ApplyComposerFontSize(next);
            commandStateChanged();
        }
    }

    private void AdjustTerminalFontSize(int wheelDelta)
    {
        var current = EffectiveTerminalFontSize(currentAppearance);
        var next = Math.Clamp(current + Math.Sign(wheelDelta), MinimumTerminalFontSize, MaximumTerminalFontSize);
        if (next == current) return;
        Profile.TerminalFontSize = next;
        Terminal.FontSizeWhenSettingTheme = next;
        Terminal.Theme = currentAppearance.Theme;
        Volatile.Write(ref remoteFontSize, next);
        commandStateChanged();
        ScheduleRemoteDimensionRefresh();
    }

    public void ApplyProfile(SessionProfile profile)
    {
        Profile = profile;
        startupRecovery = null;
        codexOutputActivity.Reset();
        Profile.SetTmuxTerminal(false);
        TitleText.Text = profile.Name;
        ApplyAccent();
        Terminal.StartupCommandLine = BuildCommandLine(profile, null);
    }

    public void RefreshProfileDisplay(SessionProfile profile)
    {
        Profile = profile;
        TitleText.Text = profile.Name;
        ApplyAccent();
    }

    public bool IsNativeScrollbarThemed()
    {
        return TerminalViewportScrollbar.Visibility == Visibility.Visible
            && TerminalViewportScrollbar.Orientation == Orientation.Vertical
            && TerminalViewportScrollbar.Style == TryFindResource("ThemedScrollBar") as Style
            && ReferenceEquals(VisualTreeHelper.GetParent(TerminalViewportScrollbar), TerminalScrollbarHost)
            && TerminalViewportScrollbar.IsHitTestVisible && TerminalScrollbarGutter.ActualWidth >= 10
            && nativeScrollbar is { IsHitTestVisible: false, ActualWidth: 0 }
            && !ReferenceEquals(VisualTreeHelper.GetParent(nativeScrollbar), TerminalScrollbarHost);
    }

    public bool NativeScrollbarInteractiveForTest => TerminalViewportScrollbar is { IsHitTestVisible: true, IsVisible: true }
        && TerminalViewportScrollbar.ActualWidth >= 10 && TerminalViewportScrollbar.ActualHeight > 0
        && (!TerminalViewportScrollbar.IsEnabled
            || TerminalViewportScrollbar.InputHitTest(new Point(TerminalViewportScrollbar.ActualWidth / 2, TerminalViewportScrollbar.ActualHeight / 2)) is not null);
    public bool TerminalScrollbarBridgeStableForTest => terminalScrollbarBridgeAttached && terminalContainer is not null
        && nativeScrollContainer is not null && nativeTerminalScrolledHandler is not null
        && NativeTerminalUserScrollMethod is not null && NativeTerminalWheelMethod is not null
        && TerminalViewportScrollbar.Orientation == Orientation.Vertical
        && TerminalViewportScrollbar.ActualWidth <= TerminalScrollbarGutter.ActualWidth
        && nativeScrollbar is { IsHitTestVisible: false, Opacity: 0 }
        && nativeScrollbar.Width == 0 && nativeScrollbar.MaxWidth == 0
        && !ReferenceEquals(VisualTreeHelper.GetParent(nativeScrollbar), TerminalScrollbarHost)
        && RecoveryOutputText.TextWrapping == TextWrapping.Wrap
        && RecoveryOutputText.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled;
    public string TerminalScrollbarBridgeDiagnosticForTest => string.Join(", ", new[]
    {
        $"Attached={terminalScrollbarBridgeAttached}",
        $"NativeCallback={nativeTerminalScrolledHandler is not null}/{nativeTerminalScrollCallbackObserved}",
        $"Container={terminalContainer is not null}",
        $"External={TerminalViewportScrollbar.ActualWidth:F1}x{TerminalViewportScrollbar.ActualHeight:F1}",
        $"Gutter={TerminalScrollbarGutter.ActualWidth:F1}",
        $"InternalWidth={nativeScrollbar?.ActualWidth:F1}/{nativeScrollbar?.Width:F1}/{nativeScrollbar?.MaxWidth:F1}",
        $"InternalHit={nativeScrollbar?.IsHitTestVisible}",
        $"InternalOpacity={nativeScrollbar?.Opacity:F1}",
        $"RecoveryWrap={RecoveryOutputText.TextWrapping}",
        $"RecoveryHorizontal={RecoveryOutputText.HorizontalScrollBarVisibility}"
    });
    public bool TerminalScrollbarHasRangeForTest
    {
        get
        {
            SynchronizeTerminalViewportScrollbar();
            return TerminalViewportScrollbar.Maximum > 0 && TerminalViewportScrollbar.IsEnabled;
        }
    }
    public bool ExerciseTerminalScrollbarForTest()
    {
        SynchronizeTerminalViewportScrollbar();
        if (nativeScrollbar is null || TerminalViewportScrollbar.Maximum <= 0) return false;
        var target = Math.Max(TerminalViewportScrollbar.Minimum, TerminalViewportScrollbar.Maximum - 1);
        TerminalViewportScrollbar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbPosition, target)
        {
            RoutedEvent = ScrollBar.ScrollEvent,
            Source = TerminalViewportScrollbar
        });
        return Math.Abs(nativeScrollbar.Value - target) < .5;
    }
    public bool TerminalScrollbarRebindsReplacementForTest()
    {
        var original = nativeScrollbar;
        var callbackObserved = nativeTerminalScrollCallbackObserved;
        if (original is null) return false;
        var first = new ScrollBar { Minimum = 0, Maximum = 40, ViewportSize = 10, Value = 4 };
        var second = new ScrollBar { Minimum = 0, Maximum = 80, ViewportSize = 12, Value = 8 };
        try
        {
            nativeTerminalScrollCallbackObserved = false;
            BindNativeScrollbar(first);
            first.Value = 15;
            var firstFollowed = Math.Abs(TerminalViewportScrollbar.Value - 15) < .5;
            BindNativeScrollbar(second);
            var valueAfterReplacement = TerminalViewportScrollbar.Value;
            first.Value = 25;
            var oldDetached = Math.Abs(TerminalViewportScrollbar.Value - valueAfterReplacement) < .5;
            second.Value = 30;
            var replacementFollowed = Math.Abs(TerminalViewportScrollbar.Value - 30) < .5;
            return firstFollowed && oldDetached && replacementFollowed;
        }
        finally
        {
            nativeTerminalScrollCallbackObserved = false;
            BindNativeScrollbar(original);
            SynchronizeTerminalViewportScrollbar();
            nativeTerminalScrollCallbackObserved = callbackObserved;
        }
    }
    public bool RecoveryOverlayVisibleForTest => RecoveryOverlay.Visibility == Visibility.Visible;
    public bool RecoverySurfaceOwnsViewportForTest => RecoveryOverlay.Visibility == Visibility.Visible
        && TerminalSurfaceGrid.Visibility != Visibility.Visible;
    public bool TerminalSurfaceOwnsViewportForTest => RecoveryOverlay.Visibility != Visibility.Visible
        && TerminalSurfaceGrid.Visibility == Visibility.Visible;

    public bool FocusCommandInputForTest() => CommandInput.Focus();
    public bool CommandBarExpandedForTest => Profile.CommandBarExpanded && CommandBarContainer.Visibility == Visibility.Visible;
    public int QueuedCommandCountForTest => Profile.PendingCommands.Count;
    public string QueueCountTextForTest => QueueCountText.Text;
    public string CommandInputTextForTest => CommandInput.Text;
    public int CommandInputCaretIndexForTest => CommandInput.CaretIndex;
    public static bool WorkingDirectoryMarkerParsingWorksForTest()
    {
        var windows = WorkingDirectoryOscRegex.Match("before\u001b]9;9;\"D:\\Dev\\illest.lol\"\aafter");
        var ssh = WorkingDirectoryOscRegex.Match("\u001b]9;9;/home/ubuntu/illest.bot\u001b\\");
        var tmux = WorkingDirectoryOscRegex.Match("\u001bPtmux;\u001b\u001b]9;9;\"/home/ubuntu/tmux-project\"\a\u001b\\");
        return windows.Success && NormalizeWorkingDirectoryMarker(windows.Groups["path"].Value) == @"D:\Dev\illest.lol"
            && ssh.Success && NormalizeWorkingDirectoryMarker(ssh.Groups["path"].Value) == "/home/ubuntu/illest.bot"
            && tmux.Success && NormalizeWorkingDirectoryMarker(tmux.Groups["path"].Value) == "/home/ubuntu/tmux-project";
    }
    public void SetWorkingDirectoryForTest(string path, bool isSsh) => ApplyWorkingDirectory(path, isSsh);
    public bool CommandInputAutoGrowsForTest => CommandInput.MinLines == 1 && CommandInput.MaxLines == 8
        && CommandInput.VerticalContentAlignment == VerticalAlignment.Top
        && CommandInput.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
        && CommandInput.MaxHeight > CommandInput.MinHeight;
    public double CommandInputHeightForTest => CommandInput.ActualHeight;
    public bool CommandInputRespectsLineCapForTest => CommandInput.ActualHeight <= CommandInput.MaxHeight + .5;
    public bool HandoffButtonReadyForTest => DetachButton.IsEnabled && DetachButton.Content?.ToString() == ">_"
        && DetachButton.ToolTip?.ToString()?.Contains("Windows Terminal", StringComparison.Ordinal) == true;
    public string SendCommandGlyphForTest => RunCommandButton.Content?.ToString() ?? string.Empty;
    public string SendCommandToolTipForTest => RunCommandButton.ToolTip?.ToString() ?? string.Empty;
    public bool RunCommandSettingsMenuReadyForTest()
    {
        var menu = BuildRunCommandSettingsMenu();
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(value => value.Header?.ToString() == "Press Enter automatically");
        return item is { IsCheckable: true } && (item.IsChecked == true) == Profile.PressEnterAfterComposerSend
            && item.ToolTip?.ToString()?.Contains("separate Enter", StringComparison.OrdinalIgnoreCase) == true;
    }
    public void SetPressEnterAfterComposerSendForTest(bool enabled)
    {
        Profile.PressEnterAfterComposerSend = enabled;
        commandStateChanged();
        UpdateSendButtonVisual(sendButtonShowsAll == true, true);
    }
    public int QuickAccessCommandCountForTest => quickAccessProvider().Count(value => value.ShowInQuickAccess && !string.IsNullOrWhiteSpace(value.Command));
    public bool AutomationButtonReadyForTest => AutomationButton.ToolTip is not null && AutomationCountText is not null;
    public int AssignedAutomationCountForTest => Profile.AutomationBindings.Count;
    public bool AddFirstAutomationForTest()
    {
        var automation = automationProvider().FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Command));
        if (automation is null || Profile.AutomationBindings.Any(value => value.AutomationId == automation.Id)) return false;
        Profile.AutomationBindings.Add(new TerminalAutomationBinding { AutomationId = automation.Id });
        UpdateAutomationDisplay();
        commandStateChanged();
        return AutomationCountBadge.Visibility == Visibility.Visible;
    }
    public bool ConfigureFirstAutomationForTest(bool enabled, bool autoInsert)
    {
        var binding = Profile.AutomationBindings.FirstOrDefault();
        if (binding is null) return false;
        binding.Enabled = enabled;
        binding.AutoInsertAtEnd = autoInsert;
        commandStateChanged();
        return true;
    }
    public string AppendAutomationTextForTest(string command) => AppendEnabledAutomationText(command);
    public static bool AutomationClearLineInputWorksForTest()
    {
        var automation = new AutomationRule { Command = "Write-Output ready", ClearLine = true };
        return BuildAutomationTerminalInput(automation) == "\u0001\u000bWrite-Output ready";
    }
    public bool AutomationMenuContractForTest()
    {
        ShowAutomationMenu();
        var assigned = AutomationButton.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(value => value.Items.Count > 0 && value.Header?.ToString() != "Add automation");
        var headers = assigned?.Items.OfType<MenuItem>().Select(value => value.Header?.ToString()).ToHashSet(StringComparer.Ordinal) ?? [];
        var add = AutomationButton.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(value => value.Header?.ToString() == "Add automation");
        add?.ApplyTemplate();
        var submenuCanOpen = add?.Items.Count > 0 && add.Template?.FindName("PART_Popup", add) is Popup;
        if (AutomationButton.ContextMenu is { } menu) menu.IsOpen = false;
        return headers.Contains("Run now") && headers.Contains("Enabled") && headers.Contains("Auto insert at end of input")
            && headers.Contains("Edit automation") && headers.Contains("Remove from terminal") && submenuCanOpen;
    }
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
    public void SetCommandInputForTest(string value)
    {
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        CommandInput.Text = value;
        FlushComposerState();
    }
    public void SetCommandInputDeferredForTest(string value)
    {
        queueSelectionIndex = null;
        queueNavigationDraft = string.Empty;
        CommandInput.Text = value;
    }
    public void SetCommandInputCaretForTest(int value) => CommandInput.CaretIndex = value;
    public Task<bool> HandleCommandInputKeyForTestAsync(Key key, ModifierKeys modifiers) => HandleCommandInputKeyAsync(key, modifiers);
    public int ComposerStateFlushCountForTest => composerStateFlushCount;
    public bool ComposerStateTimerEnabledForTest => composerStateTimer.IsEnabled;
    public void FlushComposerStateForTest() => FlushComposerState();
    public (TimeSpan Elapsed, int ExtractionsDuringTyping, bool CanonicalTextMatches) SimulateFastComposerTypingForTest(int characterCount)
        => CommandInput.SimulateFastTypingForTest(characterCount);
    public int CommandHistoryCountForTest => Profile.CommandHistory.Count;
    public int CommandHistoryVisibleItemCountForTest => CommandHistoryList.Items.Count;
    public bool CommandHistoryButtonIsFramelessForTest => CommandHistoryButton.Background == Brushes.Transparent && CommandHistoryButton.BorderThickness == new Thickness(0);
    public bool CommandHistoryPanelVisibleForTest => CommandHistoryPanel.Visibility == Visibility.Visible;
    public void AddCommandHistoryForTest(string command) => RecordCommandHistory(command);
    public void SetCommandHistoryForTest(IEnumerable<string> commands)
    {
        Profile.CommandHistory = commands.Where(value => !string.IsNullOrWhiteSpace(value)).TakeLast(MaximumCommandHistory).ToList();
        Profile.CommandHistoryTimestampsUtc = Enumerable.Range(0, Profile.CommandHistory.Count)
            .Select(index => DateTime.UtcNow.AddSeconds(-(Profile.CommandHistory.Count - index) * 30))
            .ToList();
        RefreshCommandHistoryList();
    }
    public void ShowCommandHistoryForTest() => SetCommandHistoryVisible(true);
    public void HideCommandHistoryForTest() => SetCommandHistoryVisible(false);
    public bool ClearCommandHistoryForTest(bool confirmed)
    {
        if (!confirmed || Profile.CommandHistory.Count == 0) return false;
        ClearCommandHistory();
        return Profile.CommandHistory.Count == 0 && Profile.CommandHistoryTimestampsUtc.Count == 0;
    }
    public bool ClearCommandHistoryButtonReadyForTest => ClearCommandHistoryButton.Content?.ToString() == "Clear history"
        && ClearCommandHistoryButton.Foreground is SolidColorBrush && ClearCommandHistoryButton.ToolTip is not null;
    public void RestoreLatestCommandHistoryForTest()
    {
        if (Profile.CommandHistory.Count > 0) RestoreCommandHistory(Profile.CommandHistory[^1]);
    }
    public void RestoreCommandHistoryForTest(string command) => RestoreCommandHistory(command);
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
    public bool RendererFilterPreservesVtStreamForTest()
    {
        const string source = "prefix\u001b[4:4mdotted\u001b[24m\u001b[>0;10;1csuffix\u001b[3 q";
        var expected = source.Replace("\u001b[3 q", $"\u001b[{configuredCursorStyleCode} q", StringComparison.Ordinal);
        var actual = ForceCursorStyleForTest(source);
        const string splitSource = "\u001b[4:4mdotted\u001b[24m";
        var splitActual = ForceCursorStyleForTest("\u001b[4:")
            + ForceCursorStyleForTest("4mdotted\u001b[2")
            + ForceCursorStyleForTest("4m");
        return actual.Length == source.Length
            && actual == expected
            && splitActual == splitSource
            && actual.Contains("\u001b[4:4m", StringComparison.Ordinal)
            && actual.Contains("\u001b[24m", StringComparison.Ordinal)
            && actual.Contains("\u001b[>0;10;1c", StringComparison.Ordinal);
    }
    public bool RemoteOutputRelayPreservesVtStreamForTest()
    {
        const string source = "\u001b[>0;10;1c";
        string? relayed = null;
        void Handler(TerminalPane _, string data) => relayed = data;
        RawOutputReceived += Handler;
        try { CaptureTerminalOutput(this, new TerminalOutputEventArgs(source)); }
        finally { RawOutputReceived -= Handler; }
        return relayed == source;
    }
    public bool PasteTextForTest(string text)
    {
        var terminal = Terminal.ConPTYTerm;
        if (terminal is null) return false;
        terminalActivity.RecordInput(DateTime.UtcNow);
        terminal.WriteToTerm(FormatClipboardText(text));
        return true;
    }
    public void SubmitTerminalInputForTest()
    {
        terminalActivity.RecordInput(DateTime.UtcNow);
        Terminal.ConPTYTerm?.WriteToTerm("\r");
    }
    public async Task<bool> QueueWithCtrlEnterForTestAsync(string command)
    {
        var before = Profile.PendingCommands.Count;
        SetCommandInputForTest(command);
        var handled = await HandleCommandInputKeyAsync(Key.Enter, ModifierKeys.Control);
        return handled && Profile.PendingCommands.Count == before + 1 && string.IsNullOrEmpty(CommandInput.Text);
    }
    public void RefreshCommandRoutingAppearance() => UpdateSendButtonVisual(IsSendToAllActive(Keyboard.Modifiers), true);

    private void ConfigureNativeScrollbar()
    {
        AttachNativeTerminalScrollBridge();
        var scrollbar = FindVisualChild<ScrollBar>(Terminal.Terminal);
        if (scrollbar is null)
        {
            BindNativeScrollbar(null);
            return;
        }
        BindNativeScrollbar(scrollbar);
    }

    private void BindNativeScrollbar(ScrollBar? scrollbar)
    {
        if (!ReferenceEquals(nativeScrollbar, scrollbar))
        {
            DetachTerminalScrollbarBridge();
            nativeScrollbar = scrollbar;
            terminalScrollbarState = null;
            terminalScrollbarRangeObserved = false;
        }
        if (scrollbar is null) return;
        // TerminalControl reads the original scrollbar's ActualWidth during every
        // native resize. Keep that control in its own visual tree and use it only
        // as the dependency's private state holder. Moving it corrupts the HWND's
        // render/input geometry and can paint its thumb over terminal content.
        scrollbar.Visibility = Visibility.Visible;
        scrollbar.Width = 0;
        scrollbar.MinWidth = 0;
        scrollbar.MaxWidth = 0;
        scrollbar.Margin = new Thickness(0);
        scrollbar.Opacity = 0;
        scrollbar.IsHitTestVisible = false;
        scrollbar.Focusable = false;
        AttachTerminalScrollbarBridge();
        if (!UsesRemoteTmuxScrollback && !nativeTerminalScrollCallbackObserved)
            UpdateTerminalViewportScrollbar((int)Math.Round(scrollbar.Value), (int)Math.Round(scrollbar.ViewportSize),
                (int)Math.Round(scrollbar.Maximum + scrollbar.ViewportSize));
    }

    private void AttachNativeTerminalScrollBridge()
    {
        var container = FindVisualChild<TerminalContainer>(Terminal.Terminal);
        if (ReferenceEquals(nativeScrollContainer, container) && nativeTerminalScrolledHandler is not null) return;
        DetachNativeTerminalScrollBridge();
        nativeScrollContainer = container;
        if (container is null || NativeTerminalScrolledEvent?.GetAddMethod(true) is not { } addMethod) return;
        try
        {
            EventHandler<(int viewTop, int viewHeight, int bufferSize)> handler = NativeTerminalScrolled;
            addMethod.Invoke(container, [handler]);
            nativeTerminalScrolledHandler = handler;
        }
        catch
        {
            nativeTerminalScrolledHandler = null;
        }
    }

    private void DetachNativeTerminalScrollBridge()
    {
        if (nativeScrollContainer is not null && nativeTerminalScrolledHandler is not null
            && NativeTerminalScrolledEvent?.GetRemoveMethod(true) is { } removeMethod)
        {
            try { removeMethod.Invoke(nativeScrollContainer, [nativeTerminalScrolledHandler]); } catch { }
        }
        nativeTerminalScrolledHandler = null;
        nativeScrollContainer = null;
        nativeTerminalScrollCallbackObserved = false;
    }

    private void NativeTerminalScrolled(object? sender, (int viewTop, int viewHeight, int bufferSize) state)
    {
        if (UsesRemoteTmuxScrollback) return;
        nativeTerminalScrollCallbackObserved = true;
        terminalScrollbarState = (state.viewTop, Math.Max(0, state.bufferSize - state.viewHeight), state.viewHeight);
        UpdateTerminalViewportScrollbar(state.viewTop, state.viewHeight, state.bufferSize);
    }

    private void AttachTerminalScrollbarBridge()
    {
        if (nativeScrollbar is null || terminalScrollbarBridgeAttached) return;
        nativeScrollbar.ValueChanged += NativeScrollbarStateChanged;
        nativeScrollbar.IsEnabledChanged += NativeScrollbarEnabledChanged;
        nativeScrollbar.LayoutUpdated += NativeScrollbarLayoutUpdated;
        terminalScrollbarBridgeAttached = true;
    }

    private void DetachTerminalScrollbarBridge()
    {
        if (nativeScrollbar is not null && terminalScrollbarBridgeAttached)
        {
            nativeScrollbar.ValueChanged -= NativeScrollbarStateChanged;
            nativeScrollbar.IsEnabledChanged -= NativeScrollbarEnabledChanged;
            nativeScrollbar.LayoutUpdated -= NativeScrollbarLayoutUpdated;
        }
        terminalScrollbarBridgeAttached = false;
    }

    private async Task RebindNativeScrollbarAfterRestartAsync()
    {
        // RestartTerm replaces the backend and can reapply the terminal visual
        // tree asynchronously. Re-query the tree instead of retaining the old
        // private ScrollBar instance. The process/output callbacks can become
        // ready a few frames later than the visual tree, so restore both as one
        // post-restart binding operation.
        terminalScrollbarRangeObserved = false;
        nativeTerminalScrollCallbackObserved = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            ConfigureNativeScrollbar();
            AttachTerminalActivationHook();
            AttachTerminalOutputFilter();
            if (nativeScrollbar is not null && Terminal.ConPTYTerm?.TermProcIsStarted == true)
            {
                SynchronizeTerminalViewportScrollbar();
                return;
            }
            await Task.Delay(50);
        }
        ConfigureNativeScrollbar();
        AttachTerminalOutputFilter();
    }

    private void NativeScrollbarStateChanged(object sender, RoutedEventArgs e) => SynchronizeTerminalViewportScrollbar();
    private void NativeScrollbarEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => SynchronizeTerminalViewportScrollbar();
    private void NativeScrollbarLayoutUpdated(object? sender, EventArgs e) => SynchronizeTerminalViewportScrollbar();

    private void SynchronizeTerminalViewportScrollbar()
    {
        if (UsesRemoteTmuxScrollback || nativeScrollbar is null || nativeTerminalScrollCallbackObserved) return;
        var state = (nativeScrollbar.Value, nativeScrollbar.Maximum, nativeScrollbar.ViewportSize);
        if (terminalScrollbarState == state) return;
        terminalScrollbarState = state;
        UpdateTerminalViewportScrollbar((int)Math.Round(state.Value), (int)Math.Round(state.ViewportSize),
            (int)Math.Round(state.Maximum + state.ViewportSize));
    }

    private void UpdateTerminalViewportScrollbar(int viewTop, int viewHeight, int bufferSize)
    {
        void Apply()
        {
            var viewport = Math.Max(1, viewHeight);
            var maximum = Math.Max(0, bufferSize - viewport);
            terminalScrollbarUpdating = true;
            try
            {
                TerminalViewportScrollbar.Minimum = 0;
                TerminalViewportScrollbar.Maximum = maximum;
                TerminalViewportScrollbar.ViewportSize = viewport;
                TerminalViewportScrollbar.SmallChange = 1;
                TerminalViewportScrollbar.LargeChange = Math.Max(1, viewport - 1);
                TerminalViewportScrollbar.Value = Math.Clamp(viewTop, 0, maximum);
                TerminalViewportScrollbar.IsEnabled = maximum > 0;
                TerminalViewportScrollbar.Opacity = maximum > 0 ? 1 : .5;
                if (maximum > 0) terminalScrollbarRangeObserved = true;
            }
            finally { terminalScrollbarUpdating = false; }
        }

        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(Apply, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void TerminalViewportScrollbarScroll(object sender, ScrollEventArgs e)
    {
        if (terminalScrollbarUpdating || nativeScrollbar is null) return;
        ForwardTerminalScroll(e.NewValue, e.ScrollEventType);
        e.Handled = true;
    }

    private void TerminalViewportScrollbarMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryScrollTerminalViewport(e.Delta)) return;
        e.Handled = true;
    }

    private bool TryScrollTerminalViewport(int wheelDelta)
    {
        ConfigureNativeScrollbar();
        if (wheelDelta == 0) return false;
        if (!TerminalViewportScrollbar.IsEnabled || TerminalViewportScrollbar.Maximum <= 0)
            return TryForwardNativeWheelDelta(wheelDelta);
        var configuredLines = SystemParameters.WheelScrollLines;
        var lines = configuredLines > 0 ? configuredLines : Math.Max(1, (int)TerminalViewportScrollbar.ViewportSize - 1);
        var target = Math.Clamp(TerminalViewportScrollbar.Value - Math.Sign(wheelDelta) * lines,
            TerminalViewportScrollbar.Minimum, TerminalViewportScrollbar.Maximum);
        terminalScrollbarUpdating = true;
        try { TerminalViewportScrollbar.Value = target; }
        finally { terminalScrollbarUpdating = false; }
        ForwardTerminalScroll(target, wheelDelta > 0 ? ScrollEventType.SmallDecrement : ScrollEventType.SmallIncrement);
        return true;
    }

    private void ForwardTerminalScroll(double target, ScrollEventType eventType)
    {
        var normalized = Math.Clamp(target, TerminalViewportScrollbar.Minimum, TerminalViewportScrollbar.Maximum);
        if (UsesRemoteTmuxScrollback)
        {
            QueueTmuxScroll(normalized);
            return;
        }
        if (nativeScrollContainer is not null && NativeTerminalUserScrollMethod is not null)
        {
            try
            {
                NativeTerminalUserScrollMethod.Invoke(nativeScrollContainer, [(int)Math.Round(normalized)]);
                if (nativeScrollbar is not null) nativeScrollbar.Value = Math.Clamp(normalized, nativeScrollbar.Minimum, nativeScrollbar.Maximum);
                return;
            }
            catch { }
        }
        if (nativeScrollbar is null) return;
        var nativeValue = Math.Clamp(normalized, nativeScrollbar.Minimum, nativeScrollbar.Maximum);
        nativeScrollbar.Value = nativeValue;
        nativeScrollbar.RaiseEvent(new ScrollEventArgs(eventType, nativeValue)
        {
            RoutedEvent = ScrollBar.ScrollEvent,
            Source = nativeScrollbar
        });
    }

    private bool TryForwardNativeWheelDelta(int wheelDelta)
    {
        if (NativeTerminalWheelMethod is null) return false;
        try
        {
            NativeTerminalWheelMethod.Invoke(Terminal.Terminal, [Terminal.Terminal, wheelDelta]);
            return true;
        }
        catch { return false; }
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
        if (terminalContainer is null)
        {
            terminalContainer = FindVisualChild<TerminalContainer>(Terminal.Terminal);
        }
        if (!terminalMessageRouterInstalled && terminalContainer is not null) InstallTerminalMessageRouter(terminalContainer);
        if (!terminalWindowSubclassInstalled && terminalContainer?.Handle is { } handle && handle != IntPtr.Zero)
            terminalWindowSubclassInstalled = SetWindowSubclass(handle, terminalWindowSubclassProc, UIntPtr.Zero, UIntPtr.Zero);
    }

    private void RegisterTerminalThreadMessageHook()
    {
        if (terminalThreadMessageHookInstalled) return;
        ComponentDispatcher.ThreadPreprocessMessage += TerminalThreadPreprocessMessage;
        terminalThreadMessageHookInstalled = true;
    }

    private void UnregisterTerminalThreadMessageHook()
    {
        if (!terminalThreadMessageHookInstalled) return;
        ComponentDispatcher.ThreadPreprocessMessage -= TerminalThreadPreprocessMessage;
        terminalThreadMessageHookInstalled = false;
    }

    private void TerminalThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        if (handled || terminalContainer?.Handle is not { } terminalHandle || terminalHandle == IntPtr.Zero
            || message.hwnd != terminalHandle && !IsChild(terminalHandle, message.hwnd)) return;
        var nativeMessage = unchecked((uint)message.message);
        var virtualKey = unchecked((int)message.wParam.ToInt64());
        var keyboardMessage = nativeMessage == WmKeyDown || nativeMessage == WmSysKeyDown;
        var modifiers = terminalShortcutTestModifiers;
        var controlDown = keyboardMessage && (modifiers?.Control ?? IsKeyDown(VkControl));
        var altDown = keyboardMessage && (modifiers?.Alt ?? IsKeyDown(VkMenu));
        var shiftDown = keyboardMessage && (modifiers?.Shift ?? IsKeyDown(VkShift));
        if (IsPlainTerminalTabMessage(nativeMessage, virtualKey, controlDown, altDown, shiftDown))
        {
            if (TryForwardTerminalTab())
            {
                terminalActivity.RecordInput(DateTime.UtcNow);
                Interlocked.Increment(ref terminalTabInterceptCount);
                handled = true;
            }
            return;
        }
        if (keyboardMessage && IsRemoteImageShortcutMessage(nativeMessage, virtualKey, controlDown, altDown))
        {
            var mode = altDown ? RemoteImagePasteMode.FilePath : RemoteImagePasteMode.Attachment;
            var consumed = remoteClipboardPasteTestOverride?.Invoke(mode) ?? TryHandleRemoteClipboardPaste(mode);
            if (consumed)
            {
                suppressRemoteImagePasteVSequence = true;
                terminalActivity.RecordInput(DateTime.UtcNow);
                Interlocked.Increment(ref terminalThreadMessageInterceptCount);
                handled = true;
            }
            return;
        }
        if (suppressRemoteImagePasteVSequence && IsRemoteImagePasteCharacter(nativeMessage, virtualKey))
        {
            handled = true;
            return;
        }
        if (suppressRemoteImagePasteVSequence && IsRemoteImagePasteKeyUp(nativeMessage, virtualKey))
        {
            suppressRemoteImagePasteVSequence = false;
            handled = true;
        }
    }

    private void InstallTerminalMessageRouter(TerminalContainer container)
    {
        var method = typeof(TerminalContainer).GetMethod("TerminalContainer_MessageHook", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) return;
        try
        {
            terminalInternalMessageHook = (HwndSourceHook)method.CreateDelegate(typeof(HwndSourceHook), container);
            // The terminal package registers this private hook in its constructor. Replace it
            // with our router so image shortcuts can be consumed before it writes key events
            // into ConPTY; all other messages are forwarded to the original handler.
            container.MessageHook -= terminalInternalMessageHook;
            container.MessageHook += TerminalMessageHook;
            terminalMessageRouterInstalled = true;
        }
        catch (Exception exception)
        {
            terminalInternalMessageHook = null;
            terminalMessageRouterInstalled = false;
            try { ShowRemoteImageStatus("Image paste unavailable", exception.Message, false, true); } catch { }
        }
    }

    private IntPtr TerminalWindowSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        try
        {
            if (message == WmMouseWheel && IsKeyDown(VkControl))
            {
                var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
                Dispatcher.BeginInvoke(() => AdjustTerminalFontSize(delta), System.Windows.Threading.DispatcherPriority.Input);
                return IntPtr.Zero;
            }
            if (message == WmMouseWheel)
            {
                var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
                if (TryScrollTerminalViewport(delta)) return IntPtr.Zero;
            }
            var keyboardMessage = message == WmKeyDown || message == WmSysKeyDown;
            var virtualKey = unchecked((int)wParam.ToInt64());
            var controlDown = keyboardMessage && IsKeyDown(VkControl);
            var altDown = keyboardMessage && IsKeyDown(VkMenu);
            if (keyboardMessage && IsRemoteImageShortcutMessage(message, virtualKey, controlDown, altDown)
                && TryHandleRemoteClipboardPaste(altDown ? RemoteImagePasteMode.FilePath : RemoteImagePasteMode.Attachment))
            {
                suppressRemoteImagePasteVSequence = true;
                terminalActivity.RecordInput(DateTime.UtcNow);
                return IntPtr.Zero;
            }
            if (suppressRemoteImagePasteVSequence && IsRemoteImagePasteCharacter(message, virtualKey)) return IntPtr.Zero;
            if (suppressRemoteImagePasteVSequence && IsRemoteImagePasteKeyUp(message, virtualKey))
            {
                suppressRemoteImagePasteVSequence = false;
                return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            // No managed exception may cross a native window-procedure boundary.
            try { ShowRemoteImageStatus("Image paste failed", exception.Message, false, true); } catch { }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static bool IsRemoteImageShortcutMessage(uint message, int virtualKey, bool controlDown, bool altDown)
        => (message == WmKeyDown || message == WmSysKeyDown) && virtualKey == VkV && (controlDown || altDown);
    private static bool IsPlainTerminalTabMessage(uint message, int virtualKey, bool controlDown, bool altDown, bool shiftDown)
        => message == WmKeyDown && virtualKey == VkTab && !controlDown && !altDown && !shiftDown;
    private bool TryForwardTerminalTab()
    {
        if (terminalTabForwardTestOverride is not null) return terminalTabForwardTestOverride();
        if (Terminal.ConPTYTerm?.TermProcIsStarted != true) return false;
        Terminal.ConPTYTerm.WriteToTerm("\t");
        return true;
    }
    private static bool IsRemoteImagePasteCharacter(uint message, int value)
        => (message == WmChar || message == WmSysChar) && char.ToUpperInvariant(unchecked((char)value)) == 'V';
    private static bool IsRemoteImagePasteKeyUp(uint message, int virtualKey)
        => (message == WmKeyUp || message == WmSysKeyUp) && virtualKey == VkV;

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

    private void QueueScrollbarRefreshFromOutput()
    {
        if (UsesRemoteTmuxScrollback)
        {
            QueueTmuxScrollbarRefresh();
            return;
        }
        if (terminalScrollbarRangeObserved || Dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref terminalScrollbarRefreshPending, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ConfigureNativeScrollbar();
                SynchronizeTerminalViewportScrollbar();
            }
            finally { Interlocked.Exchange(ref terminalScrollbarRefreshPending, 0); }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool UsesRemoteTmuxScrollback => startupRecovery is
    {
        SshWasActive: true,
        RemoteTmuxManaged: true
    };

    private void QueueTmuxScrollbarRefresh(bool immediate = false)
    {
        if (!UsesRemoteTmuxScrollback || Dispatcher.HasShutdownStarted) return;
        void Schedule()
        {
            if (!IsLoaded) return;
            tmuxScrollbarProbeRequested = true;
            if (tmuxScrollbarProbeInFlight || tmuxScrollbarRefreshTimer.IsEnabled) return;
            tmuxScrollbarRefreshTimer.Interval = immediate
                ? TimeSpan.FromMilliseconds(40)
                : TimeSpan.FromMilliseconds(900);
            tmuxScrollbarRefreshTimer.Start();
        }
        if (Dispatcher.CheckAccess()) Schedule();
        else Dispatcher.BeginInvoke(Schedule, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task RefreshTmuxScrollbarAsync()
    {
        if (!UsesRemoteTmuxScrollback || !IsLoaded || startupRecovery is null) return;
        if (tmuxScrollbarProbeInFlight)
        {
            tmuxScrollbarProbeRequested = true;
            return;
        }
        tmuxScrollbarProbeInFlight = true;
        tmuxScrollbarProbeRequested = false;
        var generation = Volatile.Read(ref tmuxScrollbarGeneration);
        try
        {
            var client = GetOrCreateTmuxScrollbackClient();
            var state = client is null
                ? await RemoteTmuxScrollback.ProbeAsync(startupRecovery)
                : await client.ProbeAsync();
            if (generation != Volatile.Read(ref tmuxScrollbarGeneration) || !IsLoaded || !state.Succeeded) return;
            ApplyTmuxScrollbarState(state);
        }
        finally
        {
            tmuxScrollbarProbeInFlight = false;
            if (tmuxScrollbarProbeRequested) QueueTmuxScrollbarRefresh();
        }
    }

    private void QueueTmuxScroll(double target)
    {
        if (!UsesRemoteTmuxScrollback || !tmuxScrollbackState.Succeeded)
        {
            QueueTmuxScrollbarRefresh(true);
            return;
        }
        var viewTop = Math.Clamp((int)Math.Round(target), 0, tmuxScrollbackState.HistorySize);
        pendingTmuxScrollPosition = tmuxScrollbackState.HistorySize - viewTop;
        tmuxScrollbarApplyTimer.Stop();
        tmuxScrollbarApplyTimer.Start();
    }

    private async Task ApplyPendingTmuxScrollAsync()
    {
        if (!UsesRemoteTmuxScrollback || startupRecovery is null || pendingTmuxScrollPosition is null) return;
        if (tmuxScrollbarApplyInFlight)
        {
            return;
        }
        var scrollPosition = pendingTmuxScrollPosition.Value;
        pendingTmuxScrollPosition = null;
        tmuxScrollbarApplyInFlight = true;
        var generation = Volatile.Read(ref tmuxScrollbarGeneration);
        try
        {
            var client = GetOrCreateTmuxScrollbackClient();
            var state = client is null
                ? await ScrollAndProbeTmuxFallbackAsync(startupRecovery, scrollPosition)
                : await client.ScrollAndProbeAsync(scrollPosition);
            if (state.Succeeded && generation == Volatile.Read(ref tmuxScrollbarGeneration) && IsLoaded
                && pendingTmuxScrollPosition is null)
                ApplyTmuxScrollbarState(state);
        }
        finally
        {
            tmuxScrollbarApplyInFlight = false;
            if (pendingTmuxScrollPosition is not null) tmuxScrollbarApplyTimer.Start();
        }
    }

    private RemoteTmuxScrollbackClient? GetOrCreateTmuxScrollbackClient()
    {
        if (!UsesRemoteTmuxScrollback || startupRecovery is null) return null;
        if (tmuxScrollbackClient is not null) return tmuxScrollbackClient;
        if (RemoteTmuxScrollbackClient.TryCreate(startupRecovery, out var created)) tmuxScrollbackClient = created;
        return tmuxScrollbackClient;
    }

    private void DisposeTmuxScrollbackClient()
    {
        var previous = tmuxScrollbackClient;
        tmuxScrollbackClient = null;
        previous?.Dispose();
    }

    private void ApplyTmuxScrollbarState(RemoteTmuxScrollbackState state)
    {
        tmuxScrollbackState = state;
        var viewport = RemoteTmuxScrollback.ToViewport(state);
        terminalScrollbarState = (viewport.ViewTop, state.HistorySize, viewport.ViewHeight);
        UpdateTerminalViewportScrollbar(viewport.ViewTop, viewport.ViewHeight, viewport.BufferSize);
    }

    private static async Task<RemoteTmuxScrollbackState> ScrollAndProbeTmuxFallbackAsync(
        SessionRecoveryEntry recovery, int scrollPosition)
    {
        var sessionName = RemoteTmuxSession.IsSafeSessionName(recovery.RemoteTmuxSessionName)
            ? recovery.RemoteTmuxSessionName!
            : RemoteTmuxSession.GetSessionName(recovery.SessionId);
        var command = RemoteTmuxScrollback.BuildScrollAndProbeCommand(sessionName, scrollPosition);
        var result = await RemoteTmuxSession.RunSshCommandAsync(recovery, command, CancellationToken.None);
        return result.Started && result.ExitCode == 0 && RemoteTmuxScrollback.TryParse(result.Output, out var state)
            ? state : default;
    }

    private void CaptureTerminalOutput(object? sender, TerminalOutputEventArgs args)
    {
        Interlocked.Increment(ref remoteOutputEventCount);
        bracketedPasteMode.RecordOutput(args.Data);
        QueueScrollbarRefreshFromOutput();
        CaptureWorkingDirectory(args.Data);
        var rawOutput = args.Data;
        // Browser xterm clients must receive the same complete VT stream as the
        // native renderer. Removing variable-length fragments would desynchronize
        // cursor positioning and persistent SGR attributes in full-screen TUIs.
        try { RawOutputReceived?.Invoke(this, rawOutput); }
        catch { /* A remote viewer must never interrupt the ConPTY read loop. */ }
        var output = TerminalTextSanitizer.ForLiveOutput(rawOutput);
        if (output.Length == 0) return;
        var now = DateTime.UtcNow;
        var meaningful = terminalActivity.RecordOutput(output, now);
        codexOutputActivity.RecordOutput(output, now);
        hermesActivity.RecordOutput(output, meaningful);
        lock (agentOutputSync)
        {
            recentAgentOutput.Append(output);
            if (recentAgentOutput.Length > 8192) recentAgentOutput.Remove(0, recentAgentOutput.Length - 8192);
            AppendBoundedRecoveryOutput(recoveryOutput, output);
        }
    }

    private void CaptureWorkingDirectory(string data)
    {
        string? path = null;
        lock (agentOutputSync)
        {
            workingDirectoryOscBuffer.Append(data);
            var matches = WorkingDirectoryOscRegex.Matches(workingDirectoryOscBuffer.ToString());
            if (matches.Count > 0)
            {
                var match = matches[matches.Count - 1];
                path = NormalizeWorkingDirectoryMarker(match.Groups["path"].Value);
                workingDirectoryOscBuffer.Remove(0, match.Index + match.Length);
            }
            else if (workingDirectoryOscBuffer.Length > 4096)
            {
                workingDirectoryOscBuffer.Remove(0, workingDirectoryOscBuffer.Length - 4096);
            }
        }
        if (path is null) return;
        var isSsh = path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => ApplyWorkingDirectory(path, isSsh)));
    }

    private static string? NormalizeWorkingDirectoryMarker(string value)
    {
        var path = value.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        if (path.Length is 0 or > 2048 || path.Any(char.IsControl)) return null;
        return path;
    }

    private void ApplyWorkingDirectory(string path, bool isSsh)
    {
        if (string.Equals(Profile.LiveWorkingDirectory, path, StringComparison.Ordinal)
            && Profile.LiveWorkingDirectoryIsSsh == isSsh) return;
        Profile.LiveWorkingDirectory = path;
        Profile.LiveWorkingDirectoryIsSsh = isSsh;
        Profile.NotifyDirectoryChanged();
        commandStateChanged();
    }

    private static void AppendBoundedRecoveryOutput(StringBuilder buffer, string output)
    {
        if (string.IsNullOrEmpty(output)) return;
        buffer.Append(output);
        if (buffer.Length > RecoveryOutputTrimThreshold)
            buffer.Remove(0, buffer.Length - MaximumRecoveryOutputCharacters);
    }

    private async void RefreshAgentStatus(bool force = false)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (agentStatusRefreshPending)
        {
            forceAgentStatusRefreshQueued |= force;
            return;
        }

        agentStatusRefreshPending = true;
        try
        {
            var now = DateTime.UtcNow;
            if (force || now - lastAgentProbeUtc >= TimeSpan.FromSeconds(4))
            {
                lastAgentProbeUtc = now;
                var output = string.Empty;
                lock (agentOutputSync) output = recentAgentOutput.ToString();
                var profileId = Profile.Id;
                var codexLaunch = await Task.Run(() => CodexLaunchStore.Load(profileId));
                if (Dispatcher.HasShutdownStarted) return;
                var remoteCodex = startupRecovery?.RemoteCodexWasActive == true
                    && CodexSessionLocator.IsSafeCodexId(startupRecovery.RemoteCodexSessionId);
                if (remoteCodex || codexLaunch?.IsActive == true || output.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase))
                {
                    detectedAgentKind = AgentKind.Codex;
                    activeCodexSessionId = remoteCodex
                        ? startupRecovery!.RemoteCodexSessionId
                        : codexLaunch?.SessionId ?? startupRecovery?.CodexSessionId;
                    if (remoteCodex) QueueRemoteCodexActivityProbe(now);
                }
                else if (output.Contains("Resume this session with:", StringComparison.OrdinalIgnoreCase))
                {
                    hermesExitObserved = true;
                    detectedAgentKind = AgentKind.Terminal;
                }
                else if (!hermesExitObserved && (startupRecovery?.HermesWasActive == true || output.Contains("Hermes Agent", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("$ Hermes", StringComparison.OrdinalIgnoreCase))
                        )
                    detectedAgentKind = AgentKind.Hermes;
                else if (codexLaunch?.EndedUtc is not null)
                {
                    detectedAgentKind = AgentKind.Terminal;
                    activeCodexSessionId = null;
                }
            }

            bool terminalRunning;
            try { terminalRunning = Terminal.ConPTYTerm?.TermProcIsStarted == true; }
            catch { terminalRunning = false; }
            var remoteCodexActive = startupRecovery?.RemoteCodexWasActive == true
                && string.Equals(activeCodexSessionId, startupRecovery.RemoteCodexSessionId, StringComparison.OrdinalIgnoreCase);
            var codexActivity = default(CodexTurnActivity);
            if (detectedAgentKind == AgentKind.Codex)
            {
                if (remoteCodexActive) codexActivity = remoteCodexActivity;
                else
                {
                    var sessionId = activeCodexSessionId;
                    codexActivity = await Task.Run(() => CodexSessionLocator.FindActivity(sessionId));
                    if (Dispatcher.HasShutdownStarted) return;
                }
            }
            var next = ClassifyAgentActivity(detectedAgentKind, terminalRunning, codexActivity.State,
                codexOutputActivity.State, hermesActivity.State);
            SetAgentStatus(detectedAgentKind, next);
        }
        catch { }
        finally
        {
            agentStatusRefreshPending = false;
            if (forceAgentStatusRefreshQueued && !Dispatcher.HasShutdownStarted)
            {
                forceAgentStatusRefreshQueued = false;
                _ = Dispatcher.BeginInvoke(() => RefreshAgentStatus(true), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private async void QueueRemoteCodexActivityProbe(DateTime now)
    {
        if (remoteCodexActivityProbePending || now < nextRemoteCodexActivityProbeUtc || startupRecovery is not { RemoteCodexWasActive: true } recovery) return;
        remoteCodexActivityProbePending = true;
        nextRemoteCodexActivityProbeUtc = now.AddSeconds(4);
        try
        {
            remoteCodexActivity = await RemoteCodexActivityProbe.ProbeAsync(recovery);
            if (!Dispatcher.HasShutdownStarted) RefreshAgentStatus();
        }
        catch { remoteCodexActivity = default; }
        finally { remoteCodexActivityProbePending = false; }
    }

    private static AgentActivityState ClassifyAgentActivity(AgentKind kind, bool terminalRunning, CodexTurnActivityState codexState,
        AgentActivityState codexOutputState, AgentActivityState hermesState)
    {
        if (!terminalRunning) return AgentActivityState.Stopped;
        if (kind == AgentKind.Codex)
        {
            if (codexOutputState == AgentActivityState.Waiting) return AgentActivityState.Waiting;
            if (codexOutputState == AgentActivityState.Working) return AgentActivityState.Working;
            return codexState switch
            {
                CodexTurnActivityState.Working => AgentActivityState.Working,
                CodexTurnActivityState.Waiting => AgentActivityState.Waiting,
                _ => AgentActivityState.Idle
            };
        }
        // Hermes currently has no supported structured status API. Its state is
        // latched from a submitted turn until the TUI renders either its ready
        // prompt or a blocking approval/clarification overlay. Ordinary shells
        // never animate merely because they printed output.
        return kind == AgentKind.Hermes ? hermesState : AgentActivityState.Idle;
    }

    private void SetAgentStatus(AgentKind kind, AgentActivityState state)
    {
        if (agentActivityState == state && displayedAgentKind == kind) return;
        detectedAgentKind = kind;
        displayedAgentKind = kind;
        agentActivityState = state;
        var color = state switch
        {
            AgentActivityState.Working => Color.FromRgb(137, 180, 250),
            AgentActivityState.Waiting => Color.FromRgb(249, 226, 175),
            _ => Color.FromRgb(166, 227, 161)
        };
        var brush = new SolidColorBrush(color);
        AgentHead.BorderBrush = brush;
        AgentAntenna.Stroke = brush;
        AgentAntennaTip.Fill = brush;
        AgentLeftEye.Fill = brush;
        AgentRightEye.Fill = brush;

        AgentStatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AgentStatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AgentStatusTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AgentLeftEye.BeginAnimation(OpacityProperty, null);
        AgentRightEye.BeginAnimation(OpacityProperty, null);
        AgentStatusScale.ScaleX = AgentStatusScale.ScaleY = 1;
        AgentStatusTranslate.X = 0;
        AgentLeftEye.Opacity = AgentRightEye.Opacity = 1;
        AgentWaitingBadge.Visibility = Visibility.Collapsed;
        if (state == AgentActivityState.Working)
        {
            var bounce = new DoubleAnimation(1, 1.13, TimeSpan.FromMilliseconds(360))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            AgentStatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            AgentStatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }
        else if (state == AgentActivityState.Waiting)
        {
            AgentWaitingBadge.Visibility = Visibility.Visible;
            var shake = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.8) };
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(.72)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-.8, KeyTime.FromPercent(.76)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(.8, KeyTime.FromPercent(.80)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-.55, KeyTime.FromPercent(.84)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(.55, KeyTime.FromPercent(.88)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(.92)));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            AgentStatusTranslate.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        var agentName = kind switch { AgentKind.Codex => "Codex", AgentKind.Hermes => "Hermes", _ => "Terminal" };
        var stateLabel = state switch
        {
            AgentActivityState.Working => "working",
            AgentActivityState.Waiting => "waiting for you",
            AgentActivityState.Stopped => "stopped",
            AgentActivityState.Error => "error",
            AgentActivityState.Starting => "starting",
            _ => "idle"
        };
        var accessibleStatus = state == AgentActivityState.Waiting
            ? $"{agentName} is waiting for your response"
            : $"{agentName} is {stateLabel}";
        Profile.UpdateAgentStatus(state switch
        {
            AgentActivityState.Working => "working",
            AgentActivityState.Waiting => "waiting",
            AgentActivityState.Stopped => "stopped",
            AgentActivityState.Error => "error",
            AgentActivityState.Starting => "starting",
            _ => "idle"
        }, accessibleStatus);
        AgentStatusIcon.ToolTip = accessibleStatus;
        AutomationProperties.SetName(AgentStatusIcon, accessibleStatus);
        UpdateHeaderStatus();
    }

    private void UpdateHeaderStatus()
    {
        StateText.ToolTip = null;
        if (displayedAgentKind == AgentKind.Terminal)
        {
            StateText.Text = "  Windows PowerShell";
            return;
        }
        StateText.Text = agentActivityState switch
        {
            AgentActivityState.Working => "  Working",
            AgentActivityState.Waiting => "  Waiting for Response",
            AgentActivityState.Stopped => "  Stopped",
            AgentActivityState.Error => "  Error",
            AgentActivityState.Starting => "  Starting",
            _ => "  Idle"
        };
    }

    internal AgentActivityState AgentActivityStateForTest => agentActivityState;
    internal string AgentStatusTextForTest => StateText.Text;
    internal Color AgentStatusColorForTest => AgentHead.BorderBrush is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    internal bool AgentWorkingAnimationForTest => AgentStatusScale.HasAnimatedProperties;
    internal bool AgentWaitingAttentionForTest => AgentWaitingBadge.Visibility == Visibility.Visible && AgentStatusTranslate.HasAnimatedProperties;
    internal bool AccentAppliedForTest => ReferenceEquals(PaneAccentBar.Background, WorkspaceAccentPalette.BrushFor(Profile.AccentColor, WorkspaceAccentPalette.DefaultTerminal));
    internal void SetAgentStatusForTest(AgentKind kind, AgentActivityState state) => SetAgentStatus(kind, state);
    internal static bool ActivityTrackerRejectsInputEchoForTest()
    {
        var tracker = new TerminalOutputActivityTracker();
        var now = DateTime.UtcNow;
        tracker.RecordInput(now);
        return !tracker.RecordOutput("typed text", now.AddMilliseconds(30))
            && !tracker.HasRecentOutput(now.AddMilliseconds(50), TimeSpan.FromSeconds(2))
            && tracker.RecordOutput("background process output", now.AddMilliseconds(700))
            && tracker.HasRecentOutput(now.AddMilliseconds(800), TimeSpan.FromSeconds(2));
    }
    internal static bool AgentActivityClassificationForTest()
        => ClassifyAgentActivity(AgentKind.Codex, true, CodexTurnActivityState.Idle, AgentActivityState.Idle, AgentActivityState.Working) == AgentActivityState.Idle
            && ClassifyAgentActivity(AgentKind.Codex, true, CodexTurnActivityState.Working, AgentActivityState.Idle, AgentActivityState.Idle) == AgentActivityState.Working
            && ClassifyAgentActivity(AgentKind.Codex, true, CodexTurnActivityState.Waiting, AgentActivityState.Idle, AgentActivityState.Idle) == AgentActivityState.Waiting
            && ClassifyAgentActivity(AgentKind.Codex, true, CodexTurnActivityState.Working, AgentActivityState.Waiting, AgentActivityState.Idle) == AgentActivityState.Waiting
            && ClassifyAgentActivity(AgentKind.Codex, true, CodexTurnActivityState.Waiting, AgentActivityState.Working, AgentActivityState.Idle) == AgentActivityState.Working
            && ClassifyAgentActivity(AgentKind.Hermes, true, CodexTurnActivityState.Unknown, AgentActivityState.Idle, AgentActivityState.Working) == AgentActivityState.Working
            && ClassifyAgentActivity(AgentKind.Hermes, true, CodexTurnActivityState.Unknown, AgentActivityState.Idle, AgentActivityState.Waiting) == AgentActivityState.Waiting
            && ClassifyAgentActivity(AgentKind.Hermes, true, CodexTurnActivityState.Unknown, AgentActivityState.Idle, AgentActivityState.Idle) == AgentActivityState.Idle
            && ClassifyAgentActivity(AgentKind.Terminal, true, CodexTurnActivityState.Working, AgentActivityState.Waiting, AgentActivityState.Working) == AgentActivityState.Idle
            && ClassifyAgentActivity(AgentKind.Terminal, false, CodexTurnActivityState.Working, AgentActivityState.Waiting, AgentActivityState.Working) == AgentActivityState.Stopped;
    internal bool ComposerChromeStaysCompactForTest => QuickAccessButton.VerticalAlignment == VerticalAlignment.Bottom
        && QueueCommandButton.VerticalAlignment == VerticalAlignment.Bottom
        && RunCommandButton.VerticalAlignment == VerticalAlignment.Bottom
        && CommandInput.BorderThickness.Top > 0;

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
        // Keep backend output byte-for-byte transparent. Full-screen TUIs track
        // cursor position and SGR state across chunks, so deleting a protocol
        // fragment here can leave attributes (such as dotted underline) latched
        // across the entire viewport. Cursor normalization below is deliberately
        // same-length and in-place.
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
        else if (message == WmMouseWheel && IsKeyDown(VkControl))
        {
            var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
            AdjustTerminalFontSize(delta);
            handled = true;
        }
        else if (message == WmKeyDown)
        {
            var virtualKey = wParam.ToInt32();
            var now = DateTime.UtcNow;
            terminalActivity.RecordInput(now);
            var controlDown = IsKeyDown(VkControl);
            var altDown = IsKeyDown(VkMenu);
            var shiftDown = IsKeyDown(VkShift);
            if (IsPlainTerminalTabMessage(WmKeyDown, virtualKey, controlDown, altDown, shiftDown) && TryForwardTerminalTab())
            {
                Interlocked.Increment(ref terminalTabInterceptCount);
                handled = true;
            }
            else if (detectedAgentKind == AgentKind.Codex && virtualKey is VkReturn or VkEscape)
            {
                codexOutputActivity.UserResponded(now);
            }
            if (!handled && virtualKey == VkReturn && detectedAgentKind == AgentKind.Hermes) hermesActivity.TurnSubmitted();
            if (!handled && TryHandleEditShortcut(virtualKey)) handled = true;
            else if (!handled && virtualKey == VkV)
            {
                if ((controlDown || altDown) && TryHandleRemoteClipboardPaste(altDown ? RemoteImagePasteMode.FilePath : RemoteImagePasteMode.Attachment)) handled = true;
                else if (controlDown && !altDown && TryPasteClipboardText()) handled = true;
            }
        }
        if (handled) return IntPtr.Zero;
        Interlocked.Increment(ref terminalInternalMessageForwardCount);
        return terminalInternalMessageHook?.Invoke(hwnd, message, wParam, lParam, ref handled) ?? IntPtr.Zero;
    }

    private bool TryHandleEditShortcut(int virtualKey)
    {
        if (virtualKey != VkF2) return false;
        EditRequested?.Invoke(this, EventArgs.Empty);
        return true;
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
            terminalActivity.RecordInput(DateTime.UtcNow);
            terminal.WriteToTerm(FormatClipboardText(text));
            return true;
        }
        catch (ExternalException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private bool TryHandleRemoteClipboardPaste(RemoteImagePasteMode mode)
    {
        if (!TryGetActiveSshConnection(out var connectionArguments)) return false;
        try
        {
            if (Clipboard.ContainsImage()) return TryBeginRemoteClipboardImagePaste(connectionArguments, mode);
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrEmpty(text) && Terminal.ConPTYTerm is { } terminal)
                {
                    terminalActivity.RecordInput(DateTime.UtcNow);
                    terminal.WriteToTerm(FormatClipboardText(text));
                    ShowRemoteImageStatus("Text pasted", "Windows clipboard text inserted without using the remote X11 clipboard", false, true);
                    return true;
                }
            }
            ShowRemoteImageStatus("Nothing to paste", "The Windows clipboard does not contain text or an image.", false, true);
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or IOException or NotSupportedException)
        {
            ShowRemoteImageStatus("Clipboard paste failed", exception.Message, false, true);
            return true;
        }
    }

    private bool TryBeginRemoteClipboardImagePaste(string[] connectionArguments, RemoteImagePasteMode mode)
    {
        try
        {
            // Consume key-repeat messages while the same image is uploading so the hosted
            // terminal never forwards a second Ctrl+V to a headless remote agent.
            if (remoteImagePastePending) return true;
            if (Clipboard.GetImage() is not { } image)
            {
                ShowRemoteImageStatus("Image paste failed", "The clipboard image became unavailable before it could be copied.", false, true);
                return true;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length is 0 or > RemoteClipboardImageBridge.MaximumImageBytes)
            {
                ShowRemoteImageStatus("Image paste rejected", "Clipboard image is empty or larger than 20 MB.", false, true);
                return true;
            }
            remoteImagePastePending = true;
            ShowRemoteImageStatus(mode == RemoteImagePasteMode.FilePath ? "Pasting image path…" : "Pasting image…",
                mode == RemoteImagePasteMode.FilePath ? "Uploading and inserting a copyable remote path" : "Securely copying through SSH", true);
            _ = UploadRemoteClipboardImageAsync(stream.ToArray(), connectionArguments, mode);
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or IOException or NotSupportedException)
        {
            ShowRemoteImageStatus("Image paste failed", exception.Message, false, true);
            return true;
        }
    }

    private bool TryGetActiveSshConnection(out string[] connectionArguments)
    {
        connectionArguments = [];
        var marker = SshLaunchStore.Load(Profile.Id);
        var candidate = marker?.IsActive == true ? marker.ConnectionArguments
            : startupRecovery?.SshWasActive == true ? startupRecovery.SshConnectionArguments : [];
        if (!SshRecovery.TryNormalizeConnectionArguments(candidate, out var normalized, out _)) return false;
        connectionArguments = normalized;
        return true;
    }

    private async Task UploadRemoteClipboardImageAsync(byte[] imageBytes, string[] connectionArguments, RemoteImagePasteMode mode)
    {
        try
        {
            var result = await RemoteClipboardImageBridge.UploadPngAsync(imageBytes, connectionArguments);
            await Dispatcher.InvokeAsync(() =>
            {
                if (result.Succeeded && result.RemotePath is { } remotePath && Terminal.ConPTYTerm is { } terminal)
                {
                    terminalActivity.RecordInput(DateTime.UtcNow);
                    terminal.WriteToTerm(FormatClipboardText(FormatRemoteImagePasteText(remotePath, mode)));
                    Terminal.Focus();
                    ShowRemoteImageStatus(mode == RemoteImagePasteMode.FilePath ? "Image path pasted" : "Image pasted",
                        mode == RemoteImagePasteMode.FilePath ? "Copyable remote path inserted" : "Attached to the remote agent", false, true);
                }
                else ShowRemoteImageStatus("Image paste failed", result.Error ?? "Unknown SSH image transfer error.", false, true);
            });
        }
        finally { remoteImagePastePending = false; }
    }

    private void ShowRemoteImageStatus(string text, string detail, bool uploading, bool autoHide = false)
    {
        var version = Interlocked.Increment(ref remoteImageIndicatorVersion);
        RemoteImagePasteIndicator.Visibility = Visibility.Visible;
        RemoteImagePasteIndicator.ToolTip = detail;
        RemoteImagePasteStatusText.Text = text;
        RemoteImagePasteDetailText.Text = detail;
        RemoteImagePasteProgress.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        RemoteImagePasteGlyph.Text = uploading ? "⇧" : text.Contains("failed", StringComparison.OrdinalIgnoreCase) || text.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? "!" : "✓";
        RemoteImagePasteGlyph.Foreground = new SolidColorBrush(uploading ? Color.FromRgb(249, 226, 175)
            : text.Contains("failed", StringComparison.OrdinalIgnoreCase) || text.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                ? Color.FromRgb(243, 139, 168) : Color.FromRgb(166, 227, 161));
        if (autoHide) _ = HideRemoteImageStatusAsync(version, text.Contains("failed", StringComparison.OrdinalIgnoreCase) ? 4500 : 2500);
    }

    private async Task HideRemoteImageStatusAsync(long version, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds);
        if (Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref remoteImageIndicatorVersion) == version)
                RemoteImagePasteIndicator.Visibility = Visibility.Collapsed;
        });
    }

    private static string FormatClipboardText(string text)
    {
        var safeText = text.Length > MaximumClipboardCharacters ? text[..MaximumClipboardCharacters] : text;
        safeText = safeText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\u001b", string.Empty, StringComparison.Ordinal);
        return $"\u001b[200~{safeText}\u001b[201~";
    }

    private static string FormatRemoteImagePasteText(string remotePath, RemoteImagePasteMode mode)
        => mode == RemoteImagePasteMode.FilePath ? $"`{remotePath}`" : remotePath;

    private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    internal string TitleTextForTest => TitleText.Text;
    internal AgentKind DetectedAgentKind => detectedAgentKind;
    internal bool TriggerEditShortcutForTest() => TryHandleEditShortcut(VkF2);
    internal bool HasRemoteImagePasteIndicatorForTest => RemoteImagePasteIndicator is not null;
    internal bool TerminalInputRouterPrecedesConPtyForTest()
    {
        if (terminalContainer is null || !terminalMessageRouterInstalled) return false;
        var field = typeof(HwndHost).GetField("_hooks", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(terminalContainer) is not ArrayList hooks) return false;
        var callbacks = hooks.Cast<object>().OfType<HwndSourceHook>().ToList();
        return callbacks.Any(value => ReferenceEquals(value.Target, this) && value.Method.Name == nameof(TerminalMessageHook))
            && callbacks.All(value => value.Method.Name != "TerminalContainer_MessageHook");
    }
    internal static bool RemoteImageShortcutsClassifiedForTest()
        => IsRemoteImageShortcutMessage(WmKeyDown, VkV, true, false)
            && IsRemoteImageShortcutMessage(WmSysKeyDown, VkV, false, true)
            && IsRemoteImagePasteCharacter(WmChar, 'v') && IsRemoteImagePasteCharacter(WmSysChar, 'V')
            && IsRemoteImagePasteKeyUp(WmKeyUp, VkV) && IsRemoteImagePasteKeyUp(WmSysKeyUp, VkV)
            && !IsRemoteImageShortcutMessage(WmKeyDown, VkV, false, false)
            && !IsRemoteImageShortcutMessage(WmKeyDown, VkF2, true, false);
    internal static bool RemoteImagePasteModesFormatForTest()
    {
        const string path = "/home/ubuntu/.cache/powershellplus/images/clipboard-test.png";
        return FormatRemoteImagePasteText(path, RemoteImagePasteMode.Attachment) == path
            && FormatRemoteImagePasteText(path, RemoteImagePasteMode.FilePath) == $"`{path}`";
    }
    internal bool AddComposerAttachmentForTest(string path, bool isImage)
        => AddComposerAttachment(path, isImage, false, true);
    internal bool DropComposerFileForTest(string path)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        var files = GetDroppedComposerFiles(data);
        var before = composerAttachments.Count;
        AttachDroppedComposerFiles(files);
        var fullPath = Path.GetFullPath(path);
        return HasShellFileDrop(data) && files.Count == 1 && composerAttachments.Count == before + 1
            && composerAttachments.Any(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            && CommandInput.Text.Contains(fullPath, StringComparison.OrdinalIgnoreCase)
            && Profile.ComposerAttachments.Any(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
    }
    internal bool ReplaceFirstAttachmentFromFileDropForTest(string path)
    {
        var target = composerAttachments.FirstOrDefault();
        if (target is null) return false;
        var count = composerAttachments.Count;
        var fullPath = Path.GetFullPath(path);
        if (!ReplaceComposerAttachment(target, fullPath)) return false;
        var replacement = composerAttachments.FirstOrDefault(value => value.Id == target.Id);
        return replacement is not null && composerAttachments.Count == count
            && replacement.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
            && replacement.DisplayName.StartsWith(AttachmentLabelStem(GetAttachmentPreviewKind(fullPath)), StringComparison.Ordinal)
            && !CommandInput.Text.Contains(target.LocalPath, StringComparison.OrdinalIgnoreCase)
            && CommandInput.Text.Contains(fullPath, StringComparison.OrdinalIgnoreCase)
            && Profile.CommandDraft == CommandInput.Text
            && Profile.ComposerAttachments.Any(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
    }
    internal bool ComposerFileDropIndicatorsWorkForTest()
    {
        if (!commandInputFileDropHandlersInstalled || !CommandInput.AllowDrop || ComposerFileDropIndicator.IsHitTestVisible
            || ComposerFileDropIndicator.Visibility != Visibility.Collapsed
            || attachmentDropIndicators.Count != composerAttachments.Count) return false;
        var pill = attachmentDropIndicators.Keys.FirstOrDefault();
        if (pill is null) return false;
        SetAttachmentPillDropIndicator(pill, true);
        var shown = attachmentDropIndicators[pill].Visibility == Visibility.Visible
            && !attachmentDropIndicators[pill].IsHitTestVisible;
        SetAttachmentPillDropIndicator(pill, false);
        return shown && attachmentDropIndicators[pill].Visibility == Visibility.Collapsed;
    }
    internal bool PastePlainTextAttachmentForTest(string text, string expectedPath)
    {
        CommandInput.SimulatePlainTextPasteForTest(text);
        var fullPath = Path.GetFullPath(expectedPath);
        return composerAttachments.Any(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            && CommandInput.Text.Contains(fullPath, StringComparison.OrdinalIgnoreCase)
            && !CommandInput.Text.Contains($"\"{fullPath}\"", StringComparison.OrdinalIgnoreCase)
            && Profile.ComposerAttachments.Any(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
    }
    internal int ComposerAttachmentCountForTest => composerAttachments.Count;
    internal bool AttachmentStripVisibleForTest => AttachmentStrip.Visibility == Visibility.Visible;
    internal bool ComposerTypingAvoidsPillRebuildForTest()
    {
        if (composerAttachments.Count == 0) return false;
        var original = CommandInput.Text;
        var refreshes = attachmentPillRefreshCount;
        CommandInput.Text = original + "x";
        CommandInput.Text = original;
        return attachmentPillRefreshCount == refreshes && composerAttachments.Count > 0;
    }
    internal bool OpenFirstAttachmentPreviewForTest()
    {
        var attachment = composerAttachments.FirstOrDefault(value => value.IsImage);
        if (attachment is null) return false;
        var previewButton = FindVisualChild<Button>(AttachmentPillPanel);
        if (previewButton is null || previewButton.ToolTip?.ToString()?.StartsWith("Preview ", StringComparison.Ordinal) != true) return false;
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previewButton));
        var openedAboveNativeSurface = AttachmentPreviewOverlay.Visibility == Visibility.Visible
            && AttachmentPreviewImage.Source is not null
            && Terminal.Visibility == Visibility.Hidden
            && AttachmentPreviewCard.HorizontalAlignment == HorizontalAlignment.Stretch
            && AttachmentPreviewCard.VerticalAlignment == VerticalAlignment.Stretch
            && AttachmentPreviewViewport.ClipToBounds
            && AttachmentPreviewImage.Stretch == Stretch.Uniform
            && AttachmentPreviewImage.StretchDirection == StretchDirection.DownOnly
            && AttachmentPreviewMedia.Stretch == Stretch.Uniform
            && AttachmentPreviewMedia.StretchDirection == StretchDirection.DownOnly;
        CloseAttachmentPreview();
        return openedAboveNativeSurface
            && AttachmentPreviewOverlay.Visibility == Visibility.Collapsed
            && Terminal.Visibility == Visibility.Visible;
    }
    internal bool RemoveFirstAttachmentPathForTest()
    {
        var attachment = composerAttachments.FirstOrDefault();
        if (attachment is null) return false;
        CommandInput.Text = CommandInput.Text.Replace(attachment.LocalPath, string.Empty, StringComparison.OrdinalIgnoreCase);
        FlushComposerState();
        return composerAttachments.Count == 0 && Profile.ComposerAttachments.Count == 0
            && AttachmentStrip.Visibility == Visibility.Collapsed && Profile.CommandDraft == CommandInput.Text;
    }
    internal bool ComposerDraftPersistedForTest
    {
        get
        {
            FlushComposerState();
            return Profile.CommandDraft == CommandInput.Text
                && Profile.ComposerAttachments.Count == composerAttachments.Count;
        }
    }
    internal bool ComposerTokensMatchCanonicalPathsForTest => composerAttachments.Count > 0
        && CommandInput.RenderedTokenLabelsForTest.SequenceEqual(composerAttachments.Select(value => value.DisplayName))
        && composerAttachments.All(value => CommandInput.Text.Contains(value.LocalPath, StringComparison.OrdinalIgnoreCase))
        && CommandInput.ToggleFirstTokenForTest();
    internal bool ComposerBlankSpacePreservesTokensForTest => CommandInput.BlankSpaceDoesNotToggleAttachmentForTest();
    internal bool ComposerScrollbarThemedForTest => CommandInput.UsesThemedScrollbarForTest && CommandInput.MaxLines == 8;
    internal bool ReorderFirstTwoAttachmentsForTest()
    {
        if (composerAttachments.Count < 2) return false;
        var first = composerAttachments[0];
        var second = composerAttachments[1];
        var before = CommandInput.Text;
        CommandInput.Text = SwapAttachmentPaths(before, first.LocalPath, second.LocalPath);
        FlushComposerState();
        return composerAttachments.Count >= 2 && composerAttachments[0].Id == second.Id
            && composerAttachments[0].DisplayName == "Image 1" && composerAttachments[1].DisplayName == "Image 2"
            && FirstAttachmentPathIndex(CommandInput.Text, second.LocalPath) < FirstAttachmentPathIndex(CommandInput.Text, first.LocalPath);
    }
    internal bool PerTerminalFontZoomPersistsForTest()
    {
        var terminalBefore = EffectiveTerminalFontSize(currentAppearance);
        var composerBefore = Profile.CommandFontSize ?? (int)Math.Round(CommandInput.FontSize);
        AdjustTerminalFontSize(120);
        AdjustComposerFontSize(120);
        return Profile.TerminalFontSize == Math.Min(MaximumTerminalFontSize, terminalBefore + 1)
            && Profile.CommandFontSize == Math.Min(MaximumComposerFontSize, composerBefore + 1)
            && Terminal.FontSizeWhenSettingTheme == Profile.TerminalFontSize
            && Math.Abs(CommandInput.FontSize - Profile.CommandFontSize.Value) < .1;
    }
    internal static bool AttachmentPreviewKindsForTest()
        => GetAttachmentPreviewKind("preview.png") == AttachmentPreviewKind.Image
            && GetAttachmentPreviewKind("preview.mp4") == AttachmentPreviewKind.Media
            && GetAttachmentPreviewKind("preview.md") == AttachmentPreviewKind.Text
            && GetAttachmentPreviewKind("preview.zip") == AttachmentPreviewKind.Generic;
    internal void ClearComposerAttachmentsForTest() => RemoveComposerAttachments(composerAttachments.ToArray());
    internal static string RewriteAttachmentPathsForTest(string command, IReadOnlyDictionary<string, string> replacements)
        => RewriteAttachmentPaths(command, replacements);
    internal static bool RemoteSshPasteRoutingConsumesAllClipboardKindsForTest()
        => Enum.GetValues<RemoteClipboardPasteContent>().All(value => ShouldConsumeRemoteSshPasteForTest(value))
            && !ShouldConsumeRemoteSshPasteForTest(RemoteClipboardPasteContent.Text, false);
    private static bool ShouldConsumeRemoteSshPasteForTest(RemoteClipboardPasteContent content, bool sshActive = true)
        => sshActive && content is RemoteClipboardPasteContent.Image or RemoteClipboardPasteContent.Text or RemoteClipboardPasteContent.Empty;
    internal bool ExerciseThreadMessagePasteInterceptionForTest()
    {
        AttachTerminalActivationHook();
        RegisterTerminalThreadMessageHook();
        if (terminalContainer?.Handle is not { } handle || handle == IntPtr.Zero) return false;
        var beforeIntercept = Volatile.Read(ref terminalThreadMessageInterceptCount);
        var beforeForward = Volatile.Read(ref terminalInternalMessageForwardCount);
        remoteClipboardPasteTestOverride = _ => true;
        terminalShortcutTestModifiers = (true, false, false);
        try
        {
            var keyDown = new MSG { hwnd = handle, message = WmKeyDown, wParam = new IntPtr(VkV) };
            var keyDownHandled = false;
            TerminalThreadPreprocessMessage(ref keyDown, ref keyDownHandled);
            var keyUp = new MSG { hwnd = handle, message = WmKeyUp, wParam = new IntPtr(VkV) };
            var keyUpHandled = false;
            TerminalThreadPreprocessMessage(ref keyUp, ref keyUpHandled);
            return terminalThreadMessageHookInstalled && keyDownHandled && keyUpHandled
                && Volatile.Read(ref terminalThreadMessageInterceptCount) == beforeIntercept + 1
                && Volatile.Read(ref terminalInternalMessageForwardCount) == beforeForward;
        }
        finally
        {
            remoteClipboardPasteTestOverride = null;
            terminalShortcutTestModifiers = null;
            suppressRemoteImagePasteVSequence = false;
        }
    }
    internal bool ExerciseThreadMessageTabInterceptionForTest()
    {
        AttachTerminalActivationHook();
        RegisterTerminalThreadMessageHook();
        if (terminalContainer?.Handle is not { } handle || handle == IntPtr.Zero) return false;
        var beforeIntercept = Volatile.Read(ref terminalTabInterceptCount);
        terminalShortcutTestModifiers = (false, false, false);
        terminalTabForwardTestOverride = () => true;
        try
        {
            var keyDown = new MSG { hwnd = handle, message = WmKeyDown, wParam = new IntPtr(VkTab) };
            var handled = false;
            TerminalThreadPreprocessMessage(ref keyDown, ref handled);
            return terminalThreadMessageHookInstalled && handled
                && Volatile.Read(ref terminalTabInterceptCount) == beforeIntercept + 1
                && IsPlainTerminalTabMessage(WmKeyDown, VkTab, false, false, false)
                && !IsPlainTerminalTabMessage(WmKeyDown, VkTab, false, false, true)
                && !IsPlainTerminalTabMessage(WmKeyDown, VkTab, true, false, false)
                && !IsPlainTerminalTabMessage(WmKeyUp, VkTab, false, false, false);
        }
        finally
        {
            terminalShortcutTestModifiers = null;
            terminalTabForwardTestOverride = null;
        }
    }
    internal bool ExerciseRemoteImagePasteIndicatorForTest()
    {
        ShowRemoteImageStatus("Pasting image…", "Securely copying through SSH", true);
        var uploadingVisible = RemoteImagePasteIndicator.Visibility == Visibility.Visible
            && RemoteImagePasteProgress.Visibility == Visibility.Visible && RemoteImagePasteStatusText.Text.Contains("Pasting", StringComparison.Ordinal);
        ShowRemoteImageStatus("Image pasted", "Attached to the remote agent", false);
        var attachedVisible = RemoteImagePasteIndicator.Visibility == Visibility.Visible
            && RemoteImagePasteProgress.Visibility == Visibility.Collapsed && RemoteImagePasteGlyph.Text == "✓";
        RemoteImagePasteIndicator.Visibility = Visibility.Collapsed;
        return uploadingVisible && attachedVisible;
    }

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

    private delegate IntPtr WindowSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr windowHandle, WindowSubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    public static string BuildCommandLine(SessionProfile profile, SessionRecoveryEntry? recovery, bool skipPowerShellProfile = false)
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
        var hasExactCodexSession = resumeCodex && CodexSessionLocator.IsSafeCodexId(recovery?.CodexSessionId);
        var permissionsArgument = hasExactCodexSession
            ? CodexResumeArguments.BuildPowerShell(recovery?.CodexPermissionProfile, recovery?.CodexSandboxMode,
                recovery?.CodexApprovalPolicy, recovery?.CodexApprovalsReviewer)
            : string.Empty;
        if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase) || command.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            if (skipPowerShellProfile && !command.Contains("-NoProfile", StringComparison.OrdinalIgnoreCase)) command += " -NoProfile";
            var script = validDirectory ? $"Set-Location -LiteralPath '{escaped}'; " : string.Empty;
            script += CodexLaunchStore.BuildPowerShellWrapper(profile.Id);
            script += "; " + SshLaunchStore.BuildPowerShellWrapper(profile.Id, usePersistentSession: profile.UseRemoteTmux);
            script += "; " + BuildPowerShellDirectoryHook();
            if (resumeSsh) script += "; " + sshResumeCommand;
            else if (resumeCodex) script += $"; & codex resume{resumeArgument}{modelArgument}{permissionsArgument}";
            if (script.Length == 0) return command;
            var scriptPath = PowerShellStartupScriptStore.Save(profile.Id, script);
            return $"{command} -NoExit -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        }
        if (resumeCodex && Path.GetFileNameWithoutExtension(command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty).Equals("codex", StringComparison.OrdinalIgnoreCase))
            return $"codex resume{resumeArgument}{modelArgument}{permissionsArgument}";
        return command;
    }

    internal static string BuildPowerShellDirectoryHook()
        => "$global:__PowerShellPlusOriginalPrompt = $function:prompt; function global:prompt { "
            + "$__pspLocation = $executionContext.SessionState.Path.CurrentLocation.ProviderPath; "
            + "$__pspLocationMarker = ([string][char]27) + ']9;9;\"' + $__pspLocation + '\"' + ([char]27) + '\\'; "
            + "$__pspRenderedPrompt = & $global:__PowerShellPlusOriginalPrompt; "
            + "return $__pspLocationMarker + ($__pspRenderedPrompt -join '') }";

    internal static bool ProfileStartupWatchdogWorksForTest()
    {
        var profile = new SessionProfile
        {
            Id = "profile-watchdog-test",
            CommandLine = "powershell.exe",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        var commandLine = BuildCommandLine(profile, null, true);
        return commandLine.Contains("-NoProfile", StringComparison.OrdinalIgnoreCase)
            && ShouldRecoverStalledProfile(string.Empty, [new ConsoleDescendantProcess(42, "oh-my-posh")])
            && ShouldRecoverStalledProfile(string.Empty, [new ConsoleDescendantProcess(43, "starship")])
            && !ShouldRecoverStalledProfile("PS C:\\Users\\Example>", [new ConsoleDescendantProcess(44, "oh-my-posh")])
            && !ShouldRecoverStalledProfile(string.Empty, [new ConsoleDescendantProcess(45, "git")])
            && !DecodePowerShellStartupScript(commandLine).Contains("profile previously stalled", StringComparison.OrdinalIgnoreCase);
    }

    public static string DecodePowerShellStartupScript(string commandLine)
    {
        var storedScript = PowerShellStartupScriptStore.ReadFromCommandLine(commandLine);
        if (storedScript.Length > 0) return storedScript;
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

    public void SetPreviousOutputHiddenByDefaultForTest(string output)
    {
        previousOutput = output;
        ConfigureRecoveryView();
    }

    public void HidePreviousOutputForTest() => SetRecoverySurfaceVisible(false);

    private void SetRecoverySurfaceVisible(bool visible)
    {
        // EasyTerminalControl contains an HWND. WPF overlays cannot reliably
        // cover an HWND in the same airspace: focus or composition changes can
        // make either surface flash through. Exactly one viewport owner must be
        // visible at a time.
        TerminalSurfaceGrid.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
        RecoveryOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureRecoveryView(bool show = false)
    {
        if (string.IsNullOrWhiteSpace(previousOutput)) return;
        PreviousOutputButton.Visibility = Visibility.Visible;
        RecoveryOutputText.Text = TerminalTextSanitizer.ForTranscript(previousOutput);
        RecoveryTimestampText.Text = startupRecovery?.CapturedUtc.ToLocalTime().ToString("Recovered MMM d, yyyy 'at' h:mm tt") ?? "Recovered after restart";
        // Recovered output remains one click away in the pane header, but a live
        // terminal always owns the viewport after startup. Auto-opening this
        // WPF surface over an HWND is both surprising and vulnerable to native
        // airspace flashes when focus changes.
        SetRecoverySurfaceVisible(show);
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
    private void AutomationButtonClick(object sender, RoutedEventArgs e) { ShowAutomationMenu(); e.Handled = true; }
    private void CommandHistoryClick(object sender, RoutedEventArgs e) { SetCommandHistoryVisible(CommandHistoryPanel.Visibility != Visibility.Visible); e.Handled = true; }
    private void ClearCommandHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Profile.CommandHistory.Count == 0) return;
        var historyLabel = Profile.CommandHistory.Count == 1 ? "history entry" : "history entries";
        if (!PowerShellPlusDialog.Confirm(Window.GetWindow(this),
                $"Clear all {Profile.CommandHistory.Count} saved {historyLabel} for {Profile.Name}?\n\nThis cannot be undone.",
                "Clear history?", PowerShellPlusDialogKind.Warning, "Clear history", "Cancel",
                defaultToPrimary: false, primaryIsDangerous: true)) return;
        ClearCommandHistory();
        CommandInput.Focus();
    }
    private void CommandHistoryEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryDisplayEntry entry) RestoreCommandHistory(entry.Message);
        e.Handled = true;
    }
    private void QueueCommandClick(object sender, RoutedEventArgs e) { ShowQueueMenu(); e.Handled = true; }
    private async void RunCommandClick(object sender, RoutedEventArgs e) { await RunCommandInputAsync(IsSendToAllActive(Keyboard.Modifiers)); e.Handled = true; }
    private void RunCommandRightButtonUp(object sender, MouseButtonEventArgs e) { ShowRunCommandSettingsMenu(); e.Handled = true; }
    private async void CommandInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        RefreshSendButtonVisual();
        e.Handled = await HandleCommandInputKeyAsync(e.Key, e.KeyboardDevice.Modifiers);
    }
    private async Task<bool> HandleCommandInputKeyAsync(Key key, ModifierKeys modifiers)
    {
        if (key == Key.V && (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt))
            && TryPasteComposerAttachments()) return true;
        if (key == Key.Enter && modifiers.HasFlag(ModifierKeys.Control)) { QueueCurrentCommand(); return true; }
        if ((key == Key.J && modifiers == ModifierKeys.Control)
            || (key == Key.Enter && modifiers == ModifierKeys.Shift))
        {
            CommandInput.InsertLineBreakAtCaret();
            return true;
        }
        if (key == Key.U && modifiers == ModifierKeys.Control)
        {
            CommandInput.DeleteToCurrentLineBoundary(true);
            return true;
        }
        if (key == Key.K && modifiers == ModifierKeys.Control)
        {
            CommandInput.DeleteToCurrentLineBoundary(false);
            return true;
        }
        if (key == Key.Enter) { await RunCommandInputAsync(IsSendToAllActive(modifiers)); return true; }
        if (modifiers == ModifierKeys.None && key == Key.Up)
        {
            if (!CommandInput.TryMoveCaretByVisualLine(-1)) NavigateQueue(-1);
            return true;
        }
        if (modifiers == ModifierKeys.None && key == Key.Down)
        {
            if (!CommandInput.TryMoveCaretByVisualLine(1)) NavigateQueue(1);
            return true;
        }
        return false;
    }

    private bool TryPasteComposerAttachments()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return false;
            var attached = false;
            if (data.GetDataPresent(DataFormats.FileDrop, true)
                && data.GetData(DataFormats.FileDrop, true) is string[] droppedFiles)
            {
                foreach (var path in droppedFiles.Where(File.Exists).Take(MaximumComposerAttachments - composerAttachments.Count))
                    attached |= AddComposerAttachment(path, IsImageFile(path), false, true);
                if (attached) return true;
            }
            if (data.GetDataPresent(DataFormats.Bitmap, true) && Clipboard.GetImage() is { } image)
            {
                var directory = Path.Combine(WorkspaceStore.DirectoryPath, "composer-attachments", SessionRecoveryStore.SafeSessionId(Profile.Id));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"image-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}"[..22] + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path)) encoder.Save(stream);
                return AddComposerAttachment(path, true, true, true);
            }
            if (data.GetDataPresent(DataFormats.UnicodeText, true))
            {
                var candidate = (data.GetData(DataFormats.UnicodeText, true) as string ?? string.Empty).Trim().Trim('"', '\'', '`');
                if (File.Exists(candidate)) return AddComposerAttachment(candidate, IsImageFile(candidate), false, true);
            }
            return false;
        }
        catch (Exception exception) when (exception is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ShowRemoteImageStatus("Attachment paste failed", exception.Message, false, true);
            return true;
        }
    }

    private bool AddComposerAttachment(string path, bool isImage, bool isTemporary, bool insertPath)
    {
        if (composerAttachments.Count >= MaximumComposerAttachments)
        {
            ShowRemoteImageStatus("Attachment limit reached", $"A command can include up to {MaximumComposerAttachments} files.", false, true);
            return true;
        }
        if (!TryNormalizeComposerFile(path, out var fullPath))
        {
            ShowRemoteImageStatus("Attachment rejected", "Files must exist locally and be between 1 byte and 100 MB.", false, true);
            if (isTemporary) try { File.Delete(path); } catch { }
            return true;
        }
        var attachment = composerAttachments.FirstOrDefault(value => value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (attachment is null)
        {
            attachment = new ComposerAttachment(Guid.NewGuid().ToString("N"), fullPath,
                Path.GetFileName(fullPath), isImage, isTemporary);
            composerAttachments.Add(attachment);
            RefreshAttachmentPills();
            PersistComposerAttachments();
        }
        if (insertPath) InsertComposerPath(fullPath);
        ShowRemoteImageStatus(isImage ? "Image attached" : "File attached",
            "The local path is in the command and will be replaced with a private VPS path when sent over SSH.", false, true);
        return true;
    }

    private void InsertComposerPath(string path)
    {
        var insertion = path;
        var caret = CommandInput.CaretIndex;
        if (caret > 0 && !char.IsWhiteSpace(CommandInput.Text[caret - 1])) insertion = " " + insertion;
        if (caret < CommandInput.Text.Length && !char.IsWhiteSpace(CommandInput.Text[caret])) insertion += " ";
        CommandInput.SelectedText = insertion;
        CommandInput.CaretIndex = caret + insertion.Length;
        CommandInput.Focus();
    }

    private void RefreshAttachmentPills()
    {
        if (synchronizingComposerAttachments) return;
        attachmentPillRefreshCount++;
        synchronizingComposerAttachments = true;
        try
        {
            var changed = SynchronizeComposerAttachmentOrder();
            CommandInput.SetAttachmentTokens(composerAttachments.Select(value => new ComposerTokenDescriptor(
                value.Id, value.LocalPath, value.DisplayName, GetAttachmentPreviewKind(value.LocalPath))));
            AttachmentPillPanel.Children.Clear();
            attachmentDropIndicators.Clear();
            foreach (var attachment in composerAttachments)
            {
                var previewContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (attachment.IsImage && LoadAttachmentBitmap(attachment.LocalPath, 72) is { } thumbnail)
                {
                    previewContent.Children.Add(new Image
                    {
                        Source = thumbnail,
                        Width = 28,
                        Height = 22,
                        Stretch = Stretch.UniformToFill,
                        Margin = new Thickness(0, 0, 7, 0),
                    });
                }
                previewContent.Children.Add(new TextBlock
                {
                    Text = attachment.DisplayName,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(AttachmentLabelColor(GetAttachmentPreviewKind(attachment.LocalPath)))
                });
                var previewButton = new Button
                {
                    Content = previewContent,
                    Tag = attachment,
                    Padding = new Thickness(6, 3, 6, 3),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = $"Preview {attachment.DisplayName} · drag to reorder · {attachment.LocalPath}"
                };
                AutomationProperties.SetName(previewButton, $"Preview {attachment.DisplayName}");
                var remove = new Button { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(7, 0, 0, 0), Tag = attachment };
                remove.Click += RemoveAttachmentClick;
                var content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition());
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(previewButton, 0);
                Grid.SetColumn(remove, 1);
                content.Children.Add(previewButton);
                content.Children.Add(remove);
                var replacementIndicator = new Border
                {
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                    Background = new SolidColorBrush(Color.FromArgb(244, 30, 30, 46)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(137, 180, 250)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = "↻", Foreground = new SolidColorBrush(Color.FromRgb(137, 180, 250)), FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 5, 0) },
                            new TextBlock { Text = "Drop to replace", Foreground = new SolidColorBrush(Color.FromRgb(205, 214, 244)), FontSize = 9, FontWeight = FontWeights.SemiBold }
                        }
                    }
                };
                Grid.SetColumnSpan(replacementIndicator, 2);
                Panel.SetZIndex(replacementIndicator, 4);
                content.Children.Add(replacementIndicator);
                var pill = new Border
                {
                    Child = content,
                    Tag = attachment,
                    AllowDrop = true,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 5, 0),
                    Background = new SolidColorBrush(Color.FromRgb(36, 36, 56)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(88, 91, 112)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Open preview · drag to reorder · {attachment.LocalPath}"
                };
                attachmentDropIndicators[pill] = replacementIndicator;
                pill.PreviewMouseLeftButtonDown += (_, eventArgs) =>
                {
                    if (IsWithin(eventArgs.OriginalSource as DependencyObject, remove)) return;
                    attachmentDragStart = eventArgs.GetPosition(pill);
                    attachmentDragId = attachment.Id;
                    attachmentDragOccurred = false;
                };
                pill.PreviewMouseMove += AttachmentPillMouseMove;
                previewButton.Click += (_, eventArgs) =>
                {
                    if (!attachmentDragOccurred) OpenAttachmentPreview(attachment);
                    attachmentDragStart = null;
                    attachmentDragId = null;
                    attachmentDragOccurred = false;
                    eventArgs.Handled = true;
                };
                pill.DragEnter += (_, eventArgs) => AttachmentPillFileDragOver(pill, eventArgs);
                pill.DragOver += (_, eventArgs) => AttachmentPillFileDragOver(pill, eventArgs);
                pill.DragLeave += (_, eventArgs) => AttachmentPillFileDragLeave(pill, eventArgs);
                pill.Drop += (_, eventArgs) => AttachmentPillDrop(pill, eventArgs);
                AttachmentPillPanel.Children.Add(pill);
            }
            AttachmentStrip.Visibility = composerAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (changed) PersistComposerAttachments(false);
        }
        finally { synchronizingComposerAttachments = false; }
    }

    private bool SynchronizeComposerAttachmentOrder()
    {
        var original = composerAttachments.Select(value => (value.Id, value.DisplayName)).ToArray();
        var ordered = composerAttachments
            .OrderBy(value => FirstAttachmentPathIndex(CommandInput.Text, value.LocalPath))
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToList();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        composerAttachments.Clear();
        foreach (var attachment in ordered)
        {
            var stem = AttachmentLabelStem(GetAttachmentPreviewKind(attachment.LocalPath));
            var number = counters.TryGetValue(stem, out var count) ? count + 1 : 1;
            counters[stem] = number;
            composerAttachments.Add(attachment with { DisplayName = $"{stem} {number}" });
        }
        return !original.SequenceEqual(composerAttachments.Select(value => (value.Id, value.DisplayName)));
    }

    private static int FirstAttachmentPathIndex(string command, string path)
    {
        var index = command.IndexOf(path, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? int.MaxValue : index;
    }

    private static string AttachmentLabelStem(AttachmentPreviewKind kind) => kind switch
    {
        AttachmentPreviewKind.Image => "Image",
        AttachmentPreviewKind.Media => "Video",
        AttachmentPreviewKind.Text => "Text",
        _ => "File"
    };

    private static Color AttachmentLabelColor(AttachmentPreviewKind kind) => kind switch
    {
        AttachmentPreviewKind.Image => Color.FromRgb(166, 227, 161),
        AttachmentPreviewKind.Media => Color.FromRgb(137, 180, 250),
        AttachmentPreviewKind.Text => Color.FromRgb(249, 226, 175),
        _ => Color.FromRgb(203, 166, 247)
    };

    private void AttachmentPillMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border pill || attachmentDragStart is not { } start || attachmentDragId is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(pill);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        attachmentDragOccurred = true;
        DragDrop.DoDragDrop(pill, new DataObject("PowerShellPlus.ComposerAttachment", attachmentDragId), DragDropEffects.Move);
        attachmentDragStart = null;
        attachmentDragId = null;
    }

    private void AttachmentPillFileDragOver(Border pill, DragEventArgs e)
    {
        if (HasShellFileDrop(e.Data))
        {
            var accepted = GetDroppedComposerFiles(e.Data).Count > 0;
            SetAttachmentPillDropIndicator(pill, accepted);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        SetAttachmentPillDropIndicator(pill, false);
        if (e.Data.GetDataPresent("PowerShellPlus.ComposerAttachment"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void AttachmentPillFileDragLeave(Border pill, DragEventArgs e)
    {
        SetAttachmentPillDropIndicator(pill, false);
        if (HasShellFileDrop(e.Data)) e.Handled = true;
    }

    private void SetAttachmentPillDropIndicator(Border pill, bool visible)
    {
        if (attachmentDropIndicators.TryGetValue(pill, out var indicator))
            indicator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AttachmentPillDrop(Border pill, DragEventArgs e)
    {
        SetAttachmentPillDropIndicator(pill, false);
        if (pill.Tag is not ComposerAttachment target) return;
        if (HasShellFileDrop(e.Data))
        {
            var files = GetDroppedComposerFiles(e.Data);
            var replaced = files.Count > 0 && ReplaceComposerAttachment(target, files[0]);
            if (files.Count == 0)
                ShowRemoteImageStatus("File cannot replace attachment", "Drop a non-empty local file smaller than 100 MB.", false, true);
            e.Effects = replaced ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (e.Data.GetData("PowerShellPlus.ComposerAttachment") is not string sourceId
            || sourceId == target.Id) return;
        var source = composerAttachments.FirstOrDefault(value => value.Id == sourceId);
        if (source is null) return;
        CommandInput.Text = SwapAttachmentPaths(CommandInput.Text, source.LocalPath, target.LocalPath);
        CommandInput.CaretIndex = CommandInput.Text.Length;
        e.Handled = true;
    }

    private bool ReplaceComposerAttachment(ComposerAttachment target, string path)
    {
        if (!TryNormalizeComposerFile(path, out var fullPath)) return false;
        var index = composerAttachments.FindIndex(value => value.Id == target.Id);
        if (index < 0) return false;
        if (target.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (composerAttachments.Any(value => value.Id != target.Id && value.LocalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowRemoteImageStatus("File is already attached", "Choose a different file for this attachment slot.", false, true);
            return false;
        }

        var updatedCommand = CommandInput.Text.Replace(target.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase);
        if (updatedCommand.Equals(CommandInput.Text, StringComparison.Ordinal)) return false;
        var originalCaret = CommandInput.CaretIndex;
        var replacement = target with
        {
            LocalPath = fullPath,
            DisplayName = Path.GetFileName(fullPath),
            IsImage = IsImageFile(fullPath),
            IsTemporary = false
        };
        composerAttachments[index] = replacement;
        synchronizingComposerAttachments = true;
        try
        {
            CommandInput.Text = updatedCommand;
            CommandInput.CaretIndex = Math.Clamp(originalCaret + fullPath.Length - target.LocalPath.Length, 0, CommandInput.Text.Length);
            Profile.CommandDraft = CommandInput.Text;
        }
        finally { synchronizingComposerAttachments = false; }
        RefreshAttachmentPills();
        PersistComposerAttachments();
        if (target.IsTemporary)
        {
            try { File.Delete(target.LocalPath); } catch { }
        }
        ShowRemoteImageStatus(replacement.IsImage ? "Image replaced" : "File replaced",
            "The command path and attachment type were updated together.", false, true);
        CommandInput.Focus();
        return true;
    }

    private static string SwapAttachmentPaths(string command, string first, string second)
    {
        var marker = $"__PSPLUS_ATTACHMENT_{Guid.NewGuid():N}__";
        return command.Replace(first, marker, StringComparison.OrdinalIgnoreCase)
            .Replace(second, first, StringComparison.OrdinalIgnoreCase)
            .Replace(marker, second, StringComparison.Ordinal);
    }

    private void RemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ComposerAttachment attachment })
        {
            var updated = CommandInput.Text.Replace($"\"{attachment.LocalPath}\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(attachment.LocalPath, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (updated != CommandInput.Text) CommandInput.Text = updated;
            else RemoveComposerAttachments([attachment]);
        }
        e.Handled = true;
    }

    private void OpenAttachmentPreview(ComposerAttachment attachment)
    {
        CloseAttachmentPreview();
        if (!File.Exists(attachment.LocalPath)) return;
        AttachmentPreviewTitle.Text = attachment.DisplayName;
        var kind = GetAttachmentPreviewKind(attachment.LocalPath);
        switch (kind)
        {
            case AttachmentPreviewKind.Image when LoadAttachmentBitmap(attachment.LocalPath, 1400) is { } image:
                AttachmentPreviewImage.Source = image;
                AttachmentPreviewImage.Visibility = Visibility.Visible;
                break;
            case AttachmentPreviewKind.Media:
                AttachmentPreviewMediaPanel.Visibility = Visibility.Visible;
                AttachmentPreviewMediaStatus.Text = "Loading mediaâ€¦";
                AttachmentPreviewMedia.Source = new Uri(attachment.LocalPath, UriKind.Absolute);
                break;
            case AttachmentPreviewKind.Text:
                AttachmentPreviewText.Text = ReadTextPreview(attachment.LocalPath);
                AttachmentPreviewText.Visibility = Visibility.Visible;
                break;
            default:
                ShowGenericAttachmentPreview(attachment.LocalPath);
                break;
        }
        // EasyTerminalControl owns a native child HWND. Native child windows always render
        // above WPF siblings, regardless of Panel.ZIndex, so the preview would otherwise be
        // open but invisible behind the terminal surface (the WPF/Win32 airspace rule).
        terminalVisibilityBeforeAttachmentPreview = Terminal.Visibility;
        Terminal.Visibility = Visibility.Hidden;
        AttachmentPreviewOverlay.Visibility = Visibility.Visible;
    }

    private void ShowGenericAttachmentPreview(string path, string? detail = null)
    {
        var file = new FileInfo(path);
        AttachmentPreviewGenericName.Text = file.Name;
        AttachmentPreviewGenericDetails.Text = string.Join(Environment.NewLine,
            new[] { detail, $"{FormatFileSize(file.Length)} · {file.Extension.TrimStart('.').ToUpperInvariant()} file", file.FullName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        AttachmentPreviewGeneric.Visibility = Visibility.Visible;
    }

    private static string ReadTextPreview(string path)
    {
        const int maximumCharacters = 100_000;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[maximumCharacters];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, count);
            return reader.Peek() >= 0 ? text + $"{Environment.NewLine}{Environment.NewLine}— Preview truncated at {maximumCharacters:N0} characters —" : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return $"Preview unavailable: {exception.Message}";
        }
    }

    private static BitmapImage? LoadAttachmentBitmap(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static bool IsImageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";

    private static AttachmentPreviewKind GetAttachmentPreviewKind(string path)
    {
        if (IsImageFile(path)) return AttachmentPreviewKind.Image;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".webm" or ".mkv" or ".mpeg" or ".mpg" or ".mp3" or ".wav" or ".wma" or ".aac" or ".m4a" => AttachmentPreviewKind.Media,
            ".txt" or ".md" or ".log" or ".json" or ".jsonl" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".env"
                or ".csv" or ".tsv" or ".ps1" or ".psm1" or ".psd1" or ".cs" or ".fs" or ".vb" or ".js" or ".jsx" or ".ts" or ".tsx"
                or ".html" or ".htm" or ".css" or ".scss" or ".py" or ".rb" or ".rs" or ".go" or ".java" or ".kt" or ".sh" or ".bash"
                or ".bat" or ".cmd" or ".sql" or ".gitignore" => AttachmentPreviewKind.Text,
            _ => AttachmentPreviewKind.Generic
        };
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1) { value /= 1024; suffix++; }
        return $"{value:0.##} {suffixes[suffix]}";
    }

    private void AttachmentPreviewBackdropClick(object sender, MouseButtonEventArgs e) => CloseAttachmentPreview();
    private void AttachmentPreviewCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void CloseAttachmentPreviewClick(object sender, RoutedEventArgs e) { CloseAttachmentPreview(); e.Handled = true; }
    private void AttachmentPreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        AttachmentPreviewMediaStatus.Text = "Playing";
        AttachmentPreviewMedia.Play();
    }
    private void AttachmentPreviewMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var path = AttachmentPreviewMedia.Source?.LocalPath;
        AttachmentPreviewMedia.Close();
        AttachmentPreviewMediaPanel.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ShowGenericAttachmentPreview(path, $"Media preview unavailable: {e.ErrorException?.Message}");
    }
    private void PlayAttachmentPreviewClick(object sender, RoutedEventArgs e) { AttachmentPreviewMedia.Play(); AttachmentPreviewMediaStatus.Text = "Playing"; }
    private void PauseAttachmentPreviewClick(object sender, RoutedEventArgs e) { AttachmentPreviewMedia.Pause(); AttachmentPreviewMediaStatus.Text = "Paused"; }
    private void CloseAttachmentPreview()
    {
        var wasOpen = AttachmentPreviewOverlay.Visibility == Visibility.Visible;
        AttachmentPreviewOverlay.Visibility = Visibility.Collapsed;
        AttachmentPreviewImage.Source = null;
        AttachmentPreviewImage.Visibility = Visibility.Collapsed;
        AttachmentPreviewMedia.Close();
        AttachmentPreviewMedia.Source = null;
        AttachmentPreviewMediaPanel.Visibility = Visibility.Collapsed;
        AttachmentPreviewText.Clear();
        AttachmentPreviewText.Visibility = Visibility.Collapsed;
        AttachmentPreviewGeneric.Visibility = Visibility.Collapsed;
        if (wasOpen) Terminal.Visibility = terminalVisibilityBeforeAttachmentPreview;
    }
    private void CommandInputPreviewKeyUp(object sender, KeyEventArgs e) => Dispatcher.BeginInvoke(RefreshSendButtonVisual, System.Windows.Threading.DispatcherPriority.Input);
    private void RunCommandMouseEnter(object sender, MouseEventArgs e) => RefreshSendButtonVisual();
    private void PaneHeaderDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source
            || IsWithin(source, PaneHeaderActions)) return;
        paneHeaderDragStart = e.GetPosition(PaneHeader);
        PaneHeader.CaptureMouse();
    }
    private void PaneHeaderDragEnd(object sender, MouseButtonEventArgs e)
    {
        paneHeaderDragStart = null;
        if (ReferenceEquals(Mouse.Captured, PaneHeader)) Mouse.Capture(null);
    }
    private void PaneHeaderDragLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Released) return;
        paneHeaderDragStart = null;
        if (ReferenceEquals(Mouse.Captured, PaneHeader)) Mouse.Capture(null);
    }
    private void PaneHeaderDragMove(object sender, MouseEventArgs e)
    {
        if (paneHeaderDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(PaneHeader);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        paneHeaderDragStart = null;
        if (ReferenceEquals(Mouse.Captured, PaneHeader)) Mouse.Capture(null);
        DragRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
    private void ToggleCommandBarClick(object sender, RoutedEventArgs e) { SetCommandBarExpanded(!Profile.CommandBarExpanded, true, true); e.Handled = true; }
    private void PreviousOutputClick(object sender, RoutedEventArgs e) => ConfigureRecoveryView(true);
    private void CloseRecoveryClick(object sender, RoutedEventArgs e) { SetRecoverySurfaceVisible(false); Terminal.Focus(); }
    private void ClearClick(object sender, RoutedEventArgs e) { Terminal.ConPTYTerm?.ClearUITerminal(); Terminal.Focus(); }
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private async void RestartClick(object sender, RoutedEventArgs e) => await RestartAsync();
    private void DetachClick(object sender, RoutedEventArgs e) { DetachRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    private void EditClick(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);
    private void CloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
