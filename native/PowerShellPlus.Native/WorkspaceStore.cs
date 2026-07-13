using System.Text.Json;

namespace PowerShellPlus.Native;

public static class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerShellPlus");
    public static string FilePath => Path.Combine(DirectoryPath, "native-workspace.json");

    public static WorkspaceState Load(WindowsTerminalProfile terminalProfile)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null && loaded.Version == 3)
                {
                    loaded.Settings ??= new WorkspaceSettings();
                    loaded.LayoutSizes ??= [];
                    return loaded;
                }
            }
        }
        catch { }

        var state = new WorkspaceState();
        state.Sessions.Add(new SessionProfile { Name = terminalProfile.ProfileName, CommandLine = terminalProfile.CommandLine });
        state.Snippets.Add(new CommandSnippet { Name = "Git status", Category = "Development", Command = "git status --short --branch" });
        state.Snippets.Add(new CommandSnippet { Name = "Top processes", Category = "System", Command = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15" });
        state.ActiveSessionId = state.Sessions[0].Id;
        return state;
    }

    public static void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
        File.Move(temporary, FilePath, true);
    }
}
