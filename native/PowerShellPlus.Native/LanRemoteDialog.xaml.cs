using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public partial class LanRemoteDialog : Window
{
    private readonly LanRemoteServer server;
    private readonly DispatcherTimer refreshTimer;
    private string pairedDevicesSignature = string.Empty;

    internal LanRemoteDialog(LanRemoteServer server)
    {
        this.server = server;
        InitializeComponent();
        AddressList.ItemsSource = server.Addresses;
        AddressList.SelectedIndex = server.Addresses.Count > 0 ? 0 : -1;
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
        var devices = server.PairedDevices;
        ConnectionCountText.Text = $"{count} device{(count == 1 ? string.Empty : "s")} connected · {devices.Count} saved · {server.Addresses.Count} adapter address{(server.Addresses.Count == 1 ? string.Empty : "es")}";
        PairingCodeText.Text = server.PairingCode;
        var signature = string.Join('|', devices.Select(value =>
            $"{value.Id}:{value.IsConnected}:{(value.IsConnected ? 0 : value.LastSeenUtc.UtcTicks / TimeSpan.TicksPerMinute)}"));
        if (!string.Equals(signature, pairedDevicesSignature, StringComparison.Ordinal))
        {
            pairedDevicesSignature = signature;
            PairedDeviceList.ItemsSource = devices;
            PairedDeviceList.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            PairedDevicesEmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AllowInputChanged(object sender, RoutedEventArgs e) => server.AllowInput = AllowInputToggle.IsChecked == true;

    private void CopyAddressClick(object sender, RoutedEventArgs e)
    {
        var address = (AddressList.SelectedItem as LanRemoteAddress)?.Url ?? server.Urls.FirstOrDefault() ?? string.Empty;
        if (address.Length > 0) Clipboard.SetText(address);
    }

    private void OpenBrowserClick(object sender, RoutedEventArgs e)
    {
        var address = (AddressList.SelectedItem as LanRemoteAddress)?.Url ?? server.Urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(address)) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private async void RemovePairedDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string deviceId } button) return;
        button.IsEnabled = false;
        try
        {
            await server.RevokePairedDeviceAsync(deviceId);
            pairedDevicesSignature = string.Empty;
            RefreshConnectionCount();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"PowerShellPlus could not remove this paired device.\n\n{exception.Message}",
                "Could not remove device", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { button.IsEnabled = true; }
    }

    private async void StopSharingClick(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { await server.StopAsync(); }
        finally { Close(); }
    }

    private void DoneClick(object sender, RoutedEventArgs e) => Close();
}
