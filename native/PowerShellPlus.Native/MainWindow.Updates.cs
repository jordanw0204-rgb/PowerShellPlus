using System.Diagnostics;
using System.Windows;

namespace PowerShellPlus.Native;

public partial class MainWindow
{
    private bool updateCheckRunning;

    private async void CheckForUpdatesClick(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(manual: true);

    private void SettingsUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsUiReady) return;
        state.Settings.CheckForUpdatesAutomatically = SettingsAutomaticUpdates.IsChecked == true;
        SettingsUpdateStatus.Text = state.Settings.CheckForUpdatesAutomatically
            ? "Automatic update notifications are enabled."
            : "Automatic notifications are off. Manual checks still work.";
        ScheduleSave();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateCheckRunning || (!manual && !state.Settings.CheckForUpdatesAutomatically)) return;
        updateCheckRunning = true;
        if (manual)
        {
            SettingsCheckForUpdatesButton.IsEnabled = false;
            SettingsCheckForUpdatesButton.Content = "Checking…";
            SettingsUpdateStatus.Text = "Checking the latest stable GitHub Release…";
            UpdateStatus("Checking for PowerShellPlus updates…");
        }

        try
        {
            var check = await ApplicationUpdater.CheckLatestAsync();
            if (!check.IsUpdateAvailable || check.Release is null)
            {
                if (manual)
                {
                    SettingsUpdateStatus.Text = check.Release is null
                        ? "No published stable release is available yet."
                        : $"PowerShellPlus {check.CurrentVersion} is up to date.";
                    PowerShellPlusDialog.ShowMessage(this, SettingsUpdateStatus.Text, "No update available",
                        PowerShellPlusDialogKind.Success);
                    UpdateStatus("PowerShellPlus is up to date");
                }
                return;
            }

            var choice = PowerShellPlusDialog.ShowUpdate(this, check.Release);
            if (choice.DontShowAgain)
            {
                state.Settings.CheckForUpdatesAutomatically = false;
                SettingsAutomaticUpdates.IsChecked = false;
                ScheduleSave();
            }
            if (!choice.Accepted)
            {
                if (manual) SettingsUpdateStatus.Text = choice.DontShowAgain
                    ? "Update declined. Automatic notifications are now off."
                    : $"PowerShellPlus {check.Release.DisplayVersion} is available whenever you're ready.";
                return;
            }

            await DownloadAndStartUpdateAsync(check.Release);
        }
        catch (Exception exception)
        {
            LogNativeError("Application update", exception);
            if (manual)
            {
                SettingsUpdateStatus.Text = "The update check failed. Your current installation was not changed.";
                PowerShellPlusDialog.ShowMessage(this, exception.Message + "\n\nYour current installation was not changed.",
                    "Update check failed", PowerShellPlusDialogKind.Error);
                UpdateStatus("Update check failed — current installation unchanged");
            }
        }
        finally
        {
            updateCheckRunning = false;
            if (SettingsCheckForUpdatesButton is not null)
            {
                SettingsCheckForUpdatesButton.IsEnabled = true;
                SettingsCheckForUpdatesButton.Content = "Check for updates";
            }
        }
    }

    private async Task DownloadAndStartUpdateAsync(UpdateRelease release)
    {
        SettingsCheckForUpdatesButton.IsEnabled = false;
        SettingsUpdateStatus.Text = $"Downloading PowerShellPlus {release.DisplayVersion}…";
        UpdateStatus($"Downloading PowerShellPlus {release.DisplayVersion}…");
        var progress = new Progress<double>(value =>
        {
            var percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
            SettingsUpdateStatus.Text = $"Downloading and verifying update… {percent}%";
        });
        var installerPath = await ApplicationUpdater.DownloadAndVerifyInstallerAsync(release, progress);
        SettingsUpdateStatus.Text = "Update verified. Saving sessions and opening the installer…";
        UpdateStatus("Update verified — saving sessions before installation");
        CaptureRecoverySnapshot();
        SaveNow();
        var installer = Process.Start(ApplicationUpdater.CreateInstallerStartInfo(installerPath))
            ?? throw new InvalidOperationException("Windows did not open the verified PowerShellPlus installer.");
        installer.Dispose();
        explicitShutdown = true;
        Close();
    }

    internal bool UpdateUiContractForTest => SettingsCheckForUpdatesButton.Content?.ToString() == "Check for updates"
        && SettingsInstalledVersion.Text.Contains(ApplicationUpdater.CurrentVersionText, StringComparison.Ordinal)
        && PowerShellPlusDialog.ValidateUpdatePromptContract();
}
