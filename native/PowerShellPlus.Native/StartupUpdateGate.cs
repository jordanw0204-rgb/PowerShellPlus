using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PowerShellPlus.Native;

/// <summary>
/// Checks for an accepted application update before MainWindow is constructed.
/// That boundary is intentional: constructing MainWindow reads recovery state,
/// creates every terminal pane, and makes those panes eligible for startup.
/// </summary>
internal static class StartupUpdateGate
{
    internal static bool ShouldCheckAutomatically(string? workspacePath = null)
    {
        var path = workspacePath ?? WorkspaceStore.FilePath;
        try
        {
            if (!File.Exists(path)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return !document.RootElement.TryGetProperty(nameof(WorkspaceState.Settings), out var settings)
                || settings.ValueKind != JsonValueKind.Object
                || !settings.TryGetProperty(nameof(WorkspaceSettings.CheckForUpdatesAutomatically), out var enabled)
                || enabled.ValueKind != JsonValueKind.False;
        }
        catch
        {
            // WorkspaceStore also falls back to defaults for an unreadable state
            // file. Keep the update default consistent without blocking startup.
            return true;
        }
    }

    internal static async Task<bool> TryStartAcceptedUpdateAsync(StartupWindow owner,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldCheckAutomatically()) return false;

        owner.Report(new StartupProgress("Checking for updates",
            "Looking for the latest stable PowerShellPlus release before terminals start", 0, 1));
        UpdateCheckResult check;
        try
        {
            check = await ApplicationUpdater.CheckLatestAsync(cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            owner.Report(new StartupProgress("Update check unavailable",
                "Opening the installed version without delaying your terminals", 1, 1));
            TryLogFailure("Startup update check", exception);
            return false;
        }

        if (!check.IsUpdateAvailable || check.Release is null)
        {
            owner.Report(new StartupProgress("PowerShellPlus is up to date",
                $"Version {check.CurrentVersion} is the latest stable release", 1, 1));
            return false;
        }

        var release = check.Release;
        var choice = PowerShellPlusDialog.ShowUpdate(owner, release);
        if (choice.DontShowAgain) TryDisableAutomaticChecks();
        if (!choice.Accepted)
        {
            owner.Report(new StartupProgress("Opening your workspace",
                $"PowerShellPlus {release.DisplayVersion} remains available in Settings", 1, 1));
            return false;
        }

        while (true)
        {
            try
            {
                var progress = new Progress<double>(value =>
                {
                    var percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
                    owner.Report(new StartupProgress("Downloading update",
                        $"Verifying PowerShellPlus {release.DisplayVersion} - {percent}%", percent, 100));
                });
                var installerPath = await ApplicationUpdater.DownloadAndVerifyInstallerAsync(release, progress, cancellationToken);
                owner.Report(new StartupProgress("Installing update",
                    "Terminals were not started. The newest version will open automatically.", 100, 100));
                using var installer = Process.Start(ApplicationUpdater.CreateInstallerStartInfo(installerPath))
                    ?? throw new InvalidOperationException("Windows did not open the verified PowerShellPlus installer.");
                return true;
            }
            catch (Exception exception)
            {
                TryLogFailure("Startup update install", exception);
                var retry = PowerShellPlusDialog.ShowActions(owner,
                    exception.Message + "\n\nNo terminal sessions were started. Retry the update, or explicitly open the installed version.",
                    "PowerShellPlus could not update", PowerShellPlusDialogKind.Error,
                    "Retry update", null, "Open installed version", defaultToPrimary: true);
                if (retry != PowerShellPlusDialogResult.Primary) return false;
            }
        }
    }

    private static void TryDisableAutomaticChecks()
    {
        try
        {
            var state = WorkspaceStore.Load(WindowsTerminalProfile.Load());
            state.Settings.CheckForUpdatesAutomatically = false;
            WorkspaceStore.Save(state);
        }
        catch (Exception exception) { TryLogFailure("Saving automatic update preference", exception); }
    }

    private static void TryLogFailure(string operation, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
            File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"),
                $"[{DateTime.Now:O}] {operation}: {exception}\n");
        }
        catch { }
    }

    internal static bool ContractPassesForTest(string directory)
    {
        Directory.CreateDirectory(directory);
        var enabledPath = Path.Combine(directory, "enabled.json");
        var disabledPath = Path.Combine(directory, "disabled.json");
        var missingSettingPath = Path.Combine(directory, "missing-setting.json");
        File.WriteAllText(enabledPath, "{\"Settings\":{\"CheckForUpdatesAutomatically\":true}}");
        File.WriteAllText(disabledPath, "{\"Settings\":{\"CheckForUpdatesAutomatically\":false}}");
        File.WriteAllText(missingSettingPath, "{\"Settings\":{}}");
        return ShouldCheckAutomatically(enabledPath)
            && !ShouldCheckAutomatically(disabledPath)
            && ShouldCheckAutomatically(missingSettingPath)
            && ShouldCheckAutomatically(Path.Combine(directory, "missing.json"));
    }
}
