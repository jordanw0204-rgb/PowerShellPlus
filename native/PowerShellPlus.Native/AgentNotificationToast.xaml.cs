using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public partial class AgentNotificationToast : Window
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private readonly DispatcherTimer dismissTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly IntPtr anchorWindow;
    private readonly Action activated;
    private bool dismissing;

    public AgentNotificationToast(string title, string message, string terminalName, bool waiting,
        string accentColor, IntPtr anchorWindow, Action activated)
    {
        this.anchorWindow = anchorWindow;
        this.activated = activated;
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        TerminalText.Text = terminalName;
        var status = waiting ? Color.FromRgb(249, 226, 175) : Color.FromRgb(137, 180, 250);
        var accent = ParseColor(accentColor, status);
        AccentBar.Background = new SolidColorBrush(accent);
        ProgressBar.Background = new SolidColorBrush(accent);
        StatusGlyph.Foreground = new SolidColorBrush(status);
        StatusHalo.Fill = new SolidColorBrush(status);
        dismissTimer.Tick += (_, _) => BeginDismiss();
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) =>
        {
            PlaceAtTopCenter();
            BeginEntrance();
            dismissTimer.Start();
        };
    }

    private void PlaceAtTopCenter()
    {
        var screen = anchorWindow != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(anchorWindow)
            : System.Windows.Forms.Screen.PrimaryScreen;
        var area = screen?.WorkingArea ?? System.Windows.Forms.SystemInformation.WorkingArea;
        Left = area.Left + (area.Width - ActualWidth) / 2d;
        Top = area.Top + 12;
    }

    private void BeginEntrance()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToastRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = easing });
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, dismissTimer.Interval));
    }

    private void BeginDismiss()
    {
        if (dismissing) return;
        dismissing = true;
        dismissTimer.Stop();
        var duration = TimeSpan.FromMilliseconds(190);
        var opacity = new DoubleAnimation(0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        opacity.Completed += (_, _) => Close();
        ToastRoot.BeginAnimation(OpacityProperty, opacity);
        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -16, duration));
    }

    public void DismissImmediately()
    {
        dismissTimer.Stop();
        if (IsLoaded) Close();
    }

    private void ToastCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        activated();
        BeginDismiss();
        e.Handled = true;
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        BeginDismiss();
        e.Handled = true;
    }

    private void MakeNonActivating()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return value is null ? fallback : (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    internal static bool ContractPassesForTest()
    {
        var toast = new AgentNotificationToast("Codex finished", "Finished working.", "Terminal", false,
            "#89B4FA", IntPtr.Zero, () => { });
        return toast.WindowStyle == WindowStyle.None && toast.AllowsTransparency && toast.Topmost
            && toast.ShowActivated == false && toast.ToastCard.CornerRadius.TopLeft == 14
            && toast.AccentBar.Background is SolidColorBrush && toast.ProgressBar.Background is SolidColorBrush;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);
}
