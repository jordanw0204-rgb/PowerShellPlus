using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PowerShellPlus.Native;

public sealed class WorkspaceState
{
    public int Version { get; set; } = 4;
    public string Name { get; set; } = "Main workspace";
    public string Layout { get; set; } = "Grid";
    public string? ActiveSessionId { get; set; }
    public ObservableCollection<SessionProfile> Sessions { get; set; } = [];
    public ObservableCollection<CommandSnippet> Snippets { get; set; } = [];
    public ObservableCollection<AutomationRule> Automations { get; set; } = [];
    public WorkspaceSettings Settings { get; set; } = new();
    public Dictionary<string, PaneLayoutSizing> LayoutSizes { get; set; } = [];
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
}

public sealed class SessionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "PowerShell";
    public string CommandLine { get; set; } = "powershell.exe";
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool AutoStart { get; set; } = true;
    public bool CommandBarExpanded { get; set; } = true;
    public List<string> PendingCommands { get; set; } = [];
    [JsonIgnore] public string Subtitle => WorkingDirectory;
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
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Automation";
    public string Command { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = "*";
    public string ScheduleType { get; set; } = "Interval";
    public int IntervalMinutes { get; set; } = 60;
    public string DailyTime { get; set; } = "09:00";
    public string ScheduledDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool Enabled { get; set; } = true;
    public bool HasRun { get; set; }
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public string Subtitle => ScheduleType switch
    {
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
        if (!Enabled || string.IsNullOrWhiteSpace(Command)) return false;
        if (ScheduleType == "Interval") return utcNow - LastRunUtc >= TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
        if (!TimeSpan.TryParseExact(DailyTime, @"hh\:mm", CultureInfo.InvariantCulture, out var time)) return false;
        if (ScheduleType == "Daily") return localNow >= localNow.Date.Add(time) && LastRunUtc.ToLocalTime().Date < localNow.Date;
        if (ScheduleType != "Once" || HasRun || !DateTime.TryParseExact(ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        return localNow >= date.Date.Add(time);
    }

    public DateTime? GetNextRunLocal(DateTime utcNow, DateTime localNow)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Command)) return null;
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
