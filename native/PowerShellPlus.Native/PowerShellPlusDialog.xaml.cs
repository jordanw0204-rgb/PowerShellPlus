using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerShellPlus.Native;

internal enum PowerShellPlusDialogKind
{
    Information,
    Success,
    Warning,
    Error,
    Question
}

internal enum PowerShellPlusDialogResult
{
    Primary,
    Secondary,
    Cancel
}

public partial class PowerShellPlusDialog : Window
{
    private PowerShellPlusDialogResult result = PowerShellPlusDialogResult.Cancel;

    private PowerShellPlusDialog(string title, string message, PowerShellPlusDialogKind kind,
        string primaryText, string? secondaryText, string? cancelText,
        bool defaultToPrimary, bool primaryIsDangerous)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        PrimaryActionButton.Content = primaryText;
        SecondaryActionButton.Content = secondaryText ?? string.Empty;
        SecondaryActionButton.Visibility = string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;
        CancelActionButton.Content = cancelText ?? string.Empty;
        CancelActionButton.Visibility = string.IsNullOrWhiteSpace(cancelText) ? Visibility.Collapsed : Visibility.Visible;

        var (glyph, color) = kind switch
        {
            PowerShellPlusDialogKind.Success => ("✓", "#A6E3A1"),
            PowerShellPlusDialogKind.Warning => ("!", "#F9E2AF"),
            PowerShellPlusDialogKind.Error => ("×", "#F38BA8"),
            PowerShellPlusDialogKind.Question => ("?", "#89B4FA"),
            _ => ("i", "#89DCEB")
        };
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        IconText.Text = glyph;
        IconText.Foreground = brush;
        IconBorder.BorderBrush = brush;

        if (primaryIsDangerous)
        {
            PrimaryActionButton.Background = (Brush)new BrushConverter().ConvertFromString("#5A2D3E")!;
            PrimaryActionButton.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#A64A67")!;
            PrimaryActionButton.Foreground = (Brush)new BrushConverter().ConvertFromString("#F38BA8")!;
        }

        PrimaryActionButton.IsDefault = defaultToPrimary;
        if (CancelActionButton.Visibility == Visibility.Visible)
        {
            CancelActionButton.IsCancel = true;
            CancelActionButton.IsDefault = !defaultToPrimary;
        }
        Loaded += (_, _) => (defaultToPrimary ? PrimaryActionButton :
            CancelActionButton.Visibility == Visibility.Visible ? CancelActionButton : PrimaryActionButton).Focus();
    }

    internal static void ShowMessage(Window? owner, string message, string title,
        PowerShellPlusDialogKind kind = PowerShellPlusDialogKind.Information,
        string primaryText = "OK") =>
        ShowActions(owner, message, title, kind, primaryText, null, null);

    internal static bool Confirm(Window? owner, string message, string title,
        PowerShellPlusDialogKind kind = PowerShellPlusDialogKind.Question,
        string primaryText = "Yes", string cancelText = "No",
        bool defaultToPrimary = true, bool primaryIsDangerous = false) =>
        ShowActions(owner, message, title, kind, primaryText, null, cancelText,
            defaultToPrimary, primaryIsDangerous) == PowerShellPlusDialogResult.Primary;

    internal static PowerShellPlusDialogResult ShowActions(Window? owner, string message, string title,
        PowerShellPlusDialogKind kind, string primaryText, string? secondaryText, string? cancelText,
        bool defaultToPrimary = true, bool primaryIsDangerous = false)
    {
        var dialog = new PowerShellPlusDialog(title, message, kind, primaryText, secondaryText, cancelText,
            defaultToPrimary, primaryIsDangerous);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _ = dialog.ShowDialog();
        return dialog.result;
    }

    internal static bool ValidateThemeContract()
    {
        var dialog = new PowerShellPlusDialog("Theme contract", "Wrapped body copy", PowerShellPlusDialogKind.Warning,
            "Continue", "More info", "Cancel", defaultToPrimary: false, primaryIsDangerous: true);
        return dialog.WindowStyle == WindowStyle.None
            && dialog.ResizeMode == ResizeMode.NoResize
            && !dialog.ShowInTaskbar
            && dialog.HeadingText.Text == "Theme contract"
            && dialog.MessageText.Text == "Wrapped body copy"
            && dialog.PrimaryActionButton.Content?.ToString() == "Continue"
            && dialog.SecondaryActionButton.Visibility == Visibility.Visible
            && dialog.CancelActionButton.IsDefault
            && dialog.PrimaryActionButton.Foreground is SolidColorBrush;
    }

    internal static PowerShellPlusDialog CreateSnapshotDialog() => new(
        "Global mode needs Tailscale",
        "PowerShellPlus can download the newest stable Windows installer from Tailscale's official package server. The file must pass Windows trust verification and match the Tailscale Inc. publisher before it opens.",
        PowerShellPlusDialogKind.Warning,
        "Download & install", "Open official page", "Not now",
        defaultToPrimary: true, primaryIsDangerous: false);

    private void PrimaryActionClick(object sender, RoutedEventArgs e)
    {
        result = PowerShellPlusDialogResult.Primary;
        DialogResult = true;
    }

    private void SecondaryActionClick(object sender, RoutedEventArgs e)
    {
        result = PowerShellPlusDialogResult.Secondary;
        DialogResult = false;
    }

    private void CancelActionClick(object sender, RoutedEventArgs e) => Close();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
