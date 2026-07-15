using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public partial class LanRemoteDialog : Window
{
    private readonly LanRemoteServer server;
    private readonly DispatcherTimer refreshTimer;

    internal LanRemoteDialog(LanRemoteServer server)
    {
        this.server = server;
        InitializeComponent();
        AddressList.ItemsSource = server.Urls;
        AddressList.SelectedIndex = server.Urls.Count > 0 ? 0 : -1;
        PairingCodeText.Text = server.PairingCode;
        AllowInputToggle.IsChecked = server.AllowInput;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += (_, _) => RefreshConnectionCount();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        RefreshConnectionCount();
    }

    private void RefreshConnectionCount()
    {
        var count = server.ConnectedClients;
        ConnectionCountText.Text = $"{count} phone{(count == 1 ? string.Empty : "s")} connected · {server.Urls.Count} LAN address{(server.Urls.Count == 1 ? string.Empty : "es")}";
    }

    private void AllowInputChanged(object sender, RoutedEventArgs e) => server.AllowInput = AllowInputToggle.IsChecked == true;

    private void CopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (AddressList.SelectedItem is not string address) address = server.Urls.FirstOrDefault() ?? string.Empty;
        if (address.Length > 0) Clipboard.SetText(address);
    }

    private void OpenBrowserClick(object sender, RoutedEventArgs e)
    {
        var address = AddressList.SelectedItem as string ?? server.Urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(address)) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private async void StopSharingClick(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { await server.StopAsync(); }
        finally { Close(); }
    }

    private void DoneClick(object sender, RoutedEventArgs e) => Close();
}
