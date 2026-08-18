using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PowerShellPlus.Native;

public partial class LanRemoteDialog : Window
{
    private readonly LanRemoteServer server;
    private readonly Func<RemoteAccessMode, Task> switchMode;
    private readonly Func<Task> stopSharing;
    private readonly Action<string>? tailscaleConnectionEstablished;
    private readonly DiscordRemoteWebhookClient discordWebhookClient = new();
    private readonly DiscordRemoteBotService? discordBotService;
    private readonly DispatcherTimer refreshTimer;
    private DiscordRemoteWebhookSettings discordSettings;
    private DiscordRemoteBotSettings discordBotSettings;
    private string pairedDevicesSignature = string.Empty;
    private RemoteAccessMode displayedMode;
    private bool displayedRunning;
    private RemoteAccessMode selectedMode;

    internal LanRemoteDialog(LanRemoteServer server, Func<RemoteAccessMode, Task> switchMode, Func<Task> stopSharing,
        Action<string>? tailscaleConnectionEstablished = null, DiscordRemoteBotService? discordBotService = null)
    {
        this.server = server;
        this.switchMode = switchMode;
        this.stopSharing = stopSharing;
        this.tailscaleConnectionEstablished = tailscaleConnectionEstablished;
        this.discordBotService = discordBotService;
        InitializeComponent();
        selectedMode = server.IsRunning ? server.Mode : RemoteAccessMode.Lan;
        displayedMode = selectedMode;
        discordSettings = DiscordRemoteWebhookStore.Load();
        DiscordWebhookUrlBox.Password = discordSettings.WebhookUrl;
        DiscordNotifyOnStartToggle.IsChecked = discordSettings.NotifyWhenSharingStarts;
        DiscordIncludePairingCodeToggle.IsChecked = discordSettings.IncludePairingCode;
        discordBotSettings = DiscordRemoteBotStore.Load();
        DiscordBotTokenBox.Password = discordBotSettings.BotToken;
        DiscordBotApplicationIdBox.Text = discordBotSettings.ApplicationId;
        DiscordBotGuildIdBox.Text = discordBotSettings.GuildId;
        DiscordBotChannelIdBox.Text = discordBotSettings.ChannelId;
        DiscordBotAllowedUsersBox.Text = string.Join(", ", discordBotSettings.AllowedUserIds);
        DiscordBotMessagesToggle.IsChecked = discordBotSettings.ReceiveChannelMessages;
        DiscordBotMirrorOutputToggle.IsChecked = discordBotSettings.MirrorTerminalOutput;
        DiscordBotPermissionToggle.IsChecked = discordBotSettings.AllowPermissionChanges;
        DiscordBotReconnectToggle.IsChecked = discordBotSettings.ReconnectOnStartup;
        DiscordBotPanel.IsEnabled = discordBotService is not null;
        BindAddresses();
        AllowInputToggle.IsChecked = server.AllowInput;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += (_, _) =>
        {
            RefreshConnectionCount();
            RefreshDiscordBotPresentation();
        };
        refreshTimer.Start();
        if (discordBotService is not null) discordBotService.StatusChanged += DiscordBotStatusChanged;
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            if (discordBotService is not null) discordBotService.StatusChanged -= DiscordBotStatusChanged;
        };
        ApplyModePresentation();
        RefreshConnectionCount();
        RefreshDiscordBotPresentation();
    }

    private void BindAddresses()
    {
        AddressList.ItemsSource = null;
        AddressList.ItemsSource = server.IsRunning ? server.Addresses : [];
        AddressList.SelectedIndex = server.IsRunning && server.Addresses.Count > 0 ? 0 : -1;
    }

    private void ApplyModePresentation()
    {
        displayedMode = server.IsRunning ? server.Mode : selectedMode;
        displayedRunning = server.IsRunning;
        var global = displayedMode == RemoteAccessMode.Global;
        Title = global ? "Remote Access — Global" : "Remote Access — LAN";
        ModeEyebrowText.Text = global ? "GLOBAL REMOTE" : "LAN REMOTE";
        ModeTitleText.Text = server.IsRunning
            ? global ? "Your terminals, from any browser" : "Your terminals, on this network"
            : global ? "Global sharing is ready to start" : "LAN sharing is ready to start";
        ModeDescriptionText.Text = server.IsRunning
            ? global
                ? "Open the HTTPS address in Safari, Chrome, or any modern browser. Nothing is installed on the phone; the connector runs only on this PC."
                : "Open the recommended address on your phone and pair it once. Every current session is mirrored live without moving or restarting its PowerShell process."
            : "Opening this window does not publish anything. Choose a mode, review access and Discord settings, then click Start Sharing.";
        AddressLabelText.Text = server.IsRunning
            ? global ? "GLOBAL HTTPS ADDRESS" : "LAN ADDRESS"
            : "ADDRESS APPEARS AFTER START";
        AddressHelpText.Text = global
            ? "This stable ts.net address works over cellular or any Wi-Fi. PowerShellPlus verifies public DNS, TLS, and the Funnel relay before listing it as ready. If a phone cached an earlier not-found response, toggle Airplane Mode once or fully reopen the browser."
            : "Wi-Fi or Ethernet with an internet gateway is listed first. Virtual adapters work only for devices attached to those networks.";
        SecurityTitleText.Text = global ? "Public URL, protected application" : "Trusted private network only";
        SecurityBodyText.Text = global
            ? "The URL is reachable from the internet, but terminal data requires PowerShellPlus pairing. Global mode uses a 12-digit one-time code, a Secure HttpOnly saved credential, a global attempt limit, origin checks, and encrypted HTTPS. Keep the code private."
            : "LAN mode uses local HTTP. Use it only on trusted Private Wi-Fi/Ethernet and never forward its port through your router.";
        ModeHelpText.Text = global
            ? "No phone app, VPN, router port, or public IP is required. PowerShellPlus removes its tunnel when sharing stops. If it had to connect Tailscale, it disconnects that connection too; an already-connected Tailscale is left alone."
            : "Switch to Global for browser-only access away from home. Direct router port-forwarding remains blocked.";
        TailscaleSetupButton.Visibility = Visibility.Visible;
        TailscaleSetupButton.Content = global ? "Install / update Tailscale on this PC" : "Set up browser-only Global access";
        LanModeButton.FontWeight = global ? FontWeights.Normal : FontWeights.Bold;
        GlobalModeButton.FontWeight = global ? FontWeights.Bold : FontWeights.Normal;
        LanModeButton.Opacity = global ? 0.65 : 1;
        GlobalModeButton.Opacity = global ? 1 : 0.65;
        ModeControls.IsEnabled = !server.IsRunning;
        StartSharingButton.Visibility = server.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        StopSharingButton.Visibility = server.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        CopyAddressButton.IsEnabled = server.IsRunning;
        OpenBrowserButton.IsEnabled = server.IsRunning;
        SendDiscordNowButton.IsEnabled = server.IsRunning && !string.IsNullOrWhiteSpace(discordSettings.WebhookUrl);
    }

    private void RefreshConnectionCount()
    {
        var effectiveMode = server.IsRunning ? server.Mode : selectedMode;
        if (displayedMode != effectiveMode || displayedRunning != server.IsRunning)
        {
            BindAddresses();
            ApplyModePresentation();
        }
        var count = server.ConnectedClients;
        var devices = server.PairedDevices;
        var endpointLabel = server.IsRunning && server.Mode == RemoteAccessMode.Global
            ? $"{server.Addresses.Count} global endpoint{(server.Addresses.Count == 1 ? string.Empty : "s")}"
            : server.IsRunning
                ? $"{server.Addresses.Count} adapter address{(server.Addresses.Count == 1 ? string.Empty : "es")}"
                : "sharing is off";
        ConnectionCountText.Text = server.IsRunning
            ? $"{count} device{(count == 1 ? string.Empty : "s")} connected · {devices.Count} saved · {endpointLabel}"
            : $"Not sharing · {devices.Count} saved device{(devices.Count == 1 ? string.Empty : "s")}";
        PairingCodeText.Text = server.IsRunning ? server.PairingCode : "—";
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
        ApplyModePresentation();
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

    private void LanModeClick(object sender, RoutedEventArgs e) => SelectMode(RemoteAccessMode.Lan);
    private void GlobalModeClick(object sender, RoutedEventArgs e) => SelectMode(RemoteAccessMode.Global);

    private void SelectMode(RemoteAccessMode mode)
    {
        if (server.IsRunning) return;
        selectedMode = mode;
        ApplyModePresentation();
    }

    private async void StartSharingClick(object sender, RoutedEventArgs e) => await StartSharingAsync(selectedMode);

    private async Task StartSharingAsync(RemoteAccessMode mode)
    {
        if (server.IsRunning) return;
        if (mode == RemoteAccessMode.Global && !PowerShellPlusDialog.Confirm(this,
                "Global mode creates an HTTPS address reachable from the internet. Terminal data still requires the one-time PowerShellPlus pairing code, and remote typing starts disabled.\n\nPowerShellPlus will connect Tailscale if needed. When sharing stops, it disconnects Tailscale only if it made that connection.\n\nStart browser-only Global access?",
                "Start Global Remote", PowerShellPlusDialogKind.Warning,
                "Start Global", "Not now", defaultToPrimary: false))
            return;
        SetSharingBusy(true, mode == RemoteAccessMode.Global ? "Starting Global sharing…" : "Starting LAN sharing…");
        try
        {
            await switchMode(mode);
            selectedMode = server.Mode;
            pairedDevicesSignature = string.Empty;
            BindAddresses();
            ApplyModePresentation();
            RefreshConnectionCount();
            await NotifyDiscordSharingStartedAsync(showSuccess: false);
        }
        catch (TailscaleNotInstalledException exception)
        {
            BindAddresses();
            ApplyModePresentation();
            var action = PowerShellPlusDialog.ShowActions(this,
                $"PowerShellPlus could not switch to Global mode. Your previous sharing mode was restored when possible.\n\n{exception.Message}\n\nThe installer is downloaded only from Tailscale's official package server and must pass Windows signature verification before it opens.",
                "Global mode needs Tailscale", PowerShellPlusDialogKind.Warning,
                "Download & install", "Open official page", "Not now");
            if (action == PowerShellPlusDialogResult.Primary) await DownloadAndOpenTailscaleAsync();
            else if (action == PowerShellPlusDialogResult.Secondary) OpenOfficialTailscaleDownloadPage();
        }
        catch (TailscaleLoginRequiredException exception)
        {
            BindAddresses();
            ApplyModePresentation();
            var action = PowerShellPlusDialog.ShowActions(this,
                $"PowerShellPlus found a healthy Tailscale installation, but this PC is logged out.\n\n{exception.Message}\n\nSign in now will open a one-time page on login.tailscale.com. Your password and identity-provider login never pass through PowerShellPlus.",
                "Tailscale sign-in required", PowerShellPlusDialogKind.Information,
                "Sign in now", null, "Not now");
            if (action == PowerShellPlusDialogResult.Primary)
                await SignInAndRetryGlobalAsync(exception.ExecutablePath);
        }
        catch (Exception exception)
        {
            PowerShellPlusDialog.ShowMessage(this,
                $"PowerShellPlus could not switch to {mode} mode. Your previous sharing mode was restored when possible.\n\n{exception.Message}",
                $"{mode} mode unavailable", PowerShellPlusDialogKind.Warning);
            BindAddresses();
            ApplyModePresentation();
        }
        finally { SetSharingBusy(false, "Start Sharing"); }
    }

    private async Task SignInAndRetryGlobalAsync(string executablePath)
    {
        TailscaleSetupButton.IsEnabled = false;
        TailscaleSetupButton.Content = "Waiting for browser sign-in…";
        try
        {
            await TailscaleLoginManager.SignInAsync(executablePath);
            tailscaleConnectionEstablished?.Invoke(executablePath);
            TailscaleSetupButton.Content = "Starting secure Global access…";
            await switchMode(RemoteAccessMode.Global);
            selectedMode = RemoteAccessMode.Global;
            pairedDevicesSignature = string.Empty;
            BindAddresses();
            ApplyModePresentation();
            RefreshConnectionCount();
            await NotifyDiscordSharingStartedAsync(showSuccess: false);
        }
        catch (Exception exception)
        {
            PowerShellPlusDialog.ShowMessage(this,
                $"PowerShellPlus could not complete Tailscale sign-in or start Global mode. Your previous sharing mode was restored when possible.\n\n{exception.Message}",
                "Tailscale sign-in incomplete", PowerShellPlusDialogKind.Warning);
            BindAddresses();
            ApplyModePresentation();
        }
        finally
        {
            TailscaleSetupButton.IsEnabled = true;
            ApplyModePresentation();
        }
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
            PowerShellPlusDialog.ShowMessage(this, $"PowerShellPlus could not remove this paired device.\n\n{exception.Message}",
                "Could not remove device", PowerShellPlusDialogKind.Error);
        }
        finally { button.IsEnabled = true; }
    }

    private async void StopSharingClick(object sender, RoutedEventArgs e)
    {
        SetSharingBusy(true, "Stopping…");
        try { await stopSharing(); }
        catch (Exception exception)
        {
            PowerShellPlusDialog.ShowMessage(this, $"Remote Access stopped locally, but tunnel cleanup reported a problem.\n\n{exception.Message}",
                "Remote Access cleanup", PowerShellPlusDialogKind.Warning);
        }
        finally
        {
            selectedMode = RemoteAccessMode.Lan;
            BindAddresses();
            RefreshConnectionCount();
            SetSharingBusy(false, "Start Sharing");
        }
    }

    private void SetSharingBusy(bool busy, string label)
    {
        ModeControls.IsEnabled = !busy && !server.IsRunning;
        StartSharingButton.IsEnabled = !busy;
        StartSharingButton.Content = label;
        StopSharingButton.IsEnabled = !busy;
    }

    private bool TryReadDiscordSettings(bool requireWebhook, out Uri? webhookUri)
    {
        webhookUri = null;
        var value = DiscordWebhookUrlBox.Password.Trim();
        Uri? parsed = null;
        if (value.Length > 0)
        {
            if (!DiscordRemoteWebhookClient.TryValidateWebhookUrl(value, out var validated, out var error))
            {
                DiscordStatusText.Text = error;
                DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
                return false;
            }
            parsed = validated;
        }
        if (requireWebhook && value.Length == 0)
        {
            DiscordStatusText.Text = "Paste a Discord webhook URL first.";
            DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
            return false;
        }
        discordSettings = new DiscordRemoteWebhookSettings
        {
            WebhookUrl = value,
            NotifyWhenSharingStarts = DiscordNotifyOnStartToggle.IsChecked == true,
            IncludePairingCode = DiscordIncludePairingCodeToggle.IsChecked == true
        };
        DiscordRemoteWebhookStore.Save(discordSettings);
        if (value.Length > 0) webhookUri = parsed;
        SendDiscordNowButton.IsEnabled = server.IsRunning && webhookUri is not null;
        return true;
    }

    private void SaveDiscordClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadDiscordSettings(requireWebhook: false, out _)) return;
        DiscordStatusText.Text = string.IsNullOrWhiteSpace(discordSettings.WebhookUrl)
            ? "Discord webhook removed."
            : "Discord webhook saved securely for this Windows user.";
        DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161));
    }

    private async void TestDiscordClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadDiscordSettings(requireWebhook: true, out var webhookUri) || webhookUri is null) return;
        await RunDiscordActionAsync(() => discordWebhookClient.SendTestAsync(webhookUri), "Test message sent to Discord.");
    }

    private async void SendDiscordNowClick(object sender, RoutedEventArgs e)
    {
        if (!server.IsRunning) return;
        if (!TryReadDiscordSettings(requireWebhook: true, out _)) return;
        await NotifyDiscordSharingStartedAsync(showSuccess: true);
    }

    private async Task NotifyDiscordSharingStartedAsync(bool showSuccess)
    {
        if (!TryReadDiscordSettings(requireWebhook: false, out var webhookUri) || webhookUri is null) return;
        if (!showSuccess && !discordSettings.NotifyWhenSharingStarts) return;
        var address = (AddressList.SelectedItem as LanRemoteAddress)?.Url ?? server.Urls.FirstOrDefault() ?? string.Empty;
        if (!server.IsRunning || string.IsNullOrWhiteSpace(address)) return;
        await RunDiscordActionAsync(() => discordWebhookClient.SendSharingStartedAsync(webhookUri, server.Mode, address,
            server.PairingCode, discordSettings.IncludePairingCode), showSuccess ? "Sharing details sent to Discord." : "Remote Access started and Discord was notified.");
    }

    private async Task RunDiscordActionAsync(Func<Task> action, string successMessage)
    {
        DiscordButtonsPanel.IsEnabled = false;
        DiscordStatusText.Text = "Contacting Discord…";
        DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 173, 200));
        try
        {
            await action();
            DiscordStatusText.Text = successMessage;
            DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161));
        }
        catch (Exception exception)
        {
            DiscordStatusText.Text = exception.Message;
            DiscordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
        }
        finally { DiscordButtonsPanel.IsEnabled = true; }
    }

    private void DiscordBotStatusChanged(object? sender, EventArgs e) => RefreshDiscordBotPresentation();

    private void RefreshDiscordBotPresentation()
    {
        if (discordBotService is null)
        {
            DiscordBotStatusText.Text = "Discord bot controls are unavailable in this test window.";
            DiscordBotConnectButton.IsEnabled = false;
            DiscordBotDisconnectButton.IsEnabled = false;
            return;
        }
        var running = discordBotService.IsRunning;
        var connected = discordBotService.IsConnected;
        DiscordBotStatusText.Text = discordBotService.StatusText;
        DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(connected
            ? System.Windows.Media.Color.FromRgb(166, 227, 161)
            : running ? System.Windows.Media.Color.FromRgb(249, 226, 175)
            : System.Windows.Media.Color.FromRgb(166, 173, 200));
        DiscordBotConnectButton.IsEnabled = !running;
        DiscordBotDisconnectButton.IsEnabled = running;
        DiscordBotFieldsPanel.IsEnabled = !running;
    }

    private DiscordRemoteBotSettings ReadDiscordBotSettings()
    {
        var allowedUsers = Regex.Split(DiscordBotAllowedUsersBox.Text, @"[,;\s]+")
            .Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        return new DiscordRemoteBotSettings
        {
            BotToken = DiscordBotTokenBox.Password.Trim(),
            ApplicationId = DiscordBotApplicationIdBox.Text.Trim(),
            GuildId = DiscordBotGuildIdBox.Text.Trim(),
            ChannelId = DiscordBotChannelIdBox.Text.Trim(),
            AllowedUserIds = allowedUsers,
            ReceiveChannelMessages = DiscordBotMessagesToggle.IsChecked == true,
            MirrorTerminalOutput = DiscordBotMirrorOutputToggle.IsChecked == true,
            AllowPermissionChanges = DiscordBotPermissionToggle.IsChecked == true,
            ReconnectOnStartup = DiscordBotReconnectToggle.IsChecked == true,
            SelectedTerminalId = discordBotSettings.SelectedTerminalId
        };
    }

    private bool TryReadDiscordBotSettings(bool requireComplete, out DiscordRemoteBotSettings value)
    {
        value = ReadDiscordBotSettings();
        if (requireComplete && !DiscordRemoteBotService.TryValidateSettings(value, out var error))
        {
            DiscordBotStatusText.Text = error;
            DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
            return false;
        }
        discordBotSettings = value;
        DiscordRemoteBotStore.Save(value);
        return true;
    }

    private void SaveDiscordBotClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadDiscordBotSettings(requireComplete: false, out _)) return;
        DiscordBotStatusText.Text = "Discord bot settings saved securely for this Windows user.";
        DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161));
    }

    private async void ConnectDiscordBotClick(object sender, RoutedEventArgs e)
    {
        if (discordBotService is null || !TryReadDiscordBotSettings(requireComplete: true, out var requested)) return;
        DiscordBotButtonsPanel.IsEnabled = false;
        DiscordBotFieldsPanel.IsEnabled = false;
        DiscordBotStatusText.Text = "Connecting and registering slash commands…";
        DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(137, 180, 250));
        try { await discordBotService.StartAsync(requested); }
        catch (Exception exception)
        {
            DiscordBotStatusText.Text = exception.GetBaseException().Message;
            DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
        }
        finally
        {
            DiscordBotButtonsPanel.IsEnabled = true;
            RefreshDiscordBotPresentation();
        }
    }

    private async void DisconnectDiscordBotClick(object sender, RoutedEventArgs e)
    {
        if (discordBotService is null) return;
        DiscordBotButtonsPanel.IsEnabled = false;
        try { await discordBotService.StopAsync(); }
        catch (Exception exception)
        {
            DiscordBotStatusText.Text = exception.GetBaseException().Message;
            DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
        }
        finally
        {
            DiscordBotButtonsPanel.IsEnabled = true;
            RefreshDiscordBotPresentation();
        }
    }

    private void OpenDiscordDeveloperPortalClick(object sender, RoutedEventArgs e)
        => OpenDiscordUri("https://discord.com/developers/applications");

    private void InviteDiscordBotClick(object sender, RoutedEventArgs e)
    {
        var applicationId = DiscordBotApplicationIdBox.Text.Trim();
        if (!DiscordRemoteBotStore.IsSnowflake(applicationId))
        {
            DiscordBotStatusText.Text = "Enter the Application ID before opening the bot invite.";
            DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
            return;
        }
        OpenDiscordUri($"https://discord.com/oauth2/authorize?client_id={applicationId}&permissions=68608&integration_type=0&scope=bot%20applications.commands");
    }

    private void OpenDiscordUri(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            DiscordBotStatusText.Text = exception.Message;
            DiscordBotStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));
        }
    }

    internal bool ExplicitStartContractPassesForTest()
        => !server.IsRunning && StartSharingButton.Visibility == Visibility.Visible
            && StopSharingButton.Visibility == Visibility.Collapsed
            && !CopyAddressButton.IsEnabled && !OpenBrowserButton.IsEnabled;

    private async void TailscaleSetupClick(object sender, RoutedEventArgs e) => await DownloadAndOpenTailscaleAsync();

    private async Task DownloadAndOpenTailscaleAsync()
    {
        TailscaleSetupButton.IsEnabled = false;
        TailscaleSetupButton.Content = "Contacting Tailscale's verified package server…";
        var progress = new Progress<double>(value =>
            TailscaleSetupButton.Content = $"Downloading verified installer… {value:P0}");
        try
        {
            var launch = await TailscaleInstaller.DownloadAndLaunchAsync(progress);
            PowerShellPlusDialog.ShowMessage(this,
                $"Windows opened {launch.FileName} after verifying its trusted signature and '{launch.Publisher}' publisher.\n\nFinish the installer, then choose GLOBAL again. If this PC is logged out, PowerShellPlus will open the official browser sign-in for you. Nothing needs to be installed on your phone.",
                "Tailscale installer opened", PowerShellPlusDialogKind.Success, "Got it");
        }
        catch (Exception exception)
        {
            var action = PowerShellPlusDialog.ShowActions(this,
                $"PowerShellPlus did not open an installer.\n\n{exception.Message}",
                "Could not verify Tailscale setup", PowerShellPlusDialogKind.Error,
                "Open official page", null, "Close");
            if (action == PowerShellPlusDialogResult.Primary) OpenOfficialTailscaleDownloadPage();
        }
        finally
        {
            TailscaleSetupButton.IsEnabled = true;
            ApplyModePresentation();
        }
    }

    private void OpenOfficialTailscaleDownloadPage()
    {
        try { Process.Start(new ProcessStartInfo(TailscaleInstaller.DownloadPageUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            PowerShellPlusDialog.ShowMessage(this, exception.Message, "Could not open Tailscale setup",
                PowerShellPlusDialogKind.Warning);
        }
    }

    private void DoneClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
