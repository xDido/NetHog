using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class MainWindow : Window
{
    private readonly NetworkDiscoveryService _discoveryService = new();
    private readonly ArpEnforcementService _enforcementService = new();
    private readonly ObservableCollection<NetworkDevice> _devices = new();
    private readonly ObservableCollection<NetworkAdapterOption> _adapters = new();
    private readonly Dictionary<string, List<TrafficSample>> _trafficSamples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceControlRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _ruleExpiry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _trafficAlertCooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTraffic> _previousTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceProfileStore _profileStore = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly SessionHistoryStore _sessionHistoryStore = new();
    private readonly LocalPcControlService _localPcControlService = new();
    private readonly RemoteControlCoordinator _remoteControlCoordinator = new();
    private readonly SemaphoreSlim _remoteReleaseGate = new(1, 1);
    private readonly UpdateService _updateService = new();
    private readonly NetworkProximityService _proximityService = new();
    private readonly DispatcherTimer _trafficTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly bool _startedWithWindows;
    private NetHogSettings _settings;
    private HistoryWindow? _historyWindow;
    private TrafficChartWindow? _trafficChartWindow;
    private DomainWindow? _domainWindow;
    private TrafficOverlayWindow? _trafficOverlayWindow;
    private DateTimeOffset _lastTrafficAt;
    private NetworkSnapshot? _snapshot;
    private EnforcementAvailability? _availability;
    private bool _scanStarted;
    private bool _bulkSelectionBatchInProgress;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _shutdownInProgress;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanTask;
    private bool _devicesWereVisible;
    private DeviceControlRule? _localRule;
    private DateTimeOffset _sessionStartedAt;
    private bool _sessionHistorySaved;
    private string? _controlSessionId;
    private IReadOnlyList<RemoteControlLease> _remoteControls = Array.Empty<RemoteControlLease>();
    private string? _selectedAdapterId;
    private string? _sampledInterfaceId;
    private long _previousInterfaceBytesReceived;
    private long _previousInterfaceBytesSent;
    private DateTimeOffset _lastInterfaceTrafficAt;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        try { _sessionHistoryStore.Prune(_settings.GetHistoryRetention()); }
        catch { /* history cleanup remains best effort and must not block startup */ }
        _startedWithWindows = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        try { WindowsStartupManager.SetEnabled(_settings.StartWithWindows); }
        catch { /* settings remain usable if Windows blocks the per-user Run key */ }

        UiMotion.RegisterButtonFeedback();
        App.ApplyTheme(_settings.DarkModeEnabled);
        InitializeComponent();
        App.ApplyTheme(_settings.DarkModeEnabled);
        AdapterComboBox.ItemsSource = _adapters;
        _trayIcon = CreateTrayIcon();
        DevicesGrid.ItemsSource = _devices;
        _enforcementService.SessionEndedUnexpectedly += EnforcementService_SessionEndedUnexpectedly;
        _remoteControlCoordinator.ActiveRemoteControlsChanged += RemoteControlCoordinator_ActiveRemoteControlsChanged;
        _remoteControlCoordinator.ReleaseRequested += RemoteControlCoordinator_ReleaseRequested;
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _trafficTimer.Tick += TrafficTimer_Tick;
        _trafficTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyResponsiveLayout(ActualWidth);
            if (_startedWithWindows && _settings.MinimizeToTrayOnClose) HideToTray();
            RefreshAdapterOptions();
            UpdateTrafficOverlayVisibility();
            try { await _remoteControlCoordinator.StartAsync(); }
            catch (Exception exception)
            {
                App.LogException(exception);
                SetStatus("LAN control status is unavailable on this PC. Network controls remain usable.");
            }
            await RefreshEnforcementStatusAsync();
            await RunScanAsync();
            if (_settings.AutomaticUpdatesEnabled) _ = CheckForUpdatesAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Startup could not finish: {exception.Message}");
            MessageBox.Show(this, exception.Message, "NetHog startup warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        TrafficLegendPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AdapterDiagnosticsText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
        ScanButton.IsEnabled = false;
        _domainWindow?.Close();
        _trafficChartWindow?.Close();
        _historyWindow?.Close();
        _trafficOverlayWindow?.Close();

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

            // Also remove a policy left by a prior forced termination. These
            // names are owned by NetHog and are safe to clear on full exit.
            await _localPcControlService.RemoveRuleAsync();
            _localRule = null;
            await _remoteControlCoordinator.StopAsync();
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
        try
        {
            await RunScanAsync();
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"Scan could not complete: {exception.Message}");
        }
    }

    private void RefreshAdapterOptions()
    {
        try
        {
            var options = _discoveryService.GetAdapters();
            _adapters.Clear();
            foreach (var option in options) _adapters.Add(option);

            var selected = _adapters.FirstOrDefault(option =>
                option.Id.Equals(_selectedAdapterId, StringComparison.OrdinalIgnoreCase))
                ?? _adapters.FirstOrDefault(option => option.IsDefaultRoute)
                ?? _adapters.FirstOrDefault();
            _selectedAdapterId = selected?.Id;
            ResetInterfaceRateSample();
            AdapterComboBox.SelectedItem = selected;
            AdapterDiagnosticsText.Text = selected?.DiagnosticSummary
                ?? "No connected Ethernet or Wi-Fi adapter with an IPv4 gateway was found.";
            AdapterComboBox.IsEnabled = !_enforcementService.IsSessionActive;
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            _adapters.Clear();
            _selectedAdapterId = null;
            AdapterDiagnosticsText.Text = exception.Message;
        }
    }

    private void AdapterComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterOption adapter) return;
        if (_enforcementService.IsSessionActive)
        {
            AdapterComboBox.SelectedItem = _adapters.FirstOrDefault(option =>
                option.Id.Equals(_snapshot?.InterfaceId, StringComparison.OrdinalIgnoreCase));
            return;
        }

        _selectedAdapterId = adapter.Id;
        ResetInterfaceRateSample();
        AdapterDiagnosticsText.Text = adapter.DiagnosticSummary;
        SetStatus($"Adapter selected: {adapter.Name}. Scan this network before starting a session.");
    }

    private async void ControlSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enforcementService.IsSessionActive)
        {
            try
            {
                await StopControlSessionAsync();
            }
            catch (Exception exception)
            {
                App.LogException(exception);
                SetStatus($"The session could not stop cleanly: {exception.Message}");
                RefreshSessionUi();
            }
            return;
        }

        if (_snapshot is null || OwnershipCheckBox.IsChecked != true) return;
        ControlSessionButton.IsEnabled = false;
        SetSessionStatus("Opening Npcap on this network adapter…");
        try
        {
            await _enforcementService.StartSessionAsync(_snapshot);
            _controlSessionId = Guid.NewGuid().ToString("N");
            _sessionStartedAt = DateTimeOffset.Now;
            _sessionHistorySaved = false;
            _trafficSamples.Clear();
            _trafficChartWindow?.UpdateSamples(CreateTrafficSampleSnapshot());
            SetSessionStatus("Session active. Select devices in the table to apply a temporary rule to several devices.");
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

    private async void EmergencyRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "NetHog will stop the active control session, restore ARP entries, and remove its current-PC firewall/QoS policies. Continue?",
            "Restore network",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        EmergencyRestoreButton.IsEnabled = false;
        try
        {
            if (_enforcementService.IsSessionActive) await StopControlSessionAsync();
            await _localPcControlService.RemoveRuleAsync();
            _localRule = null;
            SetStatus("Emergency restore completed. NetHog removed its controls and restored the regular network path.");
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"Emergency restore reported: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Network restore warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            App.ApplyTheme(_settings.DarkModeEnabled);
            _trafficChartWindow?.RefreshTheme();
            UpdateTrafficOverlayVisibility();
            _trafficOverlayWindow?.UpdateConfiguration(
                _settings.OverlayPosition,
                _settings.OverlayOpacity,
                _settings.OverlayClickThrough,
                _settings.OverlayScreenName);
            UpdateTrafficOverlayRate();
            try { _sessionHistoryStore.Prune(_settings.GetHistoryRetention()); }
            catch (Exception exception) { SetStatus($"History cleanup could not be completed: {exception.Message}"); }
            foreach (var device in _devices)
            {
                device.SetRateUnit(_settings.RateUnit);
                device.SetDataUnit(_settings.DataUnit);
            }
            SetStatus("Settings saved.");
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Activate();
            return;
        }

        try
        {
            var historyWindow = new HistoryWindow(
                _sessionHistoryStore,
                _settings.DataUnit,
                _settings.GetHistoryRetention())
            {
                Owner = this
            };
            _historyWindow = historyWindow;
            historyWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_historyWindow, historyWindow)) _historyWindow = null;
            };
            historyWindow.Show();
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"Session history could not open: {exception.Message}");
            MessageBox.Show(this, exception.Message, "NetHog history warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TrafficChartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_trafficChartWindow is { IsVisible: true })
        {
            _trafficChartWindow.Activate();
            return;
        }

        var chartWindow = new TrafficChartWindow { Owner = this };
        _trafficChartWindow = chartWindow;
        chartWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trafficChartWindow, chartWindow)) _trafficChartWindow = null;
        };
        chartWindow.UpdateDevices(_devices);
        chartWindow.UpdateSamples(CreateTrafficSampleSnapshot());
        chartWindow.Show();
    }

    private void DomainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_domainWindow is { IsVisible: true })
        {
            _domainWindow.Activate();
            return;
        }

        var domainWindow = new DomainWindow(_enforcementService, _devices) { Owner = this };
        _domainWindow = domainWindow;
        domainWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_domainWindow, domainWindow)) _domainWindow = null;
        };
        domainWindow.Show();
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

    public void ActivateFromExternalLaunch()
    {
        if (_shutdownInProgress) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
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
        if (sender is not FrameworkElement { DataContext: NetworkDevice device }) return;
        if (!device.IsLocalDevice && !_enforcementService.IsSessionActive) return;
        if (device.IsLocalDevice && _availability?.IsAdministrator != true) return;

        var existingRule = device.IsLocalDevice
            ? _localRule
            : _rules.GetValueOrDefault(device.MacAddress);
        var dialog = new DeviceControlWindow(device, existingRule) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.RemoveRequested)
            {
                CancelRuleExpiry(device.MacAddress);
                if (device.IsLocalDevice)
                {
                    await _localPcControlService.RemoveRuleAsync();
                    _localRule = null;
                }
                else
                {
                    await _enforcementService.RemoveRuleAsync(device.MacAddress);
                    _rules.Remove(device.MacAddress);
                    await _remoteControlCoordinator.RemoveControlAsync(device.MacAddress, _controlSessionId);
                }
                SetDeviceRuleState(device, null);
                SetStatus($"Controls removed for {device.DisplayName}. Its regular routed IPv4 access is restored.");
            }
            else if (dialog.AppliedRule is { } rule)
            {
                if (device.IsLocalDevice)
                {
                    await _localPcControlService.ApplyRuleAsync(rule);
                    _localRule = rule;
                }
                else
                {
                    await _enforcementService.ApplyRuleAsync(rule);
                    _rules[device.MacAddress] = rule;
                    await _remoteControlCoordinator.PublishControlAsync(device, rule, _controlSessionId ?? string.Empty);
                }
                CancelRuleExpiry(device.MacAddress);
                SetDeviceRuleState(device, rule);
                ScheduleRuleExpiry(device, rule);
                SetStatus(device.IsLocalDevice
                    ? FormatRuleExpiryStatus("Current PC controls applied through Windows local policy.", rule)
                    : FormatRuleExpiryStatus($"Controls applied to {device.DisplayName} for this session.", rule));
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Could not apply controls: {exception.Message}");
            MessageBox.Show(this, exception.Message, "NetHog couldn't apply the controls", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshSessionUi();

    private void BulkDeviceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bulkSelectionBatchInProgress) RefreshSessionUi();
    }

    private void SelectAllDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        _bulkSelectionBatchInProgress = true;
        try
        {
            foreach (var device in _devices.Where(device => !device.IsLocalDevice && device.CanConfigure))
            {
                device.IsBulkSelected = true;
            }
        }
        finally
        {
            _bulkSelectionBatchInProgress = false;
        }

        RefreshSessionUi();
    }

    private void ClearDeviceSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _bulkSelectionBatchInProgress = true;
        try
        {
            foreach (var device in _devices.Where(device => device.IsBulkSelected))
            {
                device.IsBulkSelected = false;
            }
        }
        finally
        {
            _bulkSelectionBatchInProgress = false;
        }

        RefreshSessionUi();
    }

    private async void BulkControlsButton_Click(object sender, RoutedEventArgs e)
    {
        var devices = _devices
            .Where(device => device.IsBulkSelected)
            .Where(device => !device.IsLocalDevice && device.CanConfigure)
            .ToArray();
        if (devices.Length == 0)
        {
            SetStatus("Select one or more peer devices using the checkboxes in the Select column.");
            return;
        }

        var dialog = new DeviceControlWindow(devices, existingRule: null) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            BulkControlsButton.IsEnabled = false;
            if (dialog.RemoveRequested)
            {
                foreach (var device in devices)
                {
                    CancelRuleExpiry(device.MacAddress);
                    await _enforcementService.RemoveRuleAsync(device.MacAddress);
                    _rules.Remove(device.MacAddress);
                    await _remoteControlCoordinator.RemoveControlAsync(device.MacAddress, _controlSessionId);
                    SetDeviceRuleState(device, null);
                }

                SetStatus($"Controls removed from {devices.Length} selected devices.");
                return;
            }

            if (dialog.AppliedRule is not { } template) return;
            foreach (var device in devices)
            {
                var rule = template with { DeviceMacAddress = device.MacAddress };
                await _enforcementService.ApplyRuleAsync(rule);
                _rules[device.MacAddress] = rule;
                await _remoteControlCoordinator.PublishControlAsync(device, rule, _controlSessionId ?? string.Empty);
                CancelRuleExpiry(device.MacAddress);
                SetDeviceRuleState(device, rule);
                ScheduleRuleExpiry(device, rule);
            }

            SetStatus($"{FormatRule(template)} applied to {devices.Length} selected devices.");
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"Could not apply controls to every selected device: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Bulk controls warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshSessionUi();
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
        UpdateTrafficOverlayRate();
        if (!_enforcementService.IsSessionActive)
        {
            _previousTraffic.Clear();
            SetTrafficInactive();
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
            device.SetDataUnit(_settings.DataUnit);
            device.SetTraffic(sample.DownloadBytes, sample.UploadBytes, downloadRate, uploadRate);
            CheckTrafficAlert(device, downloadRate + uploadRate);
            _previousTraffic[device.MacAddress] = sample;
            downloadTotalBytes += sample.DownloadBytes;
            uploadTotalBytes += sample.UploadBytes;
            downloadRateTotal += downloadRate;
            uploadRateTotal += uploadRate;
        }

        RecordTrafficSamples(now, traffic);

        TrafficDataText.Visibility = Visibility.Collapsed;
        TrafficDataMetricsPanel.Visibility = Visibility.Visible;
        TrafficDownloadText.Text = FormatRate(downloadRateTotal, _settings.RateUnit);
        TrafficUploadText.Text = FormatRate(uploadRateTotal, _settings.RateUnit);
        TrafficDataPanel.ToolTip = $"Current aggregate rate. Session totals: ↓ {FormatBytes(downloadTotalBytes, _settings.DataUnit)}  ·  ↑ {FormatBytes(uploadTotalBytes, _settings.DataUnit)}";
    }

    private void UpdateTrafficOverlayVisibility()
    {
        if (!_settings.ShowTrafficOverlay)
        {
            _trafficOverlayWindow?.Close();
            _trafficOverlayWindow = null;
            ResetInterfaceRateSample();
            return;
        }

        if (_trafficOverlayWindow is { IsVisible: true }) return;

        var overlay = new TrafficOverlayWindow(
            _settings.OverlayPosition,
            _settings.OverlayOpacity,
            _settings.OverlayClickThrough,
            _settings.OverlayScreenName);
        _trafficOverlayWindow = overlay;
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trafficOverlayWindow, overlay)) _trafficOverlayWindow = null;
        };
        overlay.Show();
        UpdateTrafficOverlayRate();
    }

    private void ResetInterfaceRateSample()
    {
        _sampledInterfaceId = null;
        _previousInterfaceBytesReceived = 0;
        _previousInterfaceBytesSent = 0;
        _lastInterfaceTrafficAt = default;
        _trafficOverlayWindow?.UpdateRates(null, null, _settings.RateUnit);
    }

    private void UpdateTrafficOverlayRate()
    {
        if (_trafficOverlayWindow is null) return;

        var interfaceId = _selectedAdapterId ?? _snapshot?.InterfaceId;
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            ResetInterfaceRateSample();
            return;
        }

        try
        {
            if (!string.Equals(_sampledInterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase))
            {
                ResetInterfaceRateSample();
                _sampledInterfaceId = interfaceId;
            }

            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(candidate => candidate.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
            if (networkInterface is null)
            {
                _trafficOverlayWindow.UpdateRates(null, null, _settings.RateUnit);
                return;
            }

            var statistics = networkInterface.GetIPStatistics();
            var now = DateTimeOffset.UtcNow;
            if (_lastInterfaceTrafficAt == default)
            {
                _previousInterfaceBytesReceived = statistics.BytesReceived;
                _previousInterfaceBytesSent = statistics.BytesSent;
                _lastInterfaceTrafficAt = now;
                _trafficOverlayWindow.UpdateRates(null, null, _settings.RateUnit);
                return;
            }

            var elapsedSeconds = Math.Max(0.1, (now - _lastInterfaceTrafficAt).TotalSeconds);
            var receivedDelta = Math.Max(0, statistics.BytesReceived - _previousInterfaceBytesReceived);
            var sentDelta = Math.Max(0, statistics.BytesSent - _previousInterfaceBytesSent);
            var downloadRate = receivedDelta * 8d / 1_000_000d / elapsedSeconds;
            var uploadRate = sentDelta * 8d / 1_000_000d / elapsedSeconds;
            _previousInterfaceBytesReceived = statistics.BytesReceived;
            _previousInterfaceBytesSent = statistics.BytesSent;
            _lastInterfaceTrafficAt = now;
            _trafficOverlayWindow.UpdateRates(downloadRate, uploadRate, _settings.RateUnit);
        }
        catch (Exception exception) when (exception is NetworkInformationException or InvalidOperationException)
        {
            ResetInterfaceRateSample();
        }
    }

    private void CheckTrafficAlert(NetworkDevice device, double combinedRateMbps)
    {
        if (!_settings.TrafficAlertsEnabled || combinedRateMbps < _settings.TrafficAlertThresholdMbps)
        {
            _trafficAlertCooldown.Remove(device.MacAddress);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_trafficAlertCooldown.TryGetValue(device.MacAddress, out var lastAlert)
            && now - lastAlert < TimeSpan.FromSeconds(30)) return;

        _trafficAlertCooldown[device.MacAddress] = now;
        try
        {
            _trayIcon.ShowBalloonTip(
                5_000,
                "NetHog traffic alert",
                $"{device.DisplayName} is using {FormatAlertRate(combinedRateMbps)} combined.",
                Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Balloon notifications are best effort when the notification area is unavailable.
        }
    }

    private string FormatAlertRate(double megabitsPerSecond) =>
        _settings.RateUnit == TrafficRateUnit.BytesPerSecond
            ? $"{megabitsPerSecond * 1_000_000d / 8d / 1_000_000d:0.0} MB/s"
            : megabitsPerSecond >= 1
                ? $"{megabitsPerSecond:0.0} Mbps"
                : $"{megabitsPerSecond * 1_000:0} Kbps";

    private void RecordTrafficSamples(
        DateTimeOffset at,
        IReadOnlyDictionary<string, DeviceTraffic> traffic)
    {
        foreach (var device in _devices)
        {
            if (!traffic.TryGetValue(device.MacAddress, out var sample)) continue;
            if (!_trafficSamples.TryGetValue(device.MacAddress, out var samples))
            {
                samples = new List<TrafficSample>();
                _trafficSamples[device.MacAddress] = samples;
            }

            samples.Add(new TrafficSample(
                at,
                sample.DownloadBytes,
                sample.UploadBytes,
                device.DownloadRateMbps,
                device.UploadRateMbps));
            if (samples.Count > 1_800) samples.RemoveRange(0, samples.Count - 1_800);
        }

        _trafficChartWindow?.UpdateDevices(_devices);
        _trafficChartWindow?.UpdateSamples(CreateTrafficSampleSnapshot());
    }

    private IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> CreateTrafficSampleSnapshot() =>
        _trafficSamples.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TrafficSample>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

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
            var snapshot = await _discoveryService.ScanAsync(_selectedAdapterId, scanCancellation.Token);
            _snapshot = snapshot;
            _remoteControlCoordinator.SetTargetIdentity(snapshot);
            _devices.Clear();
            _rules.Clear();
            var nicknames = _profileStore.LoadNicknames();
            foreach (var device in snapshot.Devices)
            {
                if (nicknames.TryGetValue(device.MacAddress, out var nickname)) device.Nickname = nickname;
                else if (device.HasSuggestedName) device.Nickname = device.SuggestedName;
                device.SetRateUnit(_settings.RateUnit);
                device.SetDataUnit(_settings.DataUnit);
                if (device.IsLocalDevice && _localRule is not null) SetDeviceRuleState(device, _localRule);
                device.CanConfigure = device.HasIpv4Address
                                      && (device.IsLocalDevice
                                          ? _availability?.IsAdministrator == true
                                          : _enforcementService.IsSessionActive && !device.IsLocalDevice);
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
            RefreshRemoteControlUi(_remoteControlCoordinator.GetActiveRemoteControls());
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
        ControlEngineText.SetResourceReference(TextBlock.ForegroundProperty,
            _availability.IsAvailable ? "AccentBrush" : "WarningBrush");
        RefreshSessionUi();
    }

    private void RefreshSessionUi()
    {
        var isActive = _enforcementService.IsSessionActive;
        var hasValidNetwork = _snapshot is not null
                              && !string.IsNullOrWhiteSpace(_snapshot.GatewayMacAddress)
                              && _snapshot.Devices.Any(device => !device.IsLocalDevice && device.HasIpv4Address);
        OwnershipCheckBox.IsEnabled = !isActive && !_scanStarted && (_availability?.IsAvailable == true) && hasValidNetwork;
        ControlSessionButtonText.Text = isActive ? "Stop control session" : "Start control session";
        ControlSessionButtonText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isActive ? "InkBrush" : "PrimaryButtonTextBrush");
        ControlSessionButton.Style = (Style)FindResource(isActive ? "QuietButtonStyle" : "PrimaryButtonStyle");
        ControlSessionButton.IsEnabled = isActive
            || (!_scanStarted && hasValidNetwork && _availability?.IsAvailable == true && OwnershipCheckBox.IsChecked == true);
        EmergencyRestoreButton.IsEnabled = isActive || _localRule is not null;
        var isReady = !_scanStarted && hasValidNetwork && _availability?.IsAvailable == true
                      && OwnershipCheckBox.IsChecked == true;
        UiMotion.SetSessionIndicator(SessionStatusDot, isActive || isReady);
        SessionCard.SetResourceReference(Border.BackgroundProperty,
            isActive ? "GoodSoftBrush" : "SurfaceBrush");
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
            device.CanConfigure = device.HasIpv4Address
                                  && (device.IsLocalDevice
                                      ? _availability?.IsAdministrator == true
                                      : isActive && !device.IsLocalDevice);
        }
        var selectedDeviceCount = _devices.Count(device => device.IsBulkSelected);
        var selectedPeerCount = _devices.Count(device =>
            device.IsBulkSelected && !device.IsLocalDevice && device.CanConfigure);
        var eligiblePeerCount = _devices.Count(device =>
            !device.IsLocalDevice && device.CanConfigure);
        BulkSelectionSummaryText.Text = selectedDeviceCount == 0
            ? "Select devices below to apply one control rule to several devices."
            : selectedDeviceCount == selectedPeerCount
                ? $"{selectedDeviceCount} device{(selectedDeviceCount == 1 ? string.Empty : "s")} selected for bulk controls."
                : $"{selectedDeviceCount} selected · {selectedPeerCount} eligible for bulk controls.";
        SelectAllDevicesButton.IsEnabled = isActive && eligiblePeerCount > 0 && selectedPeerCount < eligiblePeerCount;
        ClearDeviceSelectionButton.IsEnabled = selectedDeviceCount > 0;
        BulkControlsButton.Content = selectedPeerCount > 0
            ? $"Apply to {selectedPeerCount} selected"
            : "Apply to selected";
        BulkControlsButton.IsEnabled = isActive && selectedPeerCount > 0;
        ScanButton.IsEnabled = !_scanStarted && !isActive;
        EmptyScanButton.IsEnabled = !_scanStarted && !isActive;
    }

    private async Task StopControlSessionAsync()
    {
        ControlSessionButton.IsEnabled = false;
        SetSessionStatus("Restoring device ARP entries and stopping the capture…");
        var finalTraffic = _enforcementService.GetTrafficSnapshot();
        var shouldRecordHistory = _sessionStartedAt != default && !_sessionHistorySaved;
        try
        {
            await _enforcementService.StopSessionAsync();
        }
        finally
        {
            await _remoteControlCoordinator.RemoveAllControlsAsync();
            _controlSessionId = null;
            CancelAllRuleExpiries();
            if (shouldRecordHistory) SaveSessionHistory(finalTraffic);
        }
        _rules.Clear();
        foreach (var device in _devices)
        {
            if (!device.IsLocalDevice) SetDeviceRuleState(device, null);
            device.SetTraffic(0, 0);
        }
        _previousTraffic.Clear();
        _lastTrafficAt = default;
        SetTrafficInactive();
        OwnershipCheckBox.IsChecked = false;
        SetStatus("Control session stopped and saved to history. NetHog attempted ARP restoration; affected devices may take a moment to refresh.");
        RefreshSessionUi();
    }

    private void ScheduleRuleExpiry(NetworkDevice device, DeviceControlRule rule)
    {
        CancelRuleExpiry(device.MacAddress);
        if (rule.DurationMinutes is not > 0) return;

        var cancellation = new CancellationTokenSource();
        _ruleExpiry[device.MacAddress] = cancellation;
        _ = ExpireRuleAsync(device, rule, cancellation);
    }

    private async Task ExpireRuleAsync(
        NetworkDevice device,
        DeviceControlRule rule,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(rule.DurationMinutes!.Value), cancellation.Token);
            await Dispatcher.InvokeAsync(() => RemoveExpiredRuleAsync(device, rule)).Task.Unwrap();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            App.LogException(exception);
        }
        finally
        {
            if (_ruleExpiry.TryGetValue(device.MacAddress, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _ruleExpiry.Remove(device.MacAddress);
                cancellation.Dispose();
            }
        }
    }

    private async Task RemoveExpiredRuleAsync(NetworkDevice device, DeviceControlRule rule)
    {
        var stillApplied = device.IsLocalDevice
            ? _localRule == rule
            : _rules.TryGetValue(device.MacAddress, out var current) && current == rule;
        if (!stillApplied) return;

        if (device.IsLocalDevice)
        {
            await _localPcControlService.RemoveRuleAsync();
            _localRule = null;
        }
        else if (_enforcementService.IsSessionActive)
        {
            await _enforcementService.RemoveRuleAsync(device.MacAddress);
        }

        await _remoteControlCoordinator.RemoveControlAsync(device.MacAddress, _controlSessionId);
        _rules.Remove(device.MacAddress);

        SetDeviceRuleState(device, null);
        SetStatus($"Temporary controls expired for {device.DisplayName}. Its regular routed IPv4 access is restored.");
    }

    private void CancelRuleExpiry(string macAddress)
    {
        if (_ruleExpiry.Remove(macAddress, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelAllRuleExpiries()
    {
        foreach (var cancellation in _ruleExpiry.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _ruleExpiry.Clear();
    }

    private static string FormatRuleExpiryStatus(string message, DeviceControlRule rule) =>
        rule.DurationMinutes is > 0
            ? $"{message} Auto-removal in {FormatDuration(rule.DurationMinutes.Value)}."
            : $"{message} It will remain until the session stops.";

    private static string FormatDuration(int minutes) =>
        minutes >= 60 ? $"{minutes / 60} hour{(minutes / 60 == 1 ? string.Empty : "s")}" : $"{minutes} minutes";

    private void SaveSessionHistory(IReadOnlyDictionary<string, DeviceTraffic> traffic)
    {
        if (_sessionHistorySaved || _sessionStartedAt == default || _snapshot is null) return;

        var devices = _devices.Select(device =>
        {
            traffic.TryGetValue(device.MacAddress, out var sample);
            var controlSummary = device.IsLocalDevice
                ? (_localRule is null ? "No controls" : device.ControlSummary)
                : (_rules.TryGetValue(device.MacAddress, out var rule) ? FormatRule(rule) : "No controls");
            return new SessionDeviceHistory(
                device.MacAddress,
                device.DisplayName,
                device.IpAddress,
                sample?.DownloadBytes ?? 0,
                sample?.UploadBytes ?? 0,
                controlSummary);
        }).ToArray();

        try
        {
            _sessionHistoryStore.Add(new SessionHistoryRecord(
                Guid.NewGuid(),
                _sessionStartedAt,
                DateTimeOffset.Now,
                _snapshot.InterfaceName,
                _snapshot.LocalAddress,
                _snapshot.GatewayAddress,
                devices),
                _settings.GetHistoryRetention());
            _sessionHistorySaved = true;
            _sessionStartedAt = default;
        }
        catch (Exception exception)
        {
            SetStatus($"Session stopped, but history could not be saved: {exception.Message}");
        }
    }

    private static void SetDeviceRuleState(NetworkDevice device, DeviceControlRule? rule)
    {
        device.SetControlIndicators(rule);
        device.HasControl = rule is not null;
        if (rule is null)
        {
            device.ControlSummary = "No controls";
            return;
        }

        device.ControlSummary = FormatRule(rule);
    }

    private static string FormatRule(DeviceControlRule rule)
    {
        var parts = new List<string>();
        if (rule.BlockInternet) parts.Add("Blocked");
        if (rule.DownloadLimitMbps is { } download) parts.Add($"↓ {download} Mbps");
        if (rule.UploadLimitMbps is { } upload) parts.Add($"↑ {upload} Mbps");
        return string.Join(" · ", parts);
    }

    private void EnforcementService_SessionEndedUnexpectedly(string message)
    {
        _ = HandleUnexpectedSessionAsync(message);
    }

    private void RemoteControlCoordinator_ActiveRemoteControlsChanged(
        IReadOnlyList<RemoteControlLease> leases)
    {
        try
        {
            _ = Dispatcher.InvokeAsync(() => RefreshRemoteControlUi(leases));
        }
        catch (InvalidOperationException)
        {
            // The window may already be closing.
        }
    }

    private void RemoteControlCoordinator_ReleaseRequested(RemoteControlReleaseRequest request)
    {
        try
        {
            _ = Dispatcher.InvokeAsync(() => RemoveRemoteControlAsync(request)).Task.Unwrap();
        }
        catch (InvalidOperationException)
        {
            // The window may already be closing.
        }
    }

    private async Task RemoveRemoteControlAsync(RemoteControlReleaseRequest request)
    {
        await _remoteReleaseGate.WaitAsync();
        try
        {
            if (!_enforcementService.IsSessionActive
                || string.IsNullOrWhiteSpace(_controlSessionId)
                || !request.SessionId.Equals(_controlSessionId, StringComparison.OrdinalIgnoreCase)
                || !_rules.ContainsKey(request.TargetMacAddress))
            {
                return;
            }

            var device = _devices.FirstOrDefault(candidate =>
                candidate.MacAddress.Equals(request.TargetMacAddress, StringComparison.OrdinalIgnoreCase));
            await _enforcementService.RemoveRuleAsync(request.TargetMacAddress);
            _rules.Remove(request.TargetMacAddress);
            CancelRuleExpiry(request.TargetMacAddress);
            await _remoteControlCoordinator.RemoveControlAsync(request.TargetMacAddress, request.SessionId);
            if (device is not null) SetDeviceRuleState(device, null);
            SetStatus(device is null
                ? "Controls were removed by the controlled device."
                : $"Controls removed by {device.DisplayName}.");
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"A controlled device requested removal, but NetHog could not remove the rule: {exception.Message}");
        }
        finally
        {
            _remoteReleaseGate.Release();
        }
    }

    private async void RemoveRemoteControlsButton_Click(object sender, RoutedEventArgs e)
    {
        var leases = _remoteControls.ToArray();
        if (leases.Length == 0) return;

        RemoveRemoteControlsButton.IsEnabled = false;
        SetStatus("Removing controls from this PC…");
        try
        {
            var sent = false;
            foreach (var lease in leases)
            {
                sent |= await _remoteControlCoordinator.RequestReleaseAsync(lease);
            }

            SetStatus(sent
                ? "Remove command sent. Waiting for the controlling NetHog instance to release the network path…"
                : "The remove command could not be sent on the local network.");
            RemoveRemoteControlsButton.IsEnabled = _remoteControls.Count > 0;
        }
        catch (Exception exception)
        {
            App.LogException(exception);
            SetStatus($"Could not send the remove command: {exception.Message}");
            RemoveRemoteControlsButton.IsEnabled = true;
        }
    }

    private void RefreshRemoteControlUi(IReadOnlyList<RemoteControlLease> leases)
    {
        _remoteControls = leases
            .Where(lease => !lease.IsExpired)
            .OrderBy(lease => lease.ControllerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_remoteControls.Count == 0)
        {
            RemoteControlBanner.Visibility = Visibility.Collapsed;
            RemoveRemoteControlsButton.IsEnabled = false;
            return;
        }

        RemoteControlBanner.Visibility = Visibility.Visible;
        RemoveRemoteControlsButton.IsEnabled = true;
        RemoteControlSummaryText.Text = string.Join(
            "  ·  ",
            _remoteControls.Select(lease =>
            {
                var expiry = lease.ExpiresAtUtc is { } expires
                    ? $" until {expires.ToLocalTime():HH:mm}"
                    : string.Empty;
                return $"{lease.ControlSummary} by {lease.ControllerName}{expiry}";
            }));
    }

    private async Task HandleUnexpectedSessionAsync(string message)
    {
        try
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                SetSessionStatus(message);
                SetStatus(message);
                var finalTraffic = _enforcementService.GetTrafficSnapshot();
                try { await _enforcementService.StopSessionAsync(); }
                catch { /* best effort; the engine already attempted cleanup */ }
                await _remoteControlCoordinator.RemoveAllControlsAsync();
                _controlSessionId = null;
                SaveSessionHistory(finalTraffic);
                _rules.Clear();
                foreach (var device in _devices)
                {
                    if (!device.IsLocalDevice) SetDeviceRuleState(device, null);
                    device.SetTraffic(0, 0);
                }
                _previousTraffic.Clear();
                _lastTrafficAt = default;
                SetTrafficInactive();
                OwnershipCheckBox.IsChecked = false;
                RefreshSessionUi();
            }).Task.Unwrap();
        }
        catch (Exception exception)
        {
            App.LogException(exception);
        }
    }

    private void SetStatus(string message)
    {
        if (StatusText.Text == message) return;
        StatusText.Text = message;
        UiMotion.Flash(StatusText);
    }

    private void SetTrafficInactive()
    {
        TrafficDataMetricsPanel.Visibility = Visibility.Collapsed;
        TrafficDataText.Visibility = Visibility.Visible;
        TrafficDataText.Text = "Session not active";
        TrafficDataPanel.ToolTip = "Start a control session to observe the current aggregate rate.";
    }

    private void SetSessionStatus(string message)
    {
        if (SessionStatusText.Text == message) return;
        SessionStatusText.Text = message;
        UiMotion.Flash(SessionStatusText);
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckAsync();
            if (update is null || !IsVisible) return;
            var result = MessageBox.Show(
                this,
                $"{update.Name} is available. Open the release page to download the portable EXE or MSI installer?",
                "NetHog update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.ReleaseUrl)
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Update checks are optional and must never affect network controls.
        }
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
