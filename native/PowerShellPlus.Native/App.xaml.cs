using System.Windows;

namespace PowerShellPlus.Native;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        var uiSnapshot = e.Args.FirstOrDefault(value => value.StartsWith("--ui-snapshot", StringComparison.OrdinalIgnoreCase));
        if (uiSnapshot is not null)
        {
            var directory = uiSnapshot.Contains('=') ? uiSnapshot[(uiSnapshot.IndexOf('=') + 1)..] : AppContext.BaseDirectory;
            // Keep the snapshot window away from the visible desktop so automated
            // verification never flashes over or steals focus from the user.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 20000;
            window.Top = 100;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
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
        if (smoke is not null || codexSmoke is not null || multiSmoke is not null)
        {
            // Automated gates must never flash over or steal focus from whatever
            // the user is doing; park the window outside the visible desktop.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 20000;
            window.Top = 100;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            var argument = multiSmoke ?? codexSmoke ?? smoke!;
            var defaultName = multiSmoke is not null ? "native-multi-smoke.txt" : codexSmoke is not null ? "native-codex-smoke.txt" : "native-smoke.txt";
            var report = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : Path.Combine(AppContext.BaseDirectory, defaultName);
            window.Loaded += async (_, _) =>
            {
                var success = multiSmoke is not null
                    ? await window.RunMultiPaneSmokeTestAsync(Path.GetFullPath(report))
                    : codexSmoke is not null
                        ? await window.RunCodexSmokeTestAsync(Path.GetFullPath(report))
                        : await window.RunSmokeTestAsync(Path.GetFullPath(report));
                Shutdown(success ? 0 : 2);
            };
        }
        window.Show();
    }
}
