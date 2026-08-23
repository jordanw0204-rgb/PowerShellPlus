using System.Text.Json;

namespace PowerShellPlus.Native;

public static class WorkspaceStore
{
    // This is an internal state file, not a user-authored document. Compact JSON
    // substantially reduces serializer CPU, allocations, and disk traffic while
    // a large workspace is being checkpointed in the background.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long latestSaveVersion;
    public static string? DirectoryOverride { get; set; }
    public static string DirectoryPath => DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerShellPlus");
    public static string FilePath => Path.Combine(DirectoryPath, "native-workspace.json");

    internal static string LoadApplicationTheme()
    {
        try
        {
            if (!File.Exists(FilePath)) return AppThemeCatalog.DefaultThemeId;
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!document.RootElement.TryGetProperty(nameof(WorkspaceState.Settings), out var settings)
                || !settings.TryGetProperty(nameof(WorkspaceSettings.ApplicationTheme), out var theme))
                return AppThemeCatalog.DefaultThemeId;
            return AppThemeCatalog.Normalize(theme.GetString());
        }
        catch { return AppThemeCatalog.DefaultThemeId; }
    }

    public static WorkspaceState Load(WindowsTerminalProfile terminalProfile)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded is not null && loaded.Version is >= 3 and <= 8)
            {
                var upgradedFromLegacy = loaded.Version <= 6;
                loaded.Version = 8;
                loaded.Settings ??= new WorkspaceSettings();
                loaded.Settings.ApplicationTheme = AppThemeCatalog.Normalize(loaded.Settings.ApplicationTheme);
                if (loaded.Settings.NotificationSound is not ("System" or "Custom" or "Silent"))
                    loaded.Settings.NotificationSound = "System";
                if (string.IsNullOrWhiteSpace(loaded.Settings.CustomNotificationSoundPath))
                    loaded.Settings.CustomNotificationSoundPath = null;
                    if (string.Equals(loaded.Settings.SendToAllModifier, "Ctrl", StringComparison.OrdinalIgnoreCase)) loaded.Settings.SendToAllModifier = "Shift";
                loaded.LayoutSizes ??= [];
                loaded.Automations ??= [];
                foreach (var automation in loaded.Automations)
                {
                    if (string.IsNullOrWhiteSpace(automation.Id)) automation.Id = Guid.NewGuid().ToString("N");
                    automation.Name = string.IsNullOrWhiteSpace(automation.Name) ? "Automation" : automation.Name;
                    automation.Command ??= string.Empty;
                    automation.TargetSessionId = string.IsNullOrWhiteSpace(automation.TargetSessionId) ? "*" : automation.TargetSessionId;
                    if (automation.ScheduleType is not (AutomationRule.NoSchedule or "Interval" or "Daily" or "Once"))
                        automation.ScheduleType = AutomationRule.NoSchedule;
                }
                var automationIds = loaded.Automations.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var session in loaded.Sessions)
                {
                    session.AccentColor = WorkspaceAccentPalette.Normalize(session.AccentColor, WorkspaceAccentPalette.DefaultTerminal);
                    session.TerminalFontSize = NormalizeFontSize(session.TerminalFontSize, 6, 36);
                    session.CommandFontSize = NormalizeFontSize(session.CommandFontSize, 8, 28);
                    session.CommandDraft ??= string.Empty;
                    session.ComposerAttachments ??= [];
                    session.ComposerAttachments = session.ComposerAttachments
                        .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.LocalPath))
                        .Take(10)
                        .ToList();
                    session.PendingCommands ??= [];
                    session.CommandHistory ??= [];
                    session.CommandHistory = session.CommandHistory
                        .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 32_768)
                        .TakeLast(100)
                        .ToList();
                    session.CommandHistoryTimestampsUtc ??= [];
                    session.CommandHistoryTimestampsUtc = session.CommandHistoryTimestampsUtc.TakeLast(session.CommandHistory.Count).ToList();
                    if (session.CommandHistoryTimestampsUtc.Count < session.CommandHistory.Count)
                    {
                        var legacyCount = session.CommandHistory.Count - session.CommandHistoryTimestampsUtc.Count;
                        session.CommandHistoryTimestampsUtc.InsertRange(0, Enumerable.Repeat(DateTime.MinValue, legacyCount));
                    }
                    session.LiveWorkingDirectory ??= string.Empty;
                    session.AutomationBindings ??= [];
                    session.AutomationBindings = session.AutomationBindings
                        .Where(value => value is not null && automationIds.Contains(value.AutomationId))
                        .GroupBy(value => value.AutomationId, StringComparer.Ordinal)
                        .Select(value => value.First())
                        .ToList();
                }
                loaded.TerminalSessions ??= [];
                if (loaded.TerminalSessions.Count == 0)
                {
                    loaded.TerminalSessions.Add(new TerminalSession
                    {
                        Name = "Session 1",
                        Layout = string.IsNullOrWhiteSpace(loaded.Layout) ? "Grid" : loaded.Layout,
                        TerminalIds = loaded.Sessions.Select(value => value.Id).ToList(),
                        ActiveTerminalId = loaded.ActiveSessionId,
                        LayoutSizes = loaded.LayoutSizes ?? []
                    });
                }
                NormalizeTerminalSessions(loaded);
                if (upgradedFromLegacy) loaded.ActiveTerminalSessionId = loaded.TerminalSessions[0].Id;
                return loaded;
                }
            }
        }
        catch { }

        var state = new WorkspaceState();
        // Keep the first-run bootstrap terminal independent of optional WSL/tmux.
        // User-created terminals still default all persistence choices on in the
        // editor, but a clean Windows install must always reach an interactive
        // shell before those optional dependencies have been configured.
        state.Sessions.Add(new SessionProfile { Name = terminalProfile.ProfileName, CommandLine = terminalProfile.CommandLine, UseRemoteTmux = true, AutoStart = true });
        state.Snippets.Add(new CommandSnippet { Name = "Git status", Category = "Development", Command = "git status --short --branch", ShowInQuickAccess = true });
        state.Snippets.Add(new CommandSnippet { Name = "Top processes", Category = "System", Command = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15" });
        state.ActiveSessionId = state.Sessions[0].Id;
        var firstSession = new TerminalSession
        {
            Name = "Session 1",
            TerminalIds = state.Sessions.Select(value => value.Id).ToList(),
            ActiveTerminalId = state.ActiveSessionId
        };
        state.TerminalSessions.Add(firstSession);
        state.ActiveTerminalSessionId = firstSession.Id;
        return state;
    }

    internal static void NormalizeTerminalSessions(WorkspaceState state)
    {
        state.TerminalSessions ??= [];
        var validTerminalIds = state.Sessions.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in state.TerminalSessions)
        {
            session.AccentColor = WorkspaceAccentPalette.Normalize(session.AccentColor, WorkspaceAccentPalette.DefaultSession);
            session.TerminalIds ??= [];
            session.LayoutSizes ??= [];
            session.Layout = session.Layout is "Grid" or "Rows" or "Columns" or "Focus" or "Tabs" ? session.Layout : "Grid";
            session.TerminalIds = session.TerminalIds
                .Where(value => validTerminalIds.Contains(value) && assigned.Add(value))
                .ToList();
            if (!session.TerminalIds.Contains(session.ActiveTerminalId ?? string.Empty, StringComparer.Ordinal))
                session.ActiveTerminalId = session.TerminalIds.FirstOrDefault();
        }
        if (state.TerminalSessions.Count == 0)
            state.TerminalSessions.Add(new TerminalSession { Name = "Session 1" });
        var fallback = state.TerminalSessions[0];
        foreach (var terminal in state.Sessions.Where(value => !assigned.Contains(value.Id)))
            fallback.TerminalIds.Add(terminal.Id);
        fallback.ActiveTerminalId ??= fallback.TerminalIds.FirstOrDefault();
        if (!state.TerminalSessions.Any(value => value.Id == state.ActiveTerminalSessionId))
            state.ActiveTerminalSessionId = state.TerminalSessions[0].Id;
        var active = state.TerminalSessions.First(value => value.Id == state.ActiveTerminalSessionId);
        state.ActiveSessionId = active.ActiveTerminalId;
    }

    internal static bool VerifyLegacySessionMigrationForTest(WindowsTerminalProfile terminalProfile, string directory)
    {
        var originalDirectory = DirectoryOverride;
        try
        {
            DirectoryOverride = directory;
            Directory.CreateDirectory(directory);
            var first = new SessionProfile { Name = "Legacy one" };
            var second = new SessionProfile { Name = "Legacy two" };
            var legacy = new WorkspaceState
            {
                Version = 6,
                Layout = "Rows",
                ActiveSessionId = second.Id,
                Sessions = [first, second],
                TerminalSessions = []
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(legacy, JsonOptions));
            var migrated = Load(terminalProfile);
            return migrated.Version == 8
                && migrated.TerminalSessions.Count == 1
                && migrated.TerminalSessions[0].Name == "Session 1"
                && migrated.TerminalSessions[0].Layout == "Rows"
                && migrated.TerminalSessions[0].ActiveTerminalId == second.Id
                && migrated.TerminalSessions[0].TerminalIds.SequenceEqual([first.Id, second.Id]);
        }
        finally { DirectoryOverride = originalDirectory; }
    }

    internal static bool VerifyComposerDraftPersistenceForTest(WindowsTerminalProfile terminalProfile, string directory)
    {
        var originalDirectory = DirectoryOverride;
        try
        {
            DirectoryOverride = directory;
            Directory.CreateDirectory(directory);
            var terminal = new SessionProfile
            {
                Name = "Composer persistence",
                TerminalFontSize = 15,
                CommandFontSize = 13,
                CommandDraft = "inspect \"C:\\fixtures\\preview.png\"",
                ComposerAttachments =
                [
                    new ComposerAttachmentState
                    {
                        LocalPath = "C:\\fixtures\\preview.png",
                        DisplayName = "Image 1",
                        IsImage = true,
                        IsTemporary = true
                    }
                ]
            };
            var state = new WorkspaceState
            {
                Sessions = [terminal],
                ActiveSessionId = terminal.Id,
                TerminalSessions = [new TerminalSession { Name = "Session 1", TerminalIds = [terminal.Id], ActiveTerminalId = terminal.Id }]
            };
            state.ActiveTerminalSessionId = state.TerminalSessions[0].Id;
            Save(state);
            var restored = Load(terminalProfile).Sessions.Single();
            return restored.CommandDraft == terminal.CommandDraft
                && restored.TerminalFontSize == 15
                && restored.CommandFontSize == 13
                && restored.ComposerAttachments.Count == 1
                && restored.ComposerAttachments[0].LocalPath == terminal.ComposerAttachments[0].LocalPath
                && restored.ComposerAttachments[0].DisplayName == "Image 1"
                && restored.ComposerAttachments[0].IsImage
                && restored.ComposerAttachments[0].IsTemporary;
        }
        finally
        {
            DirectoryOverride = originalDirectory;
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    internal static bool VerifyApplicationThemePersistenceForTest(WindowsTerminalProfile terminalProfile, string directory)
    {
        var originalDirectory = DirectoryOverride;
        try
        {
            DirectoryOverride = directory;
            Directory.CreateDirectory(directory);
            var terminal = new SessionProfile { Name = "Theme persistence" };
            var state = new WorkspaceState
            {
                Sessions = [terminal],
                ActiveSessionId = terminal.Id,
                TerminalSessions = [new TerminalSession { Name = "Session 1", TerminalIds = [terminal.Id], ActiveTerminalId = terminal.Id }],
                Settings = new WorkspaceSettings { ApplicationTheme = AppThemeCatalog.BlackThemeId }
            };
            state.ActiveTerminalSessionId = state.TerminalSessions[0].Id;
            Save(state);
            return LoadApplicationTheme() == AppThemeCatalog.BlackThemeId
                && Load(terminalProfile).Settings.ApplicationTheme == AppThemeCatalog.BlackThemeId;
        }
        finally
        {
            DirectoryOverride = originalDirectory;
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    public static void Save(WorkspaceState state)
    {
        var version = Interlocked.Increment(ref latestSaveVersion);
        SaveCore(state, version, DirectoryPath, FilePath);
    }

    public static Task SaveAsync(WorkspaceState state)
    {
        // Capture only plain model data while still on the UI thread. JSON
        // serialization and disk replacement then happen off-dispatcher without
        // enumerating live ObservableCollections from a worker thread.
        var snapshot = CreateSnapshot(state);
        var version = Interlocked.Increment(ref latestSaveVersion);
        var directoryPath = DirectoryPath;
        var filePath = FilePath;
        return Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var originalPriority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.Lowest;
                SaveCore(snapshot, version, directoryPath, filePath);
            }
            finally { thread.Priority = originalPriority; }
        });
    }

    internal static WorkspaceState CreateSnapshot(WorkspaceState state) => new()
    {
        Version = state.Version,
        Name = state.Name,
        Layout = state.Layout,
        WorkspaceSidebarExpanded = state.WorkspaceSidebarExpanded,
        ActiveSessionId = state.ActiveSessionId,
        ActiveTerminalSessionId = state.ActiveTerminalSessionId,
        Sessions = [.. state.Sessions.Select(CloneSession)],
        TerminalSessions = [.. state.TerminalSessions.Select(CloneTerminalSession)],
        Snippets = [.. state.Snippets.Select(value => new CommandSnippet
        {
            Id = value.Id,
            Name = value.Name,
            Category = value.Category,
            Command = value.Command,
            ShowInQuickAccess = value.ShowInQuickAccess
        })],
        Automations = [.. state.Automations.Select(value => new AutomationRule
        {
            Id = value.Id,
            Name = value.Name,
            Command = value.Command,
            TargetSessionId = value.TargetSessionId,
            ScheduleType = value.ScheduleType,
            IntervalMinutes = value.IntervalMinutes,
            DailyTime = value.DailyTime,
            ScheduledDate = value.ScheduledDate,
            Enabled = value.Enabled,
            ClearLine = value.ClearLine,
            HasRun = value.HasRun,
            LastRunUtc = value.LastRunUtc
        })],
        Settings = new WorkspaceSettings
        {
            ApplicationTheme = state.Settings.ApplicationTheme,
            FontFace = state.Settings.FontFace,
            FontSize = state.Settings.FontSize,
            CursorStyle = state.Settings.CursorStyle,
            CursorBlink = state.Settings.CursorBlink,
            DefaultCommandLine = state.Settings.DefaultCommandLine,
            DefaultWorkingDirectory = state.Settings.DefaultWorkingDirectory,
            AutomaticallySetTerminalColor = state.Settings.AutomaticallySetTerminalColor,
            ConfirmBeforeRemove = state.Settings.ConfirmBeforeRemove,
            KeepSessionsRunningInTray = state.Settings.KeepSessionsRunningInTray,
            RestoreSessionsAfterRestart = state.Settings.RestoreSessionsAfterRestart,
            SaveTerminalTranscripts = state.Settings.SaveTerminalTranscripts,
            SendToAllModifierEnabled = state.Settings.SendToAllModifierEnabled,
            SendToAllModifier = state.Settings.SendToAllModifier,
            CheckForUpdatesAutomatically = state.Settings.CheckForUpdatesAutomatically,
            AgentNotificationsEnabled = state.Settings.AgentNotificationsEnabled,
            NotificationSound = state.Settings.NotificationSound,
            CustomNotificationSoundPath = state.Settings.CustomNotificationSoundPath,
            ShowTmuxToggleWarning = state.Settings.ShowTmuxToggleWarning
        },
        LayoutSizes = state.LayoutSizes.ToDictionary(value => value.Key, value => CloneSizing(value.Value), StringComparer.Ordinal)
    };

    private static SessionProfile CloneSession(SessionProfile value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        AccentColor = value.AccentColor,
        CommandLine = value.CommandLine,
        WorkingDirectory = value.WorkingDirectory,
        AutoStart = value.AutoStart,
        AgentNotificationsEnabled = value.AgentNotificationsEnabled,
        UseRemoteTmux = value.UseRemoteTmux,
        UseLocalTmux = value.UseLocalTmux,
        LocalTmuxDistribution = value.LocalTmuxDistribution,
        CommandBarExpanded = value.CommandBarExpanded,
        TerminalFontSize = value.TerminalFontSize,
        CommandFontSize = value.CommandFontSize,
        PressEnterAfterComposerSend = value.PressEnterAfterComposerSend,
        CommandDraft = value.CommandDraft,
        ComposerAttachments = [.. value.ComposerAttachments.Select(attachment => new ComposerAttachmentState
        {
            LocalPath = attachment.LocalPath,
            DisplayName = attachment.DisplayName,
            IsImage = attachment.IsImage,
            IsTemporary = attachment.IsTemporary
        })],
        PendingCommands = [.. value.PendingCommands],
        CommandHistory = [.. value.CommandHistory],
        CommandHistoryTimestampsUtc = [.. value.CommandHistoryTimestampsUtc],
        AutomationBindings = [.. value.AutomationBindings.Select(binding => new TerminalAutomationBinding
        {
            AutomationId = binding.AutomationId,
            Enabled = binding.Enabled,
            AutoInsertAtEnd = binding.AutoInsertAtEnd
        })],
        LiveWorkingDirectory = value.LiveWorkingDirectory,
        LiveWorkingDirectoryIsSsh = value.LiveWorkingDirectoryIsSsh
    };

    private static TerminalSession CloneTerminalSession(TerminalSession value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        AccentColor = value.AccentColor,
        Layout = value.Layout,
        TerminalIds = [.. value.TerminalIds],
        ActiveTerminalId = value.ActiveTerminalId,
        LayoutSizes = value.LayoutSizes.ToDictionary(item => item.Key, item => CloneSizing(item.Value), StringComparer.Ordinal)
    };

    private static PaneLayoutSizing CloneSizing(PaneLayoutSizing value) => new()
    {
        Rows = [.. value.Rows],
        Columns = [.. value.Columns]
    };

    private static void SaveCore(WorkspaceState state, long version, string directoryPath, string filePath)
    {
        SaveGate.Wait();
        try
        {
            // A synchronous shutdown save or a newer debounced snapshot always
            // wins, even if an older worker was waiting for the file lock.
            if (version < Volatile.Read(ref latestSaveVersion)) return;
            Directory.CreateDirectory(directoryPath);
            var temporary = filePath + $".{version}.tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(true);
            }
            if (version < Volatile.Read(ref latestSaveVersion))
            {
                try { File.Delete(temporary); } catch { }
                return;
            }
            if (File.Exists(filePath)) File.Copy(filePath, filePath + ".bak", true);
            File.Move(temporary, filePath, true);
        }
        finally { SaveGate.Release(); }
    }

    private static int? NormalizeFontSize(int? value, int minimum, int maximum)
        => value is null ? null : Math.Clamp(value.Value, minimum, maximum);
}
