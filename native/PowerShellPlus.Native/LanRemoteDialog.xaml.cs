using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public partial class LanRemoteDialog : Window
{
    private readonly LanRemoteServer server;
    private readonly Func<RemoteAccessMode, Task> switchMode;
    private readonly Func<Task> stopSharing;
    private readonly DispatcherTimer refreshTimer;
    private string pairedDevicesSignature = string.Empty;
    private RemoteAccessMode displayedMode;

    internal LanRemoteDialog(LanRemoteServer server, Func<RemoteAccessMode, Task> switchMode, Func<Task> stopSharing)
    {
        this.server = server;
        this.switchMode = switchMode;
        this.stopSharing = stopSharing;
        InitializeComponent();
        displayedMode = server.Mode;
        BindAddresses();
        PairingCodeText.Text = server.PairingCode;
        AllowInputToggle.IsChecked = server.AllowInput;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += (_, _) => RefreshConnectionCount();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        ApplyModePresentation();
        RefreshConnectionCount();
    }

    private void BindAddresses()
    {
        AddressList.ItemsSource = null;
        AddressList.ItemsSource = server.Addresses;
        AddressList.SelectedIndex = server.Addresses.Count > 0 ? 0 : -1;
    }

    private void ApplyModePresentation()
    {
        displayedMode = server.Mode;
        var global = displayedMode == RemoteAccessMode.Global;
        Title = global ? "Remote Access — Global" : "Remote Access — LAN";
        ModeEyebrowText.Text = global ? "GLOBAL REMOTE" : "LAN REMOTE";
        ModeTitleText.Text = global ? "Your terminals, from any browser" : "Your terminals, on this network";
        ModeDescriptionText.Text = global
            ? "Open the HTTPS address in Safari, Chrome, or any modern browser. Nothing is installed on the phone; the connector runs only on this PC."
            : "Open the recommended address on your phone and pair it once. Every current session is mirrored live without moving or restarting its PowerShell process.";
        AddressLabelText.Text = global ? "GLOBAL HTTPS ADDRESS" : "LAN ADDRESS";
        AddressHelpText.Text = global
            ? "This stable ts.net address works over cellular or any Wi-Fi. Tailscale Funnel hides your home IP and forwards only this loopback-only web server."
            : "Wi-Fi or Ethernet with an internet gateway is listed first. Virtual adapters work only for devices attached to those networks.";
        SecurityTitleText.Text = global ? "Public URL, protected application" : "Trusted private network only";
        SecurityBodyText.Text = global
            ? "The URL is reachable from the internet, but terminal data requires PowerShellPlus pairing. Global mode uses a 12-digit one-time code, a Secure HttpOnly saved credential, a global attempt limit, origin checks, and encrypted HTTPS. Keep the code private."
            : "LAN mode uses local HTTP. Use it only on trusted Private Wi-Fi/Ethernet and never forward its port through your router.";
        ModeHelpText.Text = global
            ? "No phone app, VPN, router port, or public IP is required. Tailscale is needed only on this computer and the tunnel is removed when sharing stops or PowerShellPlus exits."
            : "Switch to Global for browser-only access away from home. Direct router port-forwarding remains blocked.";
        TailscaleSetupButton.Visibility = Visibility.Visible;
        TailscaleSetupButton.Content = global ? "Open Tailscale setup on this PC" : "Set up browser-only Global access";
        LanModeButton.FontWeight = global ? FontWeights.Normal : FontWeights.Bold;
        GlobalModeButton.FontWeight = global ? FontWeights.Bold : FontWeights.Normal;
        LanModeButton.Opacity = global ? 0.65 : 1;
        GlobalModeButton.Opacity = global ? 1 : 0.65;
    }

    private void RefreshConnectionCount()
    {
        if (displayedMode != server.Mode)
        {
            BindAddresses();
            ApplyModePresentation();
        }
        var count = server.ConnectedClients;
        var devices = server.PairedDevices;
        var endpointLabel = server.Mode == RemoteAccessMode.Global
            ? $"{server.Addresses.Count} global endpoint{(server.Addresses.Count == 1 ? string.Empty : "s")}"
            : $"{server.Addresses.Count} adapter address{(server.Addresses.Count == 1 ? string.Empty : "es")}";
        ConnectionCountText.Text = $"{count} device{(count == 1 ? string.Empty : "s")} connected · {devices.Count} saved · {endpointLabel}";
        PairingCodeText.Text = server.PairingCode;
        AllowInputToggle.IsChecked = server.AllowInput;
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

    private async void LanModeClick(object sender, RoutedEventArgs e) => await ChangeModeAsync(RemoteAccessMode.Lan);
    private async void GlobalModeClick(object sender, RoutedEventArgs e) => await ChangeModeAsync(RemoteAccessMode.Global);

    private async Task ChangeModeAsync(RemoteAccessMode mode)
    {
        if (server.IsRunning && server.Mode == mode) return;
        if (mode == RemoteAccessMode.Global && MessageBox.Show(this,
                "Global mode creates an HTTPS address reachable from the internet. Terminal data still requires the one-time PowerShellPlus pairing code, and remote typing starts disabled.\n\nStart browser-only Global access?",
                "Start Global Remote", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ModeControls.IsEnabled = false;
        try
        {
            await switchMode(mode);
            pairedDevicesSignature = string.Empty;
            BindAddresses();
            ApplyModePresentation();
            RefreshConnectionCount();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"PowerShellPlus could not switch to {mode} mode. Your previous sharing mode was restored when possible.\n\n{exception.Message}",
                $"{mode} mode unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            BindAddresses();
            ApplyModePresentation();
        }
        finally { ModeControls.IsEnabled = true; }
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
        try { await stopSharing(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Remote Access stopped locally, but tunnel cleanup reported a problem.\n\n{exception.Message}",
                "Remote Access cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Close(); }
    }

    private void TailscaleSetupClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://tailscale.com/download/windows") { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open Tailscale setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DoneClick(object sender, RoutedEventArgs e) => Close();
}
