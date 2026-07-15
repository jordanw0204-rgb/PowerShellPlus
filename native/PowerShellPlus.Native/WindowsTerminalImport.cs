using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public sealed record WindowsTerminalTabCapture(int Index, string Title, string Transcript, string? WorkingDirectory, bool LooksLikeCodex);

public sealed record WindowsTerminalWindowCapture(IntPtr WindowHandle, string WindowTitle, IReadOnlyList<WindowsTerminalTabCapture> Tabs);

public sealed class CodexImportChoice
{
    public CodexImportChoice(CodexSessionMatch? session)
    {
        Session = session;
        DisplayName = session is null
            ? "Open as PowerShell (do not resume Codex)"
            : $"Resume {ShortId(session.SessionId)}  |  {session.WorkingDirectory}  |  {session.Model ?? "default model"}  |  {PermissionsText(session)}";
    }

    public CodexSessionMatch? Session { get; }
    public string DisplayName { get; }

    private static string ShortId(string value) => value.Length <= 12 ? value : value[..8] + "…";
    private static string PermissionsText(CodexSessionMatch value) => CodexSessionLocator.IsSafeCodexPermissionState(value.PermissionProfile, value.SandboxMode, value.ApprovalPolicy)
        ? $"{value.PermissionProfile ?? value.SandboxMode} / {value.ApprovalPolicy} / {value.ApprovalsReviewer ?? "reviewer unknown"}"
        : "permissions unavailable";
}

public sealed class WindowsTerminalImportRow : INotifyPropertyChanged
{
    private CodexImportChoice? selectedChoice;

    public required WindowsTerminalTabCapture Tab { get; init; }
    public required ObservableCollection<CodexImportChoice> Choices { get; init; }
    public string Title => Tab.Title;
    public string Details => $"{Tab.WorkingDirectory ?? "Working directory unavailable"}  •  {Math.Max(0, Tab.Transcript.Length):N0} transcript characters";
    public string CodexStatus => Tab.LooksLikeCodex
        ? "Codex detected — select the exact thread and permission level"
        : "PowerShell tab";

    public CodexImportChoice? SelectedChoice
    {
        get => selectedChoice;
        set
        {
            if (ReferenceEquals(selectedChoice, value)) return;
            selectedChoice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WindowsTerminalImportPlan
{
    public required WindowsTerminalWindowCapture Source { get; init; }
    public required ObservableCollection<WindowsTerminalImportRow> Rows { get; init; }
}

public static class WindowsTerminalImportPlanner
{
    private static readonly Regex CodexDirectoryPattern = new(@"(?im)\bdirectory:\s*(?<path>[A-Za-z]:\\[^\r\n│]+?)\s*(?:│|$)", RegexOptions.Compiled);
    private static readonly Regex ClassicPowerShellPromptPattern = new(@"(?im)\bPS\s+(?<path>[A-Za-z]:\\[^>\r\n]*?)>", RegexOptions.Compiled);
    private static readonly Regex PowerlinePromptPattern = new(@"(?im)(?<path>[A-Za-z]:\\[^\r\n]*?)\s+[>]", RegexOptions.Compiled);
    private static readonly Regex CodexSpinnerPrefixPattern = new(@"^[\u2800-\u28ff]\s+", RegexOptions.Compiled);

    public static WindowsTerminalImportPlan Create(WindowsTerminalWindowCapture capture, IEnumerable<CodexSessionMatch> sessions)
    {
        var candidates = sessions
            .Where(value => CodexSessionLocator.IsSafeCodexId(value.SessionId))
            .GroupBy(value => value.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.FileModifiedUtc).First())
            .OrderByDescending(value => value.FileModifiedUtc)
            .ToList();
        var choices = new ObservableCollection<CodexImportChoice>(new[] { new CodexImportChoice(null) }
            .Concat(candidates.Select(value => new CodexImportChoice(value))));
        var rows = new ObservableCollection<WindowsTerminalImportRow>(capture.Tabs.Select(tab => new WindowsTerminalImportRow
        {
            Tab = tab,
            Choices = choices,
            SelectedChoice = choices[0]
        }));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(value => value.Tab.LooksLikeCodex && !string.IsNullOrWhiteSpace(value.Tab.WorkingDirectory)))
        {
            var matchingRows = rows.Count(value => value.Tab.LooksLikeCodex
                && PathsEqual(value.Tab.WorkingDirectory, row.Tab.WorkingDirectory));
            var matchingSessions = candidates.Where(value => PathsEqual(value.WorkingDirectory, row.Tab.WorkingDirectory)).ToList();
            if (matchingRows != 1 || matchingSessions.Count != 1 || used.Contains(matchingSessions[0].SessionId)) continue;
            row.SelectedChoice = choices.First(value => value.Session?.SessionId == matchingSessions[0].SessionId);
            used.Add(matchingSessions[0].SessionId);
        }

        var descendantRows = rows.Where(value => value.Tab.LooksLikeCodex && value.SelectedChoice?.Session is null
            && !string.IsNullOrWhiteSpace(value.Tab.WorkingDirectory)).ToList();
        foreach (var row in descendantRows)
        {
            var ranked = candidates.Where(value => !used.Contains(value.SessionId) && IsAncestorPath(value.WorkingDirectory, row.Tab.WorkingDirectory))
                .Select(value => (Session: value, Length: NormalizedPathLength(value.WorkingDirectory)))
                .OrderByDescending(value => value.Length).ToList();
            if (ranked.Count == 0 || ranked.Count(value => value.Length == ranked[0].Length) != 1) continue;
            var candidate = ranked[0].Session;
            if (descendantRows.Count(value => value.SelectedChoice?.Session is null
                && IsAncestorPath(candidate.WorkingDirectory, value.Tab.WorkingDirectory)) != 1) continue;
            row.SelectedChoice = choices.First(value => value.Session?.SessionId == candidate.SessionId);
            used.Add(candidate.SessionId);
        }

        var unresolved = rows.Where(value => value.Tab.LooksLikeCodex && value.SelectedChoice?.Session is null).ToList();
        var unused = candidates.Where(value => !used.Contains(value.SessionId)).ToList();
        if (unresolved.Count == 1 && unused.Count == 1)
            unresolved[0].SelectedChoice = choices.First(value => value.Session?.SessionId == unused[0].SessionId);

        return new WindowsTerminalImportPlan { Source = capture, Rows = rows };
    }

    public static WindowsTerminalTabCapture CreateTabCapture(int index, string? rawTitle, string? transcript)
    {
        var output = NormalizeTranscript(transcript ?? string.Empty);
        var looksLikeCodex = output.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(rawTitle) ? $"Windows Terminal {index + 1}" : rawTitle.Trim();
        if (looksLikeCodex) title = CodexSpinnerPrefixPattern.Replace(title, string.Empty).Trim();
        if (title.Length == 0) title = $"Windows Terminal {index + 1}";
        return new WindowsTerminalTabCapture(index, title, output, FindWorkingDirectory(output), looksLikeCodex);
    }

    public static SessionRecoveryEntry CreateRecoveryEntry(WindowsTerminalImportRow row, string profileId, string? transcriptFile, CodexSessionMatch? refreshedSession = null)
    {
        var selected = refreshedSession ?? row.SelectedChoice?.Session;
        var hasExactCodex = selected is not null
            && CodexSessionLocator.IsSafeCodexId(selected.SessionId)
            && CodexSessionLocator.IsSafeCodexPermissionState(selected.PermissionProfile, selected.SandboxMode, selected.ApprovalPolicy, selected.ApprovalsReviewer)
            && CodexSessionLocator.IsSafeCodexApprovalsReviewer(selected.ApprovalsReviewer);
        return new SessionRecoveryEntry
        {
            SessionId = profileId,
            WorkingDirectory = selected?.WorkingDirectory ?? row.Tab.WorkingDirectory ?? string.Empty,
            TranscriptFile = transcriptFile,
            CodexWasActive = hasExactCodex,
            CodexSessionId = hasExactCodex ? selected!.SessionId : null,
            CodexModel = hasExactCodex && CodexSessionLocator.IsSafeCodexModel(selected!.Model) ? selected.Model : null,
            CodexSandboxMode = hasExactCodex && CodexSessionLocator.IsSafeCodexSandboxMode(selected!.SandboxMode) ? selected.SandboxMode : null,
            CodexApprovalPolicy = hasExactCodex ? selected!.ApprovalPolicy : null,
            CodexPermissionProfile = hasExactCodex && CodexSessionLocator.IsSafeCodexPermissionProfile(selected!.PermissionProfile) ? selected.PermissionProfile : null,
            CodexApprovalsReviewer = hasExactCodex ? selected!.ApprovalsReviewer : null,
            CapturedUtc = DateTime.UtcNow
        };
    }

    public static string? FindWorkingDirectory(string transcript)
    {
        foreach (var pattern in new[] { CodexDirectoryPattern, ClassicPowerShellPromptPattern, PowerlinePromptPattern })
        {
            var matches = pattern.Matches(transcript);
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var value = matches[index].Groups["path"].Value.Trim().TrimEnd('│').Trim();
                if (LooksLikeWindowsPath(value)) return value;
            }
        }
        return null;
    }

    private static bool LooksLikeWindowsPath(string value)
    {
        if (value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\') return false;
        try { return Path.IsPathFullyQualified(value); }
        catch { return false; }
    }

    private static string NormalizeTranscript(string value)
    {
        if (value.Length == 0) return value;
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var output = string.Join(Environment.NewLine, lines);
        return output.Length <= 500_000 ? output : output[^500_000..];
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(left.Trim().TrimEnd('\\', '/'), right.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    }

    private static bool IsAncestorPath(string? parent, string? child)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child) || PathsEqual(parent, child)) return false;
        try
        {
            var root = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var value = Path.GetFullPath(child).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return value.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int NormalizedPathLength(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd('\\', '/').Length; }
        catch { return value.Trim().TrimEnd('\\', '/').Length; }
    }
}

public static class WindowsTerminalImportService
{
    private const int WmClose = 0x0010;

    public static Task<WindowsTerminalWindowCapture> CaptureAsync(IntPtr windowHandle)
        => Task.Run(() => Capture(windowHandle));

    public static IReadOnlyList<IntPtr> FindWindowsTerminalWindows()
    {
        var handles = new HashSet<IntPtr>();
        foreach (var process in Process.GetProcessesByName("WindowsTerminal"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero) handles.Add(process.MainWindowHandle);
                }
                catch { }
            }
        }
        return handles.ToList();
    }

    public static bool RequestClose(IntPtr windowHandle)
    {
        if (!IsWindow(windowHandle)) return true;
        _ = SetForegroundWindow(windowHandle);
        return PostMessage(windowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    public static async Task<bool> WaitForClosedAsync(IntPtr windowHandle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsWindow(windowHandle)) return true;
            await Task.Delay(120);
        }
        return !IsWindow(windowHandle);
    }

    private static WindowsTerminalWindowCapture Capture(IntPtr windowHandle)
    {
        if (!IsWindow(windowHandle)) throw new InvalidOperationException("The Windows Terminal window is no longer available.");
        var root = AutomationElement.FromHandle(windowHandle) ?? throw new InvalidOperationException("Windows Terminal did not expose its automation tree.");
        var windowTitle = GetWindowTitle(windowHandle);
        var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().ToList();
        if (tabs.Count == 0) throw new InvalidOperationException("No Windows Terminal tabs were exposed through UI Automation.");

        AutomationElement? originallySelected = null;
        foreach (var tab in tabs)
        {
            try
            {
                var selection = (SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern);
                if (selection.Current.IsSelected) { originallySelected = tab; break; }
            }
            catch { }
        }

        var captures = new List<WindowsTerminalTabCapture>();
        try
        {
            for (var index = 0; index < tabs.Count; index++)
            {
                var tab = tabs[index];
                var rawTitle = tab.Current.Name;
                try { ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); }
                catch (Exception exception) { throw new InvalidOperationException($"Could not activate Windows Terminal tab {index + 1}.", exception); }
                Thread.Sleep(180);
                root = AutomationElement.FromHandle(windowHandle);
                var transcript = ReadTerminalText(root);
                captures.Add(WindowsTerminalImportPlanner.CreateTabCapture(index, rawTitle, transcript));
            }
        }
        finally
        {
            if (originallySelected is not null)
            {
                try { ((SelectionItemPattern)originallySelected.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); }
                catch { }
            }
        }
        return new WindowsTerminalWindowCapture(windowHandle, windowTitle, captures);
    }

    private static string ReadTerminalText(AutomationElement root)
    {
        var textElements = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true))
            .Cast<AutomationElement>()
            .Where(value => string.Equals(value.Current.ClassName, "TermControl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Current.HasKeyboardFocus)
            .ToList();
        if (textElements.Count == 0) return string.Empty;
        var panes = new List<string>();
        foreach (var element in textElements)
        {
            try
            {
                var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                var value = pattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(value)) panes.Add(value);
            }
            catch { }
        }
        if (panes.Count <= 1) return panes.FirstOrDefault() ?? string.Empty;
        return string.Join(Environment.NewLine + Environment.NewLine, panes.Select((value, index) => $"--- Windows Terminal pane {index + 1} ---{Environment.NewLine}{value}"));
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0) return "Windows Terminal";
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr windowHandle, StringBuilder value, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr windowHandle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr windowHandle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr windowHandle);
}

public sealed class WindowsTerminalDragMonitor : IDisposable
{
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private readonly IntPtr targetWindow;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer hoverTimer;
    private readonly WinEventDelegate callback;
    private IntPtr hook;
    private IntPtr movingWindow;
    private DateTime? hoverStartedUtc;
    private bool isOverTarget;
    private bool isArmed;

    public WindowsTerminalDragMonitor(IntPtr targetWindow, Dispatcher dispatcher)
    {
        this.targetWindow = targetWindow;
        this.dispatcher = dispatcher;
        callback = WinEventCallback;
        hoverTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        hoverTimer.Tick += (_, _) => PollHover();
        hook = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, IntPtr.Zero, callback, 0, 0, WineventOutofcontext | WineventSkipownprocess);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not monitor external window drags.");
    }

    public event Action<IntPtr, bool, bool>? HoverChanged;
    public event Action<IntPtr>? Dropped;

    public void Dispose()
    {
        hoverTimer.Stop();
        if (hook != IntPtr.Zero) { _ = UnhookWinEvent(hook); hook = IntPtr.Zero; }
    }

    private void WinEventCallback(IntPtr eventHook, uint eventType, IntPtr windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (windowHandle == IntPtr.Zero || windowHandle == targetWindow) return;
        dispatcher.BeginInvoke(() => HandleWinEvent(eventType, windowHandle));
    }

    private void HandleWinEvent(uint eventType, IntPtr windowHandle)
    {
        if (eventType == EventSystemMoveSizeStart)
        {
            if (!IsWindowsTerminalWindow(windowHandle) || !DragStartedInTitleBar(windowHandle)) return;
            movingWindow = windowHandle;
            hoverStartedUtc = null;
            isOverTarget = false;
            isArmed = false;
            hoverTimer.Start();
            return;
        }
        if (eventType != EventSystemMoveSizeEnd || movingWindow != windowHandle) return;
        PollHover();
        var dropped = isArmed && isOverTarget;
        Reset();
        if (dropped) Dropped?.Invoke(windowHandle);
    }

    private void PollHover()
    {
        if (movingWindow == IntPtr.Zero || !IsWindow(movingWindow) || !IsWindowVisible(targetWindow)
            || !GetCursorPos(out var cursor) || !GetWindowRect(targetWindow, out var targetRect) || !GetWindowRect(movingWindow, out var sourceRect))
        {
            Reset();
            return;
        }
        var cursorInside = cursor.X >= targetRect.Left && cursor.X < targetRect.Right && cursor.Y >= targetRect.Top && cursor.Y < targetRect.Bottom;
        var overlapWidth = Math.Max(0, Math.Min(targetRect.Right, sourceRect.Right) - Math.Max(targetRect.Left, sourceRect.Left));
        var overlapHeight = Math.Max(0, Math.Min(targetRect.Bottom, sourceRect.Bottom) - Math.Max(targetRect.Top, sourceRect.Top));
        var over = cursorInside && overlapWidth >= 120 && overlapHeight >= 80;
        var wasOver = isOverTarget;
        var wasArmed = isArmed;
        if (over)
        {
            hoverStartedUtc ??= DateTime.UtcNow;
            isArmed = DateTime.UtcNow - hoverStartedUtc >= TimeSpan.FromMilliseconds(550);
        }
        else
        {
            hoverStartedUtc = null;
            isArmed = false;
        }
        isOverTarget = over;
        if (wasOver == isOverTarget && wasArmed == isArmed) return;
        HoverChanged?.Invoke(movingWindow, isOverTarget, isArmed);
    }

    private void Reset()
    {
        hoverTimer.Stop();
        if (isOverTarget) HoverChanged?.Invoke(movingWindow, false, false);
        movingWindow = IntPtr.Zero;
        hoverStartedUtc = null;
        isOverTarget = false;
        isArmed = false;
    }

    private static bool IsWindowsTerminalWindow(IntPtr windowHandle)
    {
        try
        {
            _ = GetWindowThreadProcessId(windowHandle, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool DragStartedInTitleBar(IntPtr windowHandle)
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(windowHandle, out var rect)) return false;
        var relativeY = cursor.Y - rect.Top;
        return relativeY >= 6 && relativeY <= Math.Min(90, Math.Max(48, (rect.Bottom - rect.Top) / 5));
    }

    private delegate void WinEventDelegate(IntPtr eventHook, uint eventType, IntPtr windowHandle, int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint eventMinimum, uint eventMaximum, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr eventHook);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr windowHandle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr windowHandle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
