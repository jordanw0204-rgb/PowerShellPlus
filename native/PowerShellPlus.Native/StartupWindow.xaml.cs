using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public readonly record struct StartupProgress(string Stage, string Detail, int Completed, int Total);

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var pulse = new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            LogoGlow.BeginAnimation(OpacityProperty, pulse);
        };
    }

    internal void Report(StartupProgress progress)
    {
        StageText.Text = progress.Stage;
        DetailText.Text = progress.Detail;
        var total = Math.Max(1, progress.Total);
        StartupProgressBar.Maximum = total;
        StartupProgressBar.Value = Math.Clamp(progress.Completed, 0, total);
        ProgressText.Text = progress.Total > 0 ? $"{Math.Clamp(progress.Completed, 0, total)} / {total}" : "Initializing";
        UpdateLayout();
        // Startup construction runs on the UI thread. Pump only render-priority
        // work so every reported stage becomes visible without processing input
        // against a half-constructed MainWindow.
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
    }

    internal bool ContractIsValidForTest => StageText is not null && DetailText is not null
        && StartupProgressBar is { Minimum: 0 } && ProgressText is not null;
}
