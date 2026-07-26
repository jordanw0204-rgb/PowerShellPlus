using System.Text;

namespace PowerShellPlus.Native;

internal static class PowerShellStartupScriptStore
{
    private const string FileMarker = "-File \"";
    public static string DirectoryPath => Path.Combine(SessionRecoveryStore.DirectoryPath, "startup-scripts");

    public static string Save(string paneId, string script)
    {
        var directory = DirectoryPath;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SessionRecoveryStore.SafeSessionId(paneId) + ".ps1");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, script, new UTF8Encoding(true));
        File.Move(temporary, path, true);
        return path;
    }

    public static string ReadFromCommandLine(string? commandLine)
    {
        try
        {
            var value = commandLine ?? string.Empty;
            var start = value.LastIndexOf(FileMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += FileMarker.Length;
            var end = value.IndexOf('"', start);
            if (end <= start) return string.Empty;
            var path = value[start..end];
            var expectedDirectory = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(fullPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)) return string.Empty;
            return File.ReadAllText(fullPath);
        }
        catch { return string.Empty; }
    }

    public static void Delete(string paneId)
    {
        try
        {
            var path = Path.Combine(DirectoryPath, SessionRecoveryStore.SafeSessionId(paneId) + ".ps1");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
