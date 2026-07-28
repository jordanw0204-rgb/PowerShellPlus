using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace PowerShellPlus.Native;

public sealed record AccentChoice(string Name, string Value, Brush Brush);

public static class WorkspaceAccentPalette
{
    public const string DefaultTerminal = "#89B4FA";
    public const string DefaultSession = "#B4BEFE";
    public static IReadOnlyList<AccentChoice> Choices { get; } =
    [
        Choice("Sky", "#89B4FA"), Choice("Lavender", "#B4BEFE"), Choice("Teal", "#94E2D5"),
        Choice("Green", "#A6E3A1"), Choice("Yellow", "#F9E2AF"), Choice("Peach", "#FAB387"),
        Choice("Pink", "#F5C2E7"), Choice("Mauve", "#CBA6F7"), Choice("Red", "#F38BA8")
    ];

    private static readonly object BrushCacheSync = new();
    private static readonly Dictionary<string, Brush> OpaqueBrushes = Choices.ToDictionary(value => value.Value, value => value.Brush, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Value, byte Alpha), Brush> TintBrushes = Choices
        .SelectMany(value => new byte[] { 30, 54, 78 }.Select(alpha => new KeyValuePair<(string, byte), Brush>((value.Value, alpha), CreateBrush(value.Value, alpha))))
        .ToDictionary(value => value.Key, value => value.Value);

    public static string Normalize(string? value, string fallback)
    {
        var candidate = value?.Trim().ToUpperInvariant();
        return candidate is { Length: 7 } && candidate[0] == '#' && candidate.Skip(1).All(Uri.IsHexDigit)
            ? candidate
            : fallback;
    }

    public static Brush BrushFor(string? value, string fallback)
    {
        var normalized = Normalize(value, fallback);
        lock (BrushCacheSync)
        {
            if (OpaqueBrushes.TryGetValue(normalized, out var brush)) return brush;
            return OpaqueBrushes[normalized] = CreateBrush(normalized, 255);
        }
    }

    public static Brush TintFor(string? value, string fallback, byte alpha = 30)
    {
        var normalized = Normalize(value, fallback);
        lock (BrushCacheSync)
        {
            var key = (normalized, alpha);
            if (TintBrushes.TryGetValue(key, out var brush)) return brush;
            return TintBrushes[key] = CreateBrush(normalized, alpha);
        }
    }

    private static AccentChoice Choice(string name, string value) => new(name, value, CreateBrush(value, 255));

    private static Brush CreateBrush(string value, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class WorkspaceState
{
    public int Version { get; set; } = 8;
    public string Name { get; set; } = "Main workspace";
    // Layout and ActiveSessionId are retained as version-6 compatibility
    // fields. Version 7+ stores those values per workspace session instead.
    public string Layout { get; set; } = "Grid";
    public bool WorkspaceSidebarExpanded { get; set; } = true;
    public string? ActiveSessionId { get; set; }
    public ObservableCollection<SessionProfile> Sessions { get; set; } = [];
    public ObservableCollection<TerminalSession> TerminalSessions { get; set; } = [];
    public string? ActiveTerminalSessionId { get; set; }
    public ObservableCollection<CommandSnippet> Snippets { get; set; } = [];
    public ObservableCollection<AutomationRule> Automations { get; set; } = [];
    public WorkspaceSettings Settings { get; set; } = new();
    public Dictionary<string, PaneLayoutSizing> LayoutSizes { get; set; } = [];
}

/// <summary>
/// A user-facing Session. Terminals remain independent live ConPTY processes;
/// switching sessions only changes which terminals and saved layout are shown.
/// </summary>
public sealed class TerminalSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Session";
    public string AccentColor { get; set; } = WorkspaceAccentPalette.DefaultSession;
    public string Layout { get; set; } = "Grid";
    public List<string> TerminalIds { get; set; } = [];
    public string? ActiveTerminalId { get; set; }
    public Dictionary<string, PaneLayoutSizing> LayoutSizes { get; set; } = [];
    [JsonIgnore] public string Subtitle => $"{TerminalIds.Count} terminal{(TerminalIds.Count == 1 ? string.Empty : "s")}";
    [JsonIgnore] public Brush AccentBrush => WorkspaceAccentPalette.BrushFor(AccentColor, WorkspaceAccentPalette.DefaultSession);
    [JsonIgnore] public Brush AccentTintBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultSession);
    [JsonIgnore] public Brush AccentHoverBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultSession, 54);
    [JsonIgnore] public Brush AccentSelectedBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultSession, 78);
}

public sealed class PaneLayoutSizing
{
    public List<double> Rows { get; set; } = [];
    public List<double> Columns { get; set; } = [];
}

public sealed class WorkspaceSettings
{
    // Null/empty string and null int mean "inherit from the Windows Terminal profile".
    public string? FontFace { get; set; }
    public int? FontSize { get; set; }
    public string CursorStyle { get; set; } = "Bar";
    public bool CursorBlink { get; set; } = true;
    public string? DefaultCommandLine { get; set; }
    public string? DefaultWorkingDirectory { get; set; }
    public bool ConfirmBeforeRemove { get; set; } = true;
    public bool KeepSessionsRunningInTray { get; set; } = true;
    public bool RestoreSessionsAfterRestart { get; set; } = true;
    public bool SaveTerminalTranscripts { get; set; } = true;
    public bool SendToAllModifierEnabled { get; set; } = true;
    public string SendToAllModifier { get; set; } = "Shift";
}

public sealed class SessionProfile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "PowerShell";
    public string AccentColor { get; set; } = WorkspaceAccentPalette.DefaultTerminal;
    public string CommandLine { get; set; } = "powershell.exe";
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool AutoStart { get; set; } = true;
    public bool UseRemoteTmux { get; set; } = true;
    public bool CommandBarExpanded { get; set; } = true;
    public int? TerminalFontSize { get; set; }
    public int? CommandFontSize { get; set; }
    public string CommandDraft { get; set; } = string.Empty;
    public List<ComposerAttachmentState> ComposerAttachments { get; set; } = [];
    public List<string> PendingCommands { get; set; } = [];
    public List<string> CommandHistory { get; set; } = [];
    public List<DateTime> CommandHistoryTimestampsUtc { get; set; } = [];
    public List<TerminalAutomationBinding> AutomationBindings { get; set; } = [];
    public string LiveWorkingDirectory { get; set; } = string.Empty;
    public bool LiveWorkingDirectoryIsSsh { get; set; }
    [JsonIgnore] public bool IsRemoteDetached { get; private set; }
    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(LiveWorkingDirectory) ? WorkingDirectory : LiveWorkingDirectory;
    [JsonIgnore] public string DirectoryPrefix => IsRemoteDetached ? "SSH · detached · " : LiveWorkingDirectoryIsSsh ? "SSH · " : string.Empty;
    [JsonIgnore] public string AgentStatusState { get; private set; } = "starting";
    [JsonIgnore] public string AgentStatusText { get; private set; } = "Terminal is starting";
    [JsonIgnore] public Brush AgentStatusBrush => AgentStatusState switch
    {
        "working" => new SolidColorBrush(Color.FromRgb(137, 180, 250)),
        "waiting" => new SolidColorBrush(Color.FromRgb(249, 226, 175)),
        _ => new SolidColorBrush(Color.FromRgb(166, 227, 161))
    };
    [JsonIgnore] public Brush AccentBrush => WorkspaceAccentPalette.BrushFor(AccentColor, WorkspaceAccentPalette.DefaultTerminal);
    [JsonIgnore] public Brush AccentTintBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultTerminal);
    [JsonIgnore] public Brush AccentHoverBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultTerminal, 54);
    [JsonIgnore] public Brush AccentSelectedBrush => WorkspaceAccentPalette.TintFor(AccentColor, WorkspaceAccentPalette.DefaultTerminal, 78);

    public void NotifyDirectoryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectoryPrefix)));
    }

    public void UpdateAgentStatus(string state, string accessibleText)
    {
        if (AgentStatusState == state && AgentStatusText == accessibleText) return;
        AgentStatusState = state;
        AgentStatusText = accessibleText;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentStatusState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentStatusBrush)));
    }

    public void SetRemoteDetached(bool detached)
    {
        if (IsRemoteDetached == detached) return;
        IsRemoteDetached = detached;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRemoteDetached)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectoryPrefix)));
        if (detached) UpdateAgentStatus("stopped", "SSH terminal detached; remote session is still running");
    }
}

public sealed class TerminalAutomationBinding
{
    public string AutomationId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AutoInsertAtEnd { get; set; }
}

public sealed class ComposerAttachmentState
{
    public string LocalPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public bool IsTemporary { get; set; }
}

public sealed class CommandSnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Command";
    public string Category { get; set; } = "General";
    public string Command { get; set; } = string.Empty;
    public bool ShowInQuickAccess { get; set; }
    [JsonIgnore] public string Subtitle => $"{Category} · {Command}";
}

public sealed class AutomationRule : INotifyPropertyChanged
{
    public const string NoTarget = "none";
    public const string NoSchedule = "None";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Automation";
    public string Command { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = NoTarget;
    public string ScheduleType { get; set; } = NoSchedule;
    public int IntervalMinutes { get; set; } = 60;
    public string DailyTime { get; set; } = "09:00";
    public string ScheduledDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool Enabled { get; set; } = true;
    public bool ClearLine { get; set; }
    public bool HasRun { get; set; }
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public string Subtitle => ScheduleType switch
    {
        NoSchedule => "No schedule",
        "Daily" => $"Daily at {DisplayTime(DailyTime)}",
        "Once" => $"{DisplayDate(ScheduledDate)} at {DisplayTime(DailyTime)}",
        _ => $"Every {IntervalMinutes} min"
    };
    [JsonIgnore] public string Countdown => GetCountdownText(DateTime.UtcNow, DateTime.Now);

    public void NotifyCountdownChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Countdown)));

    public void NotifyDisplayChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        NotifyCountdownChanged();
    }

    public bool IsDue(DateTime utcNow, DateTime localNow)
    {
        if (!Enabled || ScheduleType == NoSchedule || TargetSessionId == NoTarget || string.IsNullOrWhiteSpace(Command)) return false;
        if (ScheduleType == "Interval") return utcNow - LastRunUtc >= TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
        if (!TimeSpan.TryParseExact(DailyTime, @"hh\:mm", CultureInfo.InvariantCulture, out var time)) return false;
        if (ScheduleType == "Daily") return localNow >= localNow.Date.Add(time) && LastRunUtc.ToLocalTime().Date < localNow.Date;
        if (ScheduleType != "Once" || HasRun || !DateTime.TryParseExact(ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        return localNow >= date.Date.Add(time);
    }

    public DateTime? GetNextRunLocal(DateTime utcNow, DateTime localNow)
    {
        if (!Enabled || ScheduleType == NoSchedule || TargetSessionId == NoTarget || string.IsNullOrWhiteSpace(Command)) return null;
        if (ScheduleType == "Interval") return LastRunUtc.AddMinutes(Math.Max(1, IntervalMinutes)).ToLocalTime();
        if (!TimeSpan.TryParseExact(DailyTime, @"hh\:mm", CultureInfo.InvariantCulture, out var time)) return null;
        if (ScheduleType == "Daily")
        {
            var today = localNow.Date.Add(time);
            return LastRunUtc.ToLocalTime().Date < localNow.Date ? today : today.AddDays(1);
        }
        if (ScheduleType != "Once" || HasRun || !DateTime.TryParseExact(ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        return date.Date.Add(time);
    }

    public string GetCountdownText(DateTime utcNow, DateTime localNow)
    {
        if (ScheduleType == "Once" && HasRun) return "Completed";
        if (!Enabled) return "Paused";
        if (ScheduleType == NoSchedule) return "Manual only";
        if (TargetSessionId == NoTarget) return "Manual only";
        var next = GetNextRunLocal(utcNow, localNow);
        if (next is null) return "No schedule";
        var remaining = next.Value - localNow;
        return remaining <= TimeSpan.Zero ? "Due now" : $"in {FormatCountdown(remaining)}";
    }

    public static string FormatCountdown(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        var days = totalSeconds / 86400; var hours = totalSeconds % 86400 / 3600; var minutes = totalSeconds % 3600 / 60; var seconds = totalSeconds % 60;
        if (days > 0) return $"{days}d {hours}h";
        if (hours > 0) return $"{hours}h {minutes}m {seconds}s";
        if (minutes > 0) return $"{minutes}m {seconds}s";
        return $"{seconds}s";
    }

    private static string DisplayTime(string value) => DateTime.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time.ToString("h:mm tt", CultureInfo.InvariantCulture)
        : value;

    private static string DisplayDate(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
        : value;
}
