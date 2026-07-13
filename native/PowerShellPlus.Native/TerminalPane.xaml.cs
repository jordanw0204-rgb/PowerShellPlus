using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Text;
using EasyWindowsTerminalControl;

namespace PowerShellPlus.Native;

public partial class TerminalPane : UserControl
{
    public SessionProfile Profile { get; private set; }
    public event EventHandler? Activated;
    public event EventHandler? CloseRequested;
    public event EventHandler? EditRequested;
    private SessionRecoveryEntry? startupRecovery;
    private string previousOutput = string.Empty;

    public TerminalPane(SessionProfile profile, TerminalAppearance appearance, SessionRecoveryEntry? recovery = null, string? recoveredOutput = null)
    {
        Profile = profile;
        startupRecovery = recovery;
        previousOutput = recoveredOutput ?? string.Empty;
        InitializeComponent();
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile, recovery);
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        Terminal.FontSizeWhenSettingTheme = appearance.FontSize;
        Terminal.Theme = appearance.Theme;
        Loaded += async (_, _) =>
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            HideNativeScrollbar();
            StateText.Text = $"  {appearance.ProfileName} · native renderer";
            await Task.Delay(1400);
            if (Terminal.ConPTYTerm?.TermProcIsStarted != true)
            {
                try
                {
                    var term = Terminal.ConPTYTerm;
                    var commandLine = Terminal.StartupCommandLine;
                    await Task.Run(() => term!.Start(commandLine, 100, 30, true));
                }
                catch (Exception exception)
                {
                    StateText.Text = "  Start failed";
                    Directory.CreateDirectory(WorkspaceStore.DirectoryPath);
                    File.AppendAllText(Path.Combine(WorkspaceStore.DirectoryPath, "native-errors.log"), $"[{DateTime.Now:O}] {exception}\n");
                    return;
                }
            }
            StateDot.Fill = new SolidColorBrush(Color.FromRgb(166, 227, 161));
            ConfigureRecoveryView();
        };
    }

    public void SetActive(bool active)
    {
        PaneBorder.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(137, 180, 250) : Color.FromRgb(49, 50, 68));
        PaneBorder.BorderThickness = active ? new Thickness(1.5) : new Thickness(1);
    }

    public async void SendCommand(string command) => await SendCommandAsync(command);

    public async Task<bool> SendCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (Terminal.ConPTYTerm?.Process?.HasExited == true) await Terminal.RestartTerm();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Terminal.ConPTYTerm?.TermProcIsStarted == true)
                {
                    Terminal.ConPTYTerm.WriteToTerm(command + "\r");
                    Terminal.Focus();
                    return true;
                }
            }
            catch (NullReferenceException) { }
            await Task.Delay(100);
        }
        return false;
    }

    public async Task RestartAsync()
    {
        StateText.Text = "  Restarting…";
        startupRecovery = null;
        Terminal.StartupCommandLine = BuildCommandLine(Profile, null);
        await Terminal.RestartTerm();
        StateText.Text = "  Windows Terminal control";
        Terminal.Focus();
    }

    public void Stop()
    {
        try { Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
        StateText.Text = "  Stopped";
        StateDot.Fill = new SolidColorBrush(Color.FromRgb(108, 112, 134));
    }

    public string GetOutput()
    {
        try { return Terminal.ConPTYTerm?.GetConsoleText() ?? string.Empty; } catch { return string.Empty; }
    }

    public int? GetRootProcessId()
    {
        try
        {
            var process = Terminal.ConPTYTerm?.Process;
            if (process is null || process.HasExited) return null;
            var type = process.GetType();
            if (type.GetProperty("Pid")?.GetValue(process) is int pid) return pid;
            if (type.GetProperty("Process")?.GetValue(process) is Process wrapped) return wrapped.Id;
            var processInfo = type.GetProperty("ProcessInfo")?.GetValue(process);
            if (processInfo is not null)
            {
                var infoType = processInfo.GetType();
                var value = infoType.GetField("dwProcessId")?.GetValue(processInfo) ?? infoType.GetProperty("dwProcessId")?.GetValue(processInfo);
                if (value is uint unsigned) return checked((int)unsigned);
                if (value is int signed) return signed;
            }
        }
        catch { return null; }
        return null;
    }

    public CodexProcessState GetCodexProcessState()
    {
        var processId = GetRootProcessId();
        return processId is int value ? ProcessTreeInspector.FindCodexProcess(value) : default;
    }

    public void ApplyAppearance(TerminalAppearance appearance)
    {
        // Font properties only take effect when the theme is (re)applied; the
        // Theme setter pushes everything to the native control immediately.
        Terminal.FontFamilyWhenSettingTheme = new FontFamily(appearance.FontFace);
        Terminal.FontSizeWhenSettingTheme = appearance.FontSize;
        Terminal.Theme = appearance.Theme;
    }

    public void ApplyProfile(SessionProfile profile)
    {
        Profile = profile;
        startupRecovery = null;
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile, null);
    }

    public bool IsNativeScrollbarHidden()
    {
        var scrollbar = FindVisualChild<ScrollBar>(Terminal.Terminal);
        return scrollbar is null || scrollbar.Visibility != Visibility.Visible || scrollbar.ActualWidth == 0;
    }

    private void HideNativeScrollbar()
    {
        var scrollbar = FindVisualChild<ScrollBar>(Terminal.Terminal);
        if (scrollbar is null) return;
        scrollbar.Visibility = Visibility.Collapsed;
        scrollbar.Width = 0;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    public static string BuildCommandLine(SessionProfile profile, SessionRecoveryEntry? recovery)
    {
        var command = Environment.ExpandEnvironmentVariables(profile.CommandLine.Trim());
        var resumeCodex = recovery?.CodexWasActive == true;
        var startupDirectory = resumeCodex && !string.IsNullOrWhiteSpace(recovery?.WorkingDirectory) && Directory.Exists(recovery.WorkingDirectory)
            ? recovery.WorkingDirectory
            : profile.WorkingDirectory;
        var validDirectory = !string.IsNullOrWhiteSpace(startupDirectory) && Directory.Exists(startupDirectory);
        var escaped = validDirectory ? startupDirectory.Replace("'", "''") : string.Empty;
        var resumeArgument = resumeCodex && CodexSessionLocator.IsSafeCodexId(recovery?.CodexSessionId) ? $" '{recovery!.CodexSessionId}'" : " --all";
        if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase) || command.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            var script = validDirectory ? $"Set-Location -LiteralPath '{escaped}'" : string.Empty;
            if (resumeCodex) script += (script.Length == 0 ? string.Empty : "; ") + $"& codex resume{resumeArgument}";
            if (script.Length == 0) return command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return $"{command} -NoExit -EncodedCommand {encoded}";
        }
        if (resumeCodex && Path.GetFileNameWithoutExtension(command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty).Equals("codex", StringComparison.OrdinalIgnoreCase))
            return $"codex resume{resumeArgument}";
        return command;
    }

    public static string DecodePowerShellStartupScript(string commandLine)
    {
        const string marker = "-EncodedCommand ";
        var index = commandLine.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        var encoded = commandLine[(index + marker.Length)..].Trim().Split(' ')[0];
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(encoded)); }
        catch { return string.Empty; }
    }

    public void SetPreviousOutputForTest(string output)
    {
        previousOutput = output;
        ConfigureRecoveryView(true);
    }

    public void HidePreviousOutputForTest() => RecoveryOverlay.Visibility = Visibility.Collapsed;

    private void ConfigureRecoveryView(bool show = false)
    {
        if (string.IsNullOrWhiteSpace(previousOutput)) return;
        PreviousOutputButton.Visibility = Visibility.Visible;
        RecoveryOutputText.Text = previousOutput;
        RecoveryTimestampText.Text = startupRecovery?.CapturedUtc.ToLocalTime().ToString("Recovered MMM d, yyyy 'at' h:mm tt") ?? "Recovered after restart";
        if (show || startupRecovery?.CodexWasActive != true) RecoveryOverlay.Visibility = Visibility.Visible;
    }

    private void ActivatePane(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);
    private void PreviousOutputClick(object sender, RoutedEventArgs e) { ConfigureRecoveryView(true); RecoveryOverlay.Visibility = Visibility.Visible; }
    private void CloseRecoveryClick(object sender, RoutedEventArgs e) { RecoveryOverlay.Visibility = Visibility.Collapsed; Terminal.Focus(); }
    private void ClearClick(object sender, RoutedEventArgs e) { Terminal.ConPTYTerm?.ClearUITerminal(); Terminal.Focus(); }
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private async void RestartClick(object sender, RoutedEventArgs e) => await RestartAsync();
    private void EditClick(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);
    private void CloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
