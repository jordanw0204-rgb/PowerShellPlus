using System.Windows;
using System.Windows.Input;

namespace PowerShellPlus.Native;

public partial class WindowsTerminalImportDialog : Window
{
    public WindowsTerminalImportDialog(WindowsTerminalImportPlan plan)
    {
        Plan = plan;
        InitializeComponent();
        SourceTitleText.Text = $"{plan.Source.WindowTitle}  •  {plan.Rows.Count} tab{(plan.Rows.Count == 1 ? string.Empty : "s")}";
        RowsList.ItemsSource = plan.Rows;
    }

    public WindowsTerminalImportPlan Plan { get; }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var selected = Plan.Rows.Select(value => value.SelectedChoice?.Session).Where(value => value is not null).Cast<CodexSessionMatch>().ToList();
        var duplicate = selected.GroupBy(value => value.SessionId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, "The same Codex thread cannot be resumed in more than one imported tab.", "Choose unique Codex sessions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var unsafeChoice = selected.FirstOrDefault(value => !CodexSessionLocator.IsSafeCodexId(value.SessionId)
            || !CodexSessionLocator.IsSafeCodexPermissionState(value.PermissionProfile, value.SandboxMode, value.ApprovalPolicy, value.ApprovalsReviewer)
            || !CodexSessionLocator.IsSafeCodexApprovalsReviewer(value.ApprovalsReviewer));
        if (unsafeChoice is not null)
        {
            MessageBox.Show(this, "PowerShellPlus could not verify that Codex thread's permission profile, approval policy, and approval reviewer. Choose another thread or import the tab as PowerShell so permissions are never silently changed.", "Codex permissions unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Plan.Rows.Any(value => value.Tab.LooksLikeCodex && value.SelectedChoice?.Session is null)
            && MessageBox.Show(this, "One or more tabs appear to contain Codex but are set to open as PowerShell without resuming Codex. Continue?", "Codex session not selected", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
