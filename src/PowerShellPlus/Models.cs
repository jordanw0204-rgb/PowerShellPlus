using System;
using System.Collections.Generic;

namespace PowerShellPlus
{
    [Serializable]
    public sealed class WorkspaceState
    {
        public int Version { get; set; }
        public string Name { get; set; }
        public string Layout { get; set; }
        public bool AlwaysOnTop { get; set; }
        public List<SessionProfile> Sessions { get; set; }
        public List<CommandSnippet> Snippets { get; set; }
        public List<AutomationRule> Automations { get; set; }

        public WorkspaceState()
        {
            Version = 1;
            Name = "My Workspace";
            Layout = "Grid";
            Sessions = new List<SessionProfile>();
            Snippets = new List<CommandSnippet>();
            Automations = new List<AutomationRule>();
        }

        public static WorkspaceState CreateDefault()
        {
            WorkspaceState state = new WorkspaceState();
            state.Sessions.Add(new SessionProfile { Name = "PowerShell 1", WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            state.Snippets.Add(new CommandSnippet { Name = "Processes", Category = "System", Command = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15" });
            state.Snippets.Add(new CommandSnippet { Name = "Git status", Category = "Development", Command = "git status --short --branch" });
            state.Snippets.Add(new CommandSnippet { Name = "Current location", Category = "Navigation", Command = "Get-Location; Get-ChildItem" });
            return state;
        }

        public void Normalize()
        {
            if (String.IsNullOrWhiteSpace(Name)) Name = "My Workspace";
            if (String.IsNullOrWhiteSpace(Layout)) Layout = "Grid";
            if (Sessions == null) Sessions = new List<SessionProfile>();
            if (Snippets == null) Snippets = new List<CommandSnippet>();
            if (Automations == null) Automations = new List<AutomationRule>();
            foreach (SessionProfile session in Sessions) session.Normalize();
            foreach (CommandSnippet snippet in Snippets) snippet.Normalize();
            foreach (AutomationRule rule in Automations) rule.Normalize();
        }
    }

    [Serializable]
    public sealed class SessionProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ShellPath { get; set; }
        public string WorkingDirectory { get; set; }
        public string StartupCommand { get; set; }
        public string AccentColor { get; set; }
        public bool AutoStart { get; set; }

        public SessionProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "PowerShell";
            ShellPath = ShellLocator.FindBestShell();
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            StartupCommand = String.Empty;
            AccentColor = "#4FC3F7";
            AutoStart = true;
        }

        public void Normalize()
        {
            if (String.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
            if (String.IsNullOrWhiteSpace(Name)) Name = "PowerShell";
            if (String.IsNullOrWhiteSpace(ShellPath)) ShellPath = ShellLocator.FindBestShell();
            if (String.IsNullOrWhiteSpace(WorkingDirectory) || !System.IO.Directory.Exists(WorkingDirectory))
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (StartupCommand == null) StartupCommand = String.Empty;
            if (String.IsNullOrWhiteSpace(AccentColor)) AccentColor = "#4FC3F7";
        }

        public override string ToString() { return Name; }
    }

    [Serializable]
    public sealed class CommandSnippet
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Command { get; set; }

        public CommandSnippet()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New command";
            Category = "General";
            Command = String.Empty;
        }

        public void Normalize()
        {
            if (String.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
            if (String.IsNullOrWhiteSpace(Name)) Name = "Command";
            if (String.IsNullOrWhiteSpace(Category)) Category = "General";
            if (Command == null) Command = String.Empty;
        }

        public override string ToString() { return Name + "  ·  " + Category; }
    }

    [Serializable]
    public sealed class AutomationRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public string TargetSessionId { get; set; }
        public string ScheduleType { get; set; }
        public int IntervalMinutes { get; set; }
        public string DailyTime { get; set; }
        public bool Enabled { get; set; }
        public DateTime LastRunUtc { get; set; }

        public AutomationRule()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New automation";
            Command = String.Empty;
            TargetSessionId = "*";
            ScheduleType = "Interval";
            IntervalMinutes = 60;
            DailyTime = "09:00";
            Enabled = true;
            LastRunUtc = DateTime.MinValue;
        }

        public void Normalize()
        {
            if (String.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
            if (String.IsNullOrWhiteSpace(Name)) Name = "Automation";
            if (Command == null) Command = String.Empty;
            if (String.IsNullOrWhiteSpace(TargetSessionId)) TargetSessionId = "*";
            if (ScheduleType != "Daily") ScheduleType = "Interval";
            if (IntervalMinutes < 1) IntervalMinutes = 1;
            TimeSpan ignored;
            if (!TimeSpan.TryParse(DailyTime, out ignored)) DailyTime = "09:00";
        }

        public bool IsDue(DateTime utcNow, DateTime localNow)
        {
            if (!Enabled || String.IsNullOrWhiteSpace(Command)) return false;
            if (ScheduleType == "Daily")
            {
                TimeSpan time;
                if (!TimeSpan.TryParse(DailyTime, out time)) return false;
                DateTime scheduled = localNow.Date.Add(time);
                DateTime lastLocal = LastRunUtc == DateTime.MinValue ? DateTime.MinValue : LastRunUtc.ToLocalTime();
                return localNow >= scheduled && lastLocal.Date < localNow.Date;
            }

            if (LastRunUtc == DateTime.MinValue) return true;
            return utcNow - LastRunUtc >= TimeSpan.FromMinutes(IntervalMinutes);
        }

        public string ScheduleSummary()
        {
            return ScheduleType == "Daily" ? "Daily at " + DailyTime : "Every " + IntervalMinutes + " min";
        }

        public override string ToString() { return (Enabled ? "● " : "○ ") + Name + "  ·  " + ScheduleSummary(); }
    }
}
