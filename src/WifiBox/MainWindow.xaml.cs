using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WifiBox.Models;
using WifiBox.Services;

namespace WifiBox;

public partial class MainWindow : Window
{
    private readonly INetworkDiscoveryService _discoveryService = new NetworkDiscoveryService();
    private readonly ArpEnforcementService _enforcementService = new();
    private readonly ObservableCollection<NetworkDevice> _devices = new();
    private readonly Dictionary<string, DeviceControlRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTraffic> _previousTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceProfileStore _profileStore = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly NetworkProximityService _proximityService = new();
    private readonly DispatcherTimer _trafficTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly bool _startedWithWindows;
    private WifiBoxSettings _settings;
    private DateTimeOffset _lastTrafficAt;
    private NetworkSnapshot? _snapshot;
    private EnforcementAvailability? _availability;
    private bool _scanStarted;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _shutdownInProgress;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanTask;
    private bool _devicesWereVisible;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _startedWithWindows = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        try { WindowsStartupManager.SetEnabled(_settings.StartWithWindows); }
        catch { /* settings remain usable if Windows blocks the per-user Run key */ }

        UiMotion.RegisterButtonFeedback();
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        DevicesGrid.ItemsSource = _devices;
        _enforcementService.SessionEndedUnexpectedly += EnforcementService_SessionEndedUnexpectedly;
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _trafficTimer.Tick += TrafficTimer_Tick;
        _trafficTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth);
        if (_startedWithWindows && _settings.MinimizeToTrayOnClose) HideToTray();
        await RefreshEnforcementStatusAsync();
        await RunScanAsync();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsInitialized) ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Windows Snap commonly gives this window about half of a 1366–1920px display.
        // Keep the device table usable by moving to a compact single-pane layout below 980px.
        var compact = width > 0 && width < 980;
        SidebarColumn.Width = compact ? new GridLength(0) : new GridLength(236);
        Sidebar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MainContentGrid.Margin = compact ? new Thickness(16, 18, 16, 16) : new Thickness(32, 28, 32, 22);
        NetworkContextColumn.Width = compact ? new GridLength(0) : new GridLength(220);
        TrafficSummaryColumn.Width = new GridLength(compact ? 210 : 260);
        NetworkContextPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_exitRequested && !_shutdownInProgress && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            SetStatus("NetHog is still running in the notification area.");
            return;
        }

        if (_shutdownInProgress)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _shutdownInProgress = true;
        _trafficTimer.Stop();
        ControlSessionButton.IsEnabled = false;

        try
        {
            _scanCancellation?.Cancel();
            if (_scanTask is not null)
            {
                try { await _scanTask; }
                catch (OperationCanceledException) { }
            }

            if (_enforcementService.IsSessionActive)
            {
                SetSessionStatus("Stopping the session and restoring device ARP entries…");
                await StopControlSessionAsync();
            }
        }
        catch (Exception exception)
        {
            // Shutdown still continues after best-effort cleanup. The service
            // closes its capture handle and restores ARP entries internally.
            SetStatus($"Shutdown cleanup reported: {exception.Message}");
        }
        finally
        {
            _allowClose = true;
            _shutdownInProgress = false;
            DisposeTrayIcon();
            Close();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await RunScanAsync();
    }

    private async void ControlSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enforcementService.IsSessionActive)
        {
            await StopControlSessionAsync();
            return;
        }

        if (_snapshot is null || OwnershipCheckBox.IsChecked != true) return;
        ControlSessionButton.IsEnabled = false;
        SetSessionStatus("Opening Npcap on this network adapter…");
        try
        {
            await _enforcementService.StartSessionAsync(_snapshot);
            SetSessionStatus("Session active. Choose Set controls on a device to apply a temporary rule.");
            SetStatus("Control session active. No client is affected until you apply a device rule.");
        }
        catch (Exception exception)
        {
            SetSessionStatus(exception.Message);
            SetStatus(exception.Message);
        }
        finally
        {
            RefreshSessionUi();
        }
    }

    private void OwnershipCheckBox_Changed(object sender, RoutedEventArgs e) => RefreshSessionUi();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsStore, _settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            foreach (var device in _devices) device.SetRateUnit(_settings.RateUnit);
            SetStatus("Settings saved.");
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var executablePath = Environment.ProcessPath;
        System.Drawing.Icon? icon = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            }
        }
        catch
        {
            // Use the system icon when the app is running from an unpackaged host.
        }
        var trayIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "NetHog",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show NetHog", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ShowFromTray();
        return trayIcon;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
    }

    private async void ConfigureDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_enforcementService.IsSessionActive
            || sender is not FrameworkElement { DataContext: NetworkDevice device }
            || device.IsLocalDevice) return;

        _rules.TryGetValue(device.MacAddress, out var existingRule);
        var dialog = new DeviceControlWindow(device, existingRule) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.RemoveRequested)
            {
                await _enforcementService.RemoveRuleAsync(device.MacAddress);
                _rules.Remove(device.MacAddress);
                SetDeviceRuleState(device, null);
                SetStatus($"Controls removed for {device.DisplayName}. Its regular routed IPv4 access is restored.");
            }
            else if (dialog.AppliedRule is { } rule)
            {
                await _enforcementService.ApplyRuleAsync(rule);
                _rules[device.MacAddress] = rule;
                SetDeviceRuleState(device, rule);
                SetStatus($"Controls applied to {device.DisplayName} for this session.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Could not apply controls: {exception.Message}");
            MessageBox.Show(this, exception.Message, "NetHog couldn't apply the controls", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NicknameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NetworkDevice device }) return;
        _profileStore.SaveNickname(device.MacAddress, device.Nickname);
        SetStatus(string.IsNullOrWhiteSpace(device.Nickname)
            ? $"Nickname cleared for {device.MacAddress}."
            : $"Nickname saved for {device.MacAddress}.");
    }

    private void TrafficTimer_Tick(object? sender, EventArgs e)
    {
        if (!_enforcementService.IsSessionActive)
        {
            _previousTraffic.Clear();
            TrafficDataText.Text = "Session not active";
            TrafficDataText.ToolTip = "Start a control session to observe the current aggregate rate.";
            return;
        }

        var traffic = _enforcementService.GetTrafficSnapshot();
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = _lastTrafficAt == default
            ? 0
            : Math.Max(0.1, (now - _lastTrafficAt).TotalSeconds);
        _lastTrafficAt = now;
        long downloadTotalBytes = 0;
        long uploadTotalBytes = 0;
        double downloadRateTotal = 0;
        double uploadRateTotal = 0;
        foreach (var device in _devices)
        {
            if (!traffic.TryGetValue(device.MacAddress, out var sample)) continue;
            var downloadRate = 0d;
            var uploadRate = 0d;
            if (elapsedSeconds > 0 && _previousTraffic.TryGetValue(device.MacAddress, out var previous))
            {
                downloadRate = Math.Max(0, sample.DownloadBytes - previous.DownloadBytes) * 8d / 1_000_000d / elapsedSeconds;
                uploadRate = Math.Max(0, sample.UploadBytes - previous.UploadBytes) * 8d / 1_000_000d / elapsedSeconds;
            }
            device.SetRateUnit(_settings.RateUnit);
            device.SetTraffic(sample.DownloadBytes, sample.UploadBytes, downloadRate, uploadRate);
            _previousTraffic[device.MacAddress] = sample;
            downloadTotalBytes += sample.DownloadBytes;
            uploadTotalBytes += sample.UploadBytes;
            downloadRateTotal += downloadRate;
            uploadRateTotal += uploadRate;
        }

        TrafficDataText.Text = $"↓ {FormatRate(downloadRateTotal, _settings.RateUnit)}  ·  ↑ {FormatRate(uploadRateTotal, _settings.RateUnit)}";
        TrafficDataText.ToolTip = $"Current aggregate rate. Session totals: ↓ {FormatBytes(downloadTotalBytes, _settings.DataUnit)}  ·  ↑ {FormatBytes(uploadTotalBytes, _settings.DataUnit)}";
    }

    private async Task RunScanAsync()
    {
        var task = ScanNetworkAsync();
        _scanTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_scanTask, task)) _scanTask = null;
        }
    }

    private async Task ScanNetworkAsync()
    {
        if (_scanStarted || _enforcementService.IsSessionActive) return;

        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        _scanStarted = true;
        ScanButton.IsEnabled = false;
        EmptyScanButton.IsEnabled = false;
        ScanButtonText.Text = "Scanning…";
        EmptyScanButton.Content = "Scanning…";
        SetStatus("Scanning the local IPv4 network. Large networks may take a few seconds.");
        EmptyTitle.Text = "Scanning your network";
        EmptyDescription.Text = "NetHog is checking which IPv4 devices are visible from this PC.";
        RefreshSessionUi();

        try
        {
            var snapshot = await _discoveryService.ScanAsync(scanCancellation.Token);
            _snapshot = snapshot;
            _devices.Clear();
            _rules.Clear();
            var nicknames = _profileStore.LoadNicknames();
            foreach (var device in snapshot.Devices)
            {
                if (nicknames.TryGetValue(device.MacAddress, out var nickname)) device.Nickname = nickname;
                else if (device.HasSuggestedName) device.Nickname = device.SuggestedName;
                device.CanConfigure = _enforcementService.IsSessionActive
                                      && device.HasIpv4Address
                                      && !device.IsLocalDevice;
                _devices.Add(device);
            }

            await _proximityService.MeasureAsync(_devices.ToArray(), scanCancellation.Token);

            DeviceCountText.Text = snapshot.Devices.Count.ToString();
            SidebarNetworkName.Text = snapshot.InterfaceName;
            SidebarNetworkAddress.Text = $"{snapshot.LocalAddress}  ·  gateway {snapshot.GatewayAddress}";
            NetworkAddressText.Text = $"{snapshot.InterfaceName}  ·  {snapshot.LocalAddress}";
            EnforcementDetails.Text = snapshot.HasIpv6
                ? "IPv4 and IPv6 traffic can appear in live rates. Controls affect only IPv4 traffic routed through this gateway."
                : "Live rates cover observed IPv4 traffic. Controls affect IPv4 traffic routed through this gateway.";

            if (snapshot.Devices.Count == 0)
            {
                EmptyTitle.Text = "No other devices are visible yet";
                EmptyDescription.Text =
                    "Windows did not find any IPv4 neighbors on this scan. A device may be asleep, " +
                    "on a separate guest network, or hidden by client isolation.";
            }
            else
            {
                EmptyTitle.Text = "No devices visible";
                EmptyDescription.Text = "Scan again after devices have joined the network.";
            }

            SetStatus(
                $"Scanned {snapshot.HostsProbed:N0} local IPv4 addresses at {snapshot.ScannedAt:HH:mm:ss}. " +
                $"{snapshot.Devices.Count} neighbor{(snapshot.Devices.Count == 1 ? string.Empty : "s")} visible. " +
                "Start a control session to observe live IPv4 and IPv6 traffic.");
            UpdateContentVisibility();
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            // Closing the window cancels discovery so no probe process remains behind.
        }
        catch (Exception exception)
        {
            _snapshot = null;
            EmptyTitle.Text = "Couldn't scan this network";
            EmptyDescription.Text = exception.Message;
            SetStatus(exception.Message);
            NetworkAddressText.Text = "No active network interface";
            UpdateContentVisibility();
        }
        finally
        {
            ScanButton.IsEnabled = true;
            EmptyScanButton.IsEnabled = true;
            ScanButtonText.Text = "Scan network";
            EmptyScanButton.Content = "Scan network";
            _scanStarted = false;
            if (ReferenceEquals(_scanCancellation, scanCancellation)) _scanCancellation = null;
            RefreshSessionUi();
        }
    }

    private void UpdateContentVisibility()
    {
        var hasDevices = _devices.Count > 0;
        if (hasDevices)
        {
            DevicesGrid.Visibility = Visibility.Visible;
            if (!_devicesWereVisible) UiMotion.Reveal(DevicesGrid, 4, 170);
        }
        else
        {
            DevicesGrid.Visibility = Visibility.Collapsed;
        }
        EmptyState.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
        _devicesWereVisible = hasDevices;
    }

    private async Task RefreshEnforcementStatusAsync()
    {
        _availability = await _enforcementService.GetAvailabilityAsync();
        ControlEngineText.Text = _availability.Status;
        ControlEngineText.Foreground = _availability.IsAvailable
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("WarningBrush");
        RefreshSessionUi();
    }

    private void RefreshSessionUi()
    {
        var isActive = _enforcementService.IsSessionActive;
        var hasValidNetwork = _snapshot is not null
                              && !string.IsNullOrWhiteSpace(_snapshot.GatewayMacAddress)
                              && _snapshot.Devices.Any(device => !device.IsLocalDevice && device.HasIpv4Address);
        OwnershipCheckBox.IsEnabled = !isActive && !_scanStarted && (_availability?.IsAvailable == true) && hasValidNetwork;
        ControlSessionButton.Content = isActive ? "Stop control session" : "Start control session";
        ControlSessionButton.Style = (Style)FindResource(isActive ? "QuietButtonStyle" : "PrimaryButtonStyle");
        ControlSessionButton.IsEnabled = isActive
            || (!_scanStarted && hasValidNetwork && _availability?.IsAvailable == true && OwnershipCheckBox.IsChecked == true);
        var isReady = !_scanStarted && hasValidNetwork && _availability?.IsAvailable == true
                      && OwnershipCheckBox.IsChecked == true;
        UiMotion.SetSessionIndicator(SessionStatusDot, isActive || isReady);
        SessionCard.Background = isActive
            ? (Brush)FindResource("GoodSoftBrush")
            : (Brush)FindResource("SurfaceBrush");
        string statusMessage;
        if (isActive)
        {
            statusMessage = "Active on this network. Rules affect selected devices until you stop the session or close NetHog.";
        }
        else if (_availability is { IsAvailable: false })
        {
            statusMessage = _availability.Explanation;
        }
        else if (_snapshot is null)
        {
            statusMessage = "Scan the network before starting a control session.";
        }
        else if (_snapshot.Devices.Count == 0)
        {
            statusMessage = "No visible devices to control. Scan again after other devices join the network.";
        }
        else if (string.IsNullOrWhiteSpace(_snapshot.GatewayMacAddress))
        {
            statusMessage = "The gateway MAC address was not found. Scan again after the network gateway responds.";
        }
        else if (OwnershipCheckBox.IsChecked == true)
        {
            statusMessage = "Ready. Applying a device rule temporarily redirects its IPv4 traffic through this PC.";
        }
        else
        {
            statusMessage = "Confirm that you administer this network to enable a control session.";
        }
        SetSessionStatus(statusMessage);

        foreach (var device in _devices)
        {
            device.CanConfigure = isActive && device.HasIpv4Address && !device.IsLocalDevice;
        }
        ScanButton.IsEnabled = !_scanStarted && !isActive;
        EmptyScanButton.IsEnabled = !_scanStarted && !isActive;
    }

    private async Task StopControlSessionAsync()
    {
        ControlSessionButton.IsEnabled = false;
        SetSessionStatus("Restoring device ARP entries and stopping the capture…");
        await _enforcementService.StopSessionAsync();
        _rules.Clear();
        foreach (var device in _devices)
        {
            SetDeviceRuleState(device, null);
            device.SetTraffic(0, 0);
        }
        _previousTraffic.Clear();
        _lastTrafficAt = default;
        TrafficDataText.Text = "Session not active";
        TrafficDataText.ToolTip = "Start a control session to observe the current aggregate rate.";
        OwnershipCheckBox.IsChecked = false;
        SetStatus("Control session stopped. NetHog attempted ARP restoration; affected devices may take a moment to refresh.");
        RefreshSessionUi();
    }

    private static void SetDeviceRuleState(NetworkDevice device, DeviceControlRule? rule)
    {
        device.HasControl = rule is not null;
        if (rule is null)
        {
            device.ControlSummary = "No controls";
            return;
        }

        var parts = new List<string>();
        if (rule.BlockInternet) parts.Add("Blocked");
        if (rule.DownloadLimitMbps is { } download) parts.Add($"↓ {download} Mbps");
        if (rule.UploadLimitMbps is { } upload) parts.Add($"↑ {upload} Mbps");
        device.ControlSummary = string.Join(" · ", parts);
    }

    private void EnforcementService_SessionEndedUnexpectedly(string message)
    {
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            SetSessionStatus(message);
            SetStatus(message);
            try { await _enforcementService.StopSessionAsync(); }
            catch { /* best effort; the engine already attempted cleanup */ }
            _rules.Clear();
            foreach (var device in _devices)
            {
                SetDeviceRuleState(device, null);
                device.SetTraffic(0, 0);
            }
            _previousTraffic.Clear();
            _lastTrafficAt = default;
            TrafficDataText.Text = "Session not active";
            TrafficDataText.ToolTip = "Start a control session to observe the current aggregate rate.";
            OwnershipCheckBox.IsChecked = false;
            RefreshSessionUi();
        }));
    }

    private void SetStatus(string message)
    {
        if (StatusText.Text == message) return;
        StatusText.Text = message;
        UiMotion.Flash(StatusText);
    }

    private void SetSessionStatus(string message)
    {
        if (SessionStatusText.Text == message) return;
        SessionStatusText.Text = message;
        UiMotion.Flash(SessionStatusText);
    }

    private static string FormatBytes(long bytes, TrafficDataUnit unit)
    {
        var divisor = unit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
        var kilobyte = divisor;
        var megabyte = divisor * divisor;
        var gigabyte = megabyte * divisor;
        var suffixes = unit == TrafficDataUnit.Binary
            ? (Kilobyte: "KiB", Megabyte: "MiB", Gigabyte: "GiB")
            : (Kilobyte: "KB", Megabyte: "MB", Gigabyte: "GB");

        if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {suffixes.Gigabyte}";
        if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {suffixes.Megabyte}";
        if (bytes >= kilobyte) return $"{bytes / kilobyte:0.0} {suffixes.Kilobyte}";
        return $"{bytes:N0} B";
    }

    private static string FormatRate(double megabitsPerSecond, TrafficRateUnit unit)
    {
        if (unit == TrafficRateUnit.BytesPerSecond)
        {
            var bytesPerSecond = megabitsPerSecond * 1_000_000d / 8d;
            if (bytesPerSecond >= 1_000_000_000) return $"{bytesPerSecond / 1_000_000_000d:0.00} GB/s";
            if (bytesPerSecond >= 1_000_000) return $"{bytesPerSecond / 1_000_000d:0.00} MB/s";
            if (bytesPerSecond >= 1_000) return $"{bytesPerSecond / 1_000d:0.0} KB/s";
            return $"{bytesPerSecond:0} B/s";
        }

        if (megabitsPerSecond >= 1_000) return $"{megabitsPerSecond / 1_000d:0.00} Gbps";
        if (megabitsPerSecond >= 1) return $"{megabitsPerSecond:0.00} Mbps";
        if (megabitsPerSecond >= 0.001) return $"{megabitsPerSecond * 1_000:0.0} Kbps";
        return $"{megabitsPerSecond * 1_000_000:0} bps";
    }
}
