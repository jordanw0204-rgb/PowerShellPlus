using System.Globalization;

namespace PowerShellPlus.Native;

internal readonly record struct RemoteTmuxScrollbackState(
    bool Succeeded,
    int HistorySize,
    int PaneHeight,
    int ScrollPosition,
    bool IsCopyMode)
{
    public int ViewTop => Math.Max(0, HistorySize - ScrollPosition);
    public int BufferSize => HistorySize + Math.Max(1, PaneHeight);
}

/// <summary>
/// Bridges the application scrollbar to tmux's own history. A tmux client uses
/// the terminal alternate buffer, which intentionally has no host scrollback;
/// history_size and copy mode are therefore the authoritative range and view.
/// </summary>
internal static class RemoteTmuxScrollback
{
    private const string Marker = "PSP_TMUX_SCROLL:";

    public static async Task<RemoteTmuxScrollbackState> ProbeAsync(
        SessionRecoveryEntry recovery,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTarget(recovery, out var sessionName)) return default;
        var result = await RemoteTmuxSession.RunSshCommandAsync(
            recovery, BuildProbeCommand(sessionName), cancellationToken);
        return result.Started && result.ExitCode == 0 && TryParse(result.Output, out var state) ? state : default;
    }

    public static async Task<bool> ScrollAsync(
        SessionRecoveryEntry recovery,
        int scrollPosition,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTarget(recovery, out var sessionName)) return false;
        var result = await RemoteTmuxSession.RunSshCommandAsync(
            recovery, BuildScrollCommand(sessionName, scrollPosition), cancellationToken);
        return result.Started && result.ExitCode == 0;
    }

    internal static bool TryParse(string? output, out RemoteTmuxScrollbackState state)
    {
        state = default;
        var line = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value => value.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null) return false;
        var fields = line[Marker.Length..].Split('|');
        if (fields.Length != 5
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var historySize)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var paneHeight)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var paneInMode)
            || historySize < 0 || paneHeight < 1 || paneInMode < 0) return false;
        var isCopyMode = paneInMode > 0 && fields[4].StartsWith("copy-mode", StringComparison.Ordinal);
        var scrollPosition = 0;
        if (isCopyMode && (!int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out scrollPosition)
            || scrollPosition < 0)) return false;
        state = new RemoteTmuxScrollbackState(true, historySize, paneHeight,
            Math.Clamp(scrollPosition, 0, historySize), isCopyMode);
        return true;
    }

    internal static (int ViewTop, int ViewHeight, int BufferSize) ToViewport(RemoteTmuxScrollbackState state)
        => (state.ViewTop, Math.Max(1, state.PaneHeight), state.BufferSize);

    internal static string BuildProbeCommand(string sessionName)
    {
        if (!RemoteTmuxSession.IsSafeSessionName(sessionName)) throw new ArgumentException("Unsafe tmux session name.", nameof(sessionName));
        var target = QuotePosix(sessionName);
        return $"tmux display-message -p -t {target} '{Marker}#{{history_size}}|#{{pane_height}}|#{{pane_in_mode}}|#{{scroll_position}}|#{{pane_mode}}'";
    }

    internal static string BuildScrollCommand(string sessionName, int scrollPosition)
    {
        if (!RemoteTmuxSession.IsSafeSessionName(sessionName)) throw new ArgumentException("Unsafe tmux session name.", nameof(sessionName));
        if (scrollPosition < 0) throw new ArgumentOutOfRangeException(nameof(scrollPosition));
        var target = QuotePosix(sessionName);
        if (scrollPosition == 0)
            return $"tmux send-keys -X -t {target} cancel >/dev/null 2>&1 || true";
        return $"tmux send-keys -X -t {target} cancel >/dev/null 2>&1 || true; "
            + $"tmux copy-mode -t {target} \\; send-keys -X -t {target} history-bottom \\; "
            + $"send-keys -X -N {scrollPosition.ToString(CultureInfo.InvariantCulture)} -t {target} scroll-up";
    }

    internal static bool ContractPassesForTest()
    {
        const string name = "powershellplus-1234567890abcdef";
        var parsed = TryParse($"noise\n{Marker}1081|22|0||\n", out var bottom)
            && bottom.Succeeded && bottom.HistorySize == 1081 && bottom.ViewTop == 1081 && bottom.BufferSize == 1103
            && TryParse($"{Marker}1081|22|1|120|copy-mode\n", out var scrolled)
            && scrolled.IsCopyMode && scrolled.ScrollPosition == 120 && scrolled.ViewTop == 961;
        var probe = BuildProbeCommand(name);
        var scroll = BuildScrollCommand(name, 120);
        var bottomCommand = BuildScrollCommand(name, 0);
        return parsed && probe.Contains("#{history_size}", StringComparison.Ordinal)
            && probe.Contains("#{scroll_position}", StringComparison.Ordinal)
            && scroll.Contains("copy-mode", StringComparison.Ordinal)
            && scroll.Contains("scroll-up", StringComparison.Ordinal)
            && scroll.Contains("-N 120", StringComparison.Ordinal)
            && bottomCommand.Contains("cancel", StringComparison.Ordinal)
            && !scroll.Contains("\n", StringComparison.Ordinal);
    }

    private static bool TryResolveTarget(SessionRecoveryEntry recovery, out string sessionName)
    {
        sessionName = RemoteTmuxSession.IsSafeSessionName(recovery.RemoteTmuxSessionName)
            ? recovery.RemoteTmuxSessionName!
            : RemoteTmuxSession.GetSessionName(recovery.SessionId);
        return recovery.SshWasActive && recovery.RemoteTmuxManaged
            && RemoteTmuxSession.IsSafeSessionName(sessionName)
            && SshRecovery.TryNormalizeConnectionArguments(recovery.SshConnectionArguments, out _, out _);
    }

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
