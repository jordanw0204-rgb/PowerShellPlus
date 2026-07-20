using System.Threading;
using System.Windows;

namespace PowerShellPlus.Native;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\PowerShellPlus.Native.Singleton.v1";
    private const string ActivationEventName = "Local\\PowerShellPlus.Native.Activate.v1";
    private Mutex? instanceMutex;
    private EventWaitHandle? activationEvent;
    private CancellationTokenSource? activationCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var automationMode = e.Args.Any(IsAutomationArgument);
        if (automationMode)
        {
            WorkspaceStore.DirectoryOverride = Path.Combine(Path.GetTempPath(), "PowerShellPlus-tests", Environment.ProcessId.ToString());
            CodexLaunchStore.DirectoryOverride = Path.Combine(WorkspaceStore.DirectoryPath, "session-recovery", "codex-launches");
            SshLaunchStore.DirectoryOverride = Path.Combine(WorkspaceStore.DirectoryPath, "session-recovery", "ssh-launches");
        }
        if (!automationMode && !ClaimPrimaryInstance())
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow(automationMode);
        MainWindow = window;
        if (!automationMode) StartActivationListener(window);

        var uiSnapshot = e.Args.FirstOrDefault(value => value.StartsWith("--ui-snapshot", StringComparison.OrdinalIgnoreCase));
        if (uiSnapshot is not null)
        {
            var directory = uiSnapshot.Contains('=') ? uiSnapshot[(uiSnapshot.IndexOf('=') + 1)..] : AppContext.BaseDirectory;
            ParkAutomationWindow(window);
            window.Loaded += async (_, _) =>
            {
                try { await window.RunUiSnapshotAsync(Path.GetFullPath(directory)); Shutdown(0); }
                catch (Exception exception) { File.WriteAllText(Path.Combine(Path.GetFullPath(directory), "ui-snapshot-error.txt"), exception.ToString()); Shutdown(2); }
            };
            window.Show();
            return;
        }

        var smoke = e.Args.FirstOrDefault(value => value.StartsWith("--smoke-test", StringComparison.OrdinalIgnoreCase));
        var codexSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--codex-smoke", StringComparison.OrdinalIgnoreCase));
        var multiSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--multi-smoke", StringComparison.OrdinalIgnoreCase));
        var persistenceSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--persistence-smoke", StringComparison.OrdinalIgnoreCase));
        var windowsTerminalSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--windows-terminal-smoke", StringComparison.OrdinalIgnoreCase));
        var lanRemoteSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--lan-remote-smoke", StringComparison.OrdinalIgnoreCase));
        var handoffSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--handoff-smoke", StringComparison.OrdinalIgnoreCase));
        var tailscaleInstallerSmoke = e.Args.FirstOrDefault(value => value.StartsWith("--tailscale-installer-smoke", StringComparison.OrdinalIgnoreCase));
        if (smoke is not null || codexSmoke is not null || multiSmoke is not null || persistenceSmoke is not null || windowsTerminalSmoke is not null || lanRemoteSmoke is not null || handoffSmoke is not null || tailscaleInstallerSmoke is not null)
        {
            ParkAutomationWindow(window);
            var argument = tailscaleInstallerSmoke ?? handoffSmoke ?? lanRemoteSmoke ?? windowsTerminalSmoke ?? persistenceSmoke ?? multiSmoke ?? codexSmoke ?? smoke!;
            var defaultName = tailscaleInstallerSmoke is not null ? "native-tailscale-installer-smoke.txt" : handoffSmoke is not null ? "native-handoff-smoke.txt" : lanRemoteSmoke is not null ? "native-lan-remote-smoke.txt" : windowsTerminalSmoke is not null ? "native-windows-terminal-smoke.txt" : persistenceSmoke is not null ? "native-persistence-smoke.txt" : multiSmoke is not null ? "native-multi-smoke.txt" : codexSmoke is not null ? "native-codex-smoke.txt" : "native-smoke.txt";
            var report = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : Path.Combine(AppContext.BaseDirectory, defaultName);
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var success = tailscaleInstallerSmoke is not null
                        ? await TailscaleInstaller.RunDownloadSmokeAsync(Path.GetFullPath(report))
                        : handoffSmoke is not null
                        ? await window.RunHandoffSmokeTestAsync(Path.GetFullPath(report))
                        : lanRemoteSmoke is not null
                        ? await window.RunLanRemoteSmokeTestAsync(Path.GetFullPath(report))
                        : windowsTerminalSmoke is not null
                        ? await window.RunWindowsTerminalCaptureSmokeTestAsync(Path.GetFullPath(report))
                        : persistenceSmoke is not null
                        ? await window.RunPersistenceSmokeTestAsync(Path.GetFullPath(report))
                        : multiSmoke is not null
                            ? await window.RunMultiPaneSmokeTestAsync(Path.GetFullPath(report))
                            : codexSmoke is not null
                                ? await window.RunCodexSmokeTestAsync(Path.GetFullPath(report))
                                : await window.RunSmokeTestAsync(Path.GetFullPath(report));
                    Shutdown(success ? 0 : 2);
                }
                catch (Exception exception)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                    File.WriteAllText(Path.GetFullPath(report), $"FAIL Automation gate threw an exception.\n{exception}");
                    Shutdown(2);
                }
            };
        }
        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.PrepareForShutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationCancellation?.Cancel();
        try { activationEvent?.Set(); } catch { }
        activationEvent?.Dispose();
        instanceMutex?.Dispose();
        activationCancellation?.Dispose();
        if (WorkspaceStore.DirectoryOverride is { } testDirectory)
        {
            try { Directory.Delete(testDirectory, true); } catch { }
            CodexLaunchStore.DirectoryOverride = null;
            SshLaunchStore.DirectoryOverride = null;
            WorkspaceStore.DirectoryOverride = null;
        }
        base.OnExit(e);
    }

    private bool ClaimPrimaryInstance()
    {
        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        instanceMutex = new Mutex(false, InstanceMutexName, out var createdNew);
        if (createdNew) return true;
        try { activationEvent.Set(); } catch { }
        activationEvent.Dispose();
        activationEvent = null;
        instanceMutex.Dispose();
        instanceMutex = null;
        return false;
    }

    private void StartActivationListener(MainWindow window)
    {
        if (activationEvent is null) return;
        activationCancellation = new CancellationTokenSource();
        var cancellation = activationCancellation.Token;
        _ = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    activationEvent.WaitOne();
                    if (!cancellation.IsCancellationRequested) Dispatcher.BeginInvoke(window.RestoreFromTray);
                }
                catch (ObjectDisposedException) { break; }
            }
        }, cancellation);
    }

    private static bool IsAutomationArgument(string value) => value.StartsWith("--ui-snapshot", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--smoke-test", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--codex-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--multi-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--persistence-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--windows-terminal-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--lan-remote-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--handoff-smoke", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("--tailscale-installer-smoke", StringComparison.OrdinalIgnoreCase);

    private static void ParkAutomationWindow(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 20000;
        window.Top = 100;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }
}
