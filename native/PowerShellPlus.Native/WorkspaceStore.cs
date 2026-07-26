using System.Text.Json;

namespace PowerShellPlus.Native;

public static class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? DirectoryOverride { get; set; }
    public static string DirectoryPath => DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerShellPlus");
    public static string FilePath => Path.Combine(DirectoryPath, "native-workspace.json");

    public static WorkspaceState Load(WindowsTerminalProfile terminalProfile)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded is not null && loaded.Version is >= 3 and <= 7)
            {
                var upgradedFromLegacy = loaded.Version <= 6;
                loaded.Version = 7;
                loaded.Settings ??= new WorkspaceSettings();
                    if (string.Equals(loaded.Settings.SendToAllModifier, "Ctrl", StringComparison.OrdinalIgnoreCase)) loaded.Settings.SendToAllModifier = "Shift";
                loaded.LayoutSizes ??= [];
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
        state.Sessions.Add(new SessionProfile { Name = terminalProfile.ProfileName, CommandLine = terminalProfile.CommandLine });
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
            return migrated.Version == 7
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

    public static void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
        File.Move(temporary, FilePath, true);
    }

    private static int? NormalizeFontSize(int? value, int minimum, int maximum)
        => value is null ? null : Math.Clamp(value.Value, minimum, maximum);
}
