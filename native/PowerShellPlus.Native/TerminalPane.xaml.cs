using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EasyWindowsTerminalControl;

namespace PowerShellPlus.Native;

public partial class TerminalPane : UserControl
{
    public SessionProfile Profile { get; private set; }
    public event EventHandler? Activated;
    public event EventHandler? CloseRequested;
    public event EventHandler? EditRequested;

    public TerminalPane(SessionProfile profile, TerminalAppearance appearance)
    {
        Profile = profile;
        InitializeComponent();
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile);
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
        Terminal.StartupCommandLine = BuildCommandLine(Profile);
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
        TitleText.Text = profile.Name;
        Terminal.StartupCommandLine = BuildCommandLine(profile);
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

    private static string BuildCommandLine(SessionProfile profile)
    {
        var command = Environment.ExpandEnvironmentVariables(profile.CommandLine.Trim());
        if (string.IsNullOrWhiteSpace(profile.WorkingDirectory) || !Directory.Exists(profile.WorkingDirectory)) return command;
        var escaped = profile.WorkingDirectory.Replace("'", "''");
        if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase) || command.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
            return $"{command} -NoExit -Command \"Set-Location -LiteralPath '{escaped}'\"";
        return command;
    }

    private void ActivatePane(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);
    private void ClearClick(object sender, RoutedEventArgs e) { Terminal.ConPTYTerm?.ClearUITerminal(); Terminal.Focus(); }
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private async void RestartClick(object sender, RoutedEventArgs e) => await RestartAsync();
    private void EditClick(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);
    private void CloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
