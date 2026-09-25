using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using NetHog.Avalonia.Services;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly PortableNetworkDiscoveryService _discovery = new();
    private readonly PortableTrafficReader _trafficReader = new();
    private readonly ArpEnforcementService _enforcement = new();
    private readonly LocalPcControlService _localPcControlService = new();
    private readonly NetworkProximityService _proximity = new();
    private readonly RemoteControlCoordinator _remoteCoordinator = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly SessionHistoryStore _historyStore = new();
    private readonly DeviceProfileStore _profileStore = new();
    private readonly DispatcherTimer _trafficTimer;
    private readonly WindowNotificationManager _notificationManager;
    private readonly SemaphoreSlim _remoteReleaseGate = new(1, 1);
    private readonly UpdateService _updateService = new();
    private readonly Dictionary<string, List<TrafficSample>> _trafficSamples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTraffic> _previousDeviceTraffic =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _localControlledMacs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceControlRule> _activeRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _ruleExpiry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _trafficAlertCooldown = new(StringComparer.OrdinalIgnoreCase);
    private TrayIcon? _trayIcon;
    private TrafficOverlayWindow? _trafficOverlayWindow;
    private ChartDialog? _chartDialog;
    private HistoryDialog? _historyDialog;
    private DomainDialog? _domainDialog;
    private NetHogSettings _settings;
    private NetworkSnapshot? _snapshot;
    private NetworkAdapterOption? _selectedAdapter;
    private bool _isNetworkAdminConfirmed;
    private bool _isBusy;
    private bool _isSessionTransitioning;
    private EnforcementAvailability? _availability;
    private DeviceControlRule? _localRule;
    private long _previousReceived;
    private long _previousSent;
    private DateTimeOffset _previousTrafficAt;
    private DateTimeOffset _sessionStartedAt;
    private string? _controlSessionId;
    private IReadOnlyList<RemoteControlLease> _remoteControls = Array.Empty<RemoteControlLease>();
    private string _statusText = "Choose an adapter and scan this network.";
    private string _adapterDiagnosticsText = "No connected Ethernet or Wi-Fi adapter with an IPv4 gateway was found.";
    private string _availabilityText = "Checking packet capture availability…";
    private string _engineStatusText = "Checking";
    private string _trafficSummaryText = "Session not active";
    private string _trafficDownloadSummaryText = "—";
    private string _trafficUploadSummaryText = "—";
    private bool _hasTrafficMetrics;
    private bool _hasDeviceSort;
    private string _sessionStatusText = "Scan the network and confirm ownership to enable controls.";
    private string _selectionSummaryText = "Select devices below to apply one control rule to several devices.";
    private string _applySelectedText = "Apply to selected";
    private string _scanButtonText = "Scan network";
    private string _emptyTitle = "Scan your network";
    private string _emptyDescription = "NetHog probes the local IPv4 subnet. Quiet, sleeping, or isolated devices may not appear.";
    private bool _allowClose;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        App.ApplyTheme(_settings.DarkModeEnabled);
        _notificationManager = new WindowNotificationManager(this);
        DataContext = this;
        Devices.CollectionChanged += Devices_CollectionChanged;
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trafficTimer.Tick += TrafficTimer_Tick;
        _remoteCoordinator.ActiveRemoteControlsChanged += RemoteCoordinator_ActiveRemoteControlsChanged;
        _remoteCoordinator.ReleaseRequested += RemoteCoordinator_ReleaseRequested;
        _enforcement.SessionEndedUnexpectedly += Enforcement_SessionEndedUnexpectedly;
        _trayIcon = CreateTrayIcon();
        Opened += async (_, _) =>
        {
            WindowsTitleBarTheme.Apply(this, _settings.DarkModeEnabled);
            Activate();
            await InitializeAsync();
            if (Environment.GetCommandLineArgs().Any(argument =>
                    string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase))
                && _settings.MinimizeToTrayOnClose)
            {
                Hide();
            }
        };
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public ObservableCollection<NetworkAdapterOption> Adapters { get; } = new();
    public ObservableCollection<NetworkDevice> Devices { get; } = new();

    public NetworkAdapterOption? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (ReferenceEquals(_selectedAdapter, value)) return;
            _selectedAdapter = value;
            ResetTrafficSamples();
            AdapterDiagnosticsText = value?.DiagnosticSummary ?? "No connected adapter selected.";
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentNetworkText));
            OnPropertyChanged(nameof(SidebarNetworkName));
            OnPropertyChanged(nameof(SidebarNetworkAddress));
            RefreshCommandState();
        }
    }

    public bool IsNetworkAdminConfirmed
    {
        get => _isNetworkAdminConfirmed;
        set
        {
            if (_isNetworkAdminConfirmed == value) return;
            _isNetworkAdminConfirmed = value;
            OnPropertyChanged();
            RefreshCommandState();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string AdapterDiagnosticsText
    {
        get => _adapterDiagnosticsText;
        private set => SetField(ref _adapterDiagnosticsText, value);
    }

    public string AvailabilityText
    {
        get => _availabilityText;
        private set => SetField(ref _availabilityText, value);
    }

    public string EngineStatusText
    {
        get => _engineStatusText;
        private set => SetField(ref _engineStatusText, value);
    }

    public bool IsEngineReady => _availability?.IsAvailable == true;

    public string TrafficSummaryText
    {
        get => _trafficSummaryText;
        private set => SetField(ref _trafficSummaryText, value);
    }

    public string TrafficDownloadSummaryText
    {
        get => _trafficDownloadSummaryText;
        private set => SetField(ref _trafficDownloadSummaryText, value);
    }

    public string TrafficUploadSummaryText
    {
        get => _trafficUploadSummaryText;
        private set => SetField(ref _trafficUploadSummaryText, value);
    }

    public bool HasTrafficMetrics
    {
        get => _hasTrafficMetrics;
        private set => SetField(ref _hasTrafficMetrics, value);
    }

    public bool HasDeviceSort
    {
        get => _hasDeviceSort;
        private set => SetField(ref _hasDeviceSort, value);
    }

    public string SessionStatusText
    {
        get => _sessionStatusText;
        private set => SetField(ref _sessionStatusText, value);
    }

    public string SelectionSummaryText
    {
        get => _selectionSummaryText;
        private set => SetField(ref _selectionSummaryText, value);
    }

    public string ApplySelectedText
    {
        get => _applySelectedText;
        private set => SetField(ref _applySelectedText, value);
    }

    public string ScanButtonText
    {
        get => _scanButtonText;
        private set => SetField(ref _scanButtonText, value);
    }

    public string EmptyTitle
    {
        get => _emptyTitle;
        private set => SetField(ref _emptyTitle, value);
    }

    public string EmptyDescription
    {
        get => _emptyDescription;
        private set => SetField(ref _emptyDescription, value);
    }

    public string DeviceCountText => Devices.Count.ToString();
    public bool HasDevices => Devices.Count > 0;
    public bool HasNoDevices => Devices.Count == 0;
    public string CurrentNetworkText => _snapshot is null
        ? "No active network interface"
        : $"{_snapshot.InterfaceName} · {_snapshot.LocalAddress}";
    public string SidebarNetworkName => _snapshot?.InterfaceName ?? "Waiting for scan";
    public string SidebarNetworkAddress => _snapshot is null
        ? "Connect to Ethernet or Wi-Fi to begin"
        : $"{_snapshot.LocalAddress} · gateway {_snapshot.GatewayAddress}";
    public string SessionButtonText => _enforcement.IsSessionActive
        ? "Stop control session"
        : "Start control session";
    public bool IsControlSessionActive => _enforcement.IsSessionActive;
    public bool CanScan => !_isBusy && !_isSessionTransitioning && !_enforcement.IsSessionActive;
    public bool CanConfirmNetwork =>
        !_isBusy && !_isSessionTransitioning && !_enforcement.IsSessionActive &&
        _snapshot is not null;
    public bool CanStartControlSession =>
        !_isSessionTransitioning &&
        (_enforcement.IsSessionActive ||
         (CanConfirmNetwork &&
          _availability?.IsAvailable == true &&
          IsNetworkAdminConfirmed &&
          _snapshot is not null &&
          IsUsableMac(_snapshot.GatewayMacAddress) &&
          _snapshot.Devices.Any(device =>
              !device.IsLocalDevice && device.HasIpv4Address && IsUsableMac(device.MacAddress))));
    public bool CanApplySelected =>
        _enforcement.IsSessionActive &&
        Devices.Any(device => device.IsBulkSelected && device.CanConfigure && !device.IsLocalDevice);
    public bool CanRestoreNetwork => _enforcement.IsSessionActive || _localRule is not null;
    public bool CanToggleBulkSelection =>
        _enforcement.IsSessionActive && Devices.Any(device => device.CanConfigure && !device.IsLocalDevice);
    public bool? IsAllEligibleSelected
    {
        get
        {
            var eligible = Devices.Where(device => device.CanConfigure && !device.IsLocalDevice).ToArray();
            if (eligible.Length == 0) return false;
            var selected = eligible.Count(device => device.IsBulkSelected);
            return selected == 0 ? false : selected == eligible.Length ? true : null;
        }
        set
        {
            if (value is not { } selected) return;
            foreach (var device in Devices.Where(device => device.CanConfigure && !device.IsLocalDevice))
                device.IsBulkSelected = selected;
            SetStatus(selected
                ? $"{Devices.Count(device => device.IsBulkSelected)} eligible device(s) selected."
                : "Device selection cleared.");
            RefreshCommandState();
        }
    }
    public bool HasRemoteControls => _remoteControls.Any(lease => !lease.IsExpired);
    public string RemoteControlSummaryText => string.Join(
        "  ·  ",
        _remoteControls.Where(lease => !lease.IsExpired).Select(lease =>
        {
            var expiry = lease.ExpiresAtUtc is { } expires
                ? $" until {expires.ToLocalTime():HH:mm}"
                : string.Empty;
            return $"{lease.ControlSummary} by {lease.ControllerName}{expiry}";
        }));

    public new event PropertyChangedEventHandler? PropertyChanged;

    private async Task InitializeAsync()
    {
        try
        {
            try { _historyStore.Prune(_settings.GetHistoryRetention()); }
            catch (Exception exception) { SetStatus($"History cleanup could not be completed: {exception.Message}"); }
            RefreshAdapters();
            await RefreshAvailabilityAsync();
            await ScanAsync();
            try { await _remoteCoordinator.StartAsync(); }
            catch (Exception exception) { SetStatus($"LAN control visibility is unavailable: {exception.Message}"); }
            UpdateTrafficOverlayVisibility();
            _trafficTimer.Start();
            if (_settings.AutomaticUpdatesEnabled) _ = CheckForUpdatesAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Startup could not finish: {exception.Message}");
        }
    }

    private void RefreshAdapters()
    {
        var adapters = _discovery.GetAdapters();
        Adapters.Clear();
        foreach (var adapter in adapters) Adapters.Add(adapter);
        SelectedAdapter = Adapters.FirstOrDefault(adapter => adapter.IsDefaultRoute) ?? Adapters.FirstOrDefault();
        if (SelectedAdapter is null) SetStatus("No IPv4 adapter with a gateway was found. Connect to Ethernet or Wi-Fi and scan again.");
    }

    private async Task RefreshAvailabilityAsync()
    {
        var availability = await _enforcement.GetAvailabilityAsync();
        _availability = availability;
        EngineStatusText = availability.IsAvailable ? "Ready" : "Setup required";
        AvailabilityText = availability.Explanation;
        UpdateDeviceEligibility();
        RefreshCommandState();
    }

    private bool CanConfigureDevice(NetworkDevice device) =>
        device.HasIpv4Address && IsUsableMac(device.MacAddress) && (device.IsLocalDevice
            ? _localPcControlService.IsSupported && _availability?.IsAdministrator == true
            : _availability?.IsAvailable == true && _enforcement.IsSessionActive);

    private static bool IsUsableMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6
               && parts.All(part => byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out _))
               && byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var firstByte)
               && firstByte != 0
               && (firstByte & 1) == 0;
    }

    private string LocalControlUnavailableText => OperatingSystem.IsWindows()
        ? "Current-PC controls require Windows administrator permission."
        : "Current-PC controls require Linux root permission and tc for upload limits or nftables/iptables for blocking.";

    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (NetworkDevice device in e.OldItems)
            {
                device.PropertyChanged -= Device_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (NetworkDevice device in e.NewItems)
            {
                device.PropertyChanged += Device_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasNoDevices));
        RefreshCommandState();
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkDevice.IsBulkSelected)
            or nameof(NetworkDevice.CanConfigure))
        {
            RefreshCommandState();
        }
    }

    private void UpdateDeviceEligibility()
    {
        foreach (var device in Devices) device.CanConfigure = CanConfigureDevice(device);
        RefreshCommandState();
    }

    private async Task ScanAsync()
    {
        if (_isBusy || SelectedAdapter is null) return;
        _isBusy = true;
        ClearScanResults();
        ScanButtonText = "Scanning…";
        EmptyTitle = "Scanning your network";
        EmptyDescription = "NetHog is checking which IPv4 devices are visible from this PC.";
        RefreshCommandState();
        try
        {
            SetStatus($"Scanning {SelectedAdapter.Name} and reading its neighbor table…");
            _snapshot = await _discovery.ScanAsync(SelectedAdapter.Id);
            _remoteCoordinator.SetTargetIdentity(_snapshot);
            Devices.Clear();
            foreach (var device in _snapshot.Devices)
            {
                device.SetRateUnit(_settings.RateUnit);
                device.SetDataUnit(_settings.DataUnit);
                var nickname = _profileStore.LoadNicknames()
                    .FirstOrDefault(profile => profile.Key.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase)).Value;
                if (!string.IsNullOrWhiteSpace(nickname)) device.Nickname = nickname;
                device.CanConfigure = CanConfigureDevice(device);
                if (device.IsLocalDevice && _localRule is not null) SetLocalDeviceState(device, _localRule);
                Devices.Add(device);
            }
            await _proximity.MeasureAsync(Devices);
            ApplyRemoteControls();
            OnPropertyChanged(nameof(DeviceCountText));
            NotifyNetworkContextChanged();
            await RefreshAvailabilityAsync();
            if (Devices.Count == 0)
            {
                EmptyTitle = "No other devices are visible yet";
                EmptyDescription = "NetHog did not find any IPv4 neighbors on this scan. A device may be asleep, on a separate guest network, or hidden by client isolation.";
            }
            else
            {
                EmptyTitle = "No devices visible";
                EmptyDescription = "Scan again after devices have joined the network.";
            }
            SetStatus($"Scan complete at {_snapshot.ScannedAt:HH:mm}. {Devices.Count} device(s) visible.");
        }
        catch (Exception exception)
        {
            ClearScanResults();
            NotifyNetworkContextChanged();
            EmptyTitle = "Couldn’t scan this network";
            EmptyDescription = exception.Message;
            SetStatus($"Scan could not complete: {exception.Message}");
        }
        finally
        {
            RefreshOpenDataDialogs();
            _isBusy = false;
            ScanButtonText = "Scan network";
            RefreshCommandState();
        }
    }

    private void ClearScanResults()
    {
        _snapshot = null;
        Devices.Clear();
        _trafficSamples.Clear();
        ResetTrafficSamples();
        NotifyNetworkContextChanged();
        RefreshOpenDataDialogs();
    }

    private void RefreshOpenDataDialogs()
    {
        _domainDialog?.UpdateDevices(Devices);
        _chartDialog?.UpdateDevices(Devices);
    }

    private async void ScanButton_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Match the Windows layout when the window is snapped or placed on a
        // narrower monitor: preserve the device table and move secondary
        // context into the main content instead of allowing it to clip.
        var compact = width > 0 && width < 1100;
        RootLayout.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(236);
        Sidebar.IsVisible = !compact;
        SidebarAuthorityCard.IsVisible = !compact;
        CompactAuthorityCard.IsVisible = compact;
        MainContentGrid.Margin = compact
            ? new Thickness(16, 18, 16, 16)
            : new Thickness(32, 28, 32, 22);
        AdapterDiagnosticsBlock.IsVisible = !compact;
        TrafficLegendPanel.IsVisible = !compact;
        MetricsGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 210 : 260);
    }

    private void ResetTrafficSamples()
    {
        _previousReceived = 0;
        _previousSent = 0;
        _previousTrafficAt = default;
        _previousDeviceTraffic.Clear();
        HasTrafficMetrics = false;
        TrafficDownloadSummaryText = "—";
        TrafficUploadSummaryText = "—";
        TrafficSummaryText = _enforcement.IsSessionActive ? "Waiting for traffic…" : "Session not active";
        _trafficOverlayWindow?.UpdateRates(null, null, _settings.RateUnit);
    }

    private void AdapterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_enforcement.IsSessionActive)
        {
            SetStatus("Stop the active control session before switching adapters.");
            return;
        }
        if (SelectedAdapter is not null) SetStatus($"Adapter selected: {SelectedAdapter.Name}. Scan this network before starting a session.");
    }

    private void DevicesGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        if (e.Column.CanUserSort)
        {
            HasDeviceSort = true;
        }
    }

    private void ResetDeviceSort_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var column in DevicesGrid.Columns)
        {
            column.ClearSort();
        }

        HasDeviceSort = false;
        SetStatus("Device sorting reset.");
    }

    private async void ControlSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_enforcement.IsSessionActive)
        {
            await StopSessionAsync();
            return;
        }

        if (!IsNetworkAdminConfirmed)
        {
            SetStatus("Confirm that you administer this network before starting a control session.");
            return;
        }
        if (_snapshot is null)
        {
            SetStatus("Scan the selected network before starting a control session.");
            return;
        }

        _isSessionTransitioning = true;
        RefreshCommandState();
        try
        {
            SetStatus("Opening the packet-capture adapter and starting a temporary IPv4 session…");
            await _enforcement.StartSessionAsync(_snapshot);
            _sessionStartedAt = DateTimeOffset.Now;
            _controlSessionId = Guid.NewGuid().ToString("N");
            _trafficSamples.Clear();
            ResetTrafficSamples();
            EngineStatusText = "Active";
            UpdateDeviceEligibility();
            SetStatus("Session active. Select devices and apply a temporary rule.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            _isSessionTransitioning = false;
            RefreshCommandState();
        }
    }

    private async Task StopSessionAsync()
    {
        _isSessionTransitioning = true;
        RefreshCommandState();
        try
        {
            SetStatus("Stopping the session and restoring device ARP entries…");
            await _enforcement.StopSessionAsync();
            await _remoteCoordinator.RemoveAllControlsAsync();
            SaveCompletedSession();
            _controlSessionId = null;
            foreach (var device in Devices.Where(device => !device.IsLocalDevice))
            {
                _localControlledMacs.Remove(device.MacAddress);
                CancelRuleExpiry(device.MacAddress);
            }
            _activeRules.Clear();
            _previousDeviceTraffic.Clear();
            ResetTrafficSamples();
            EngineStatusText = "Ready";
            UpdateDeviceEligibility();
            foreach (var device in Devices)
            {
                if (device.IsLocalDevice && _localRule is not null)
                {
                    SetLocalDeviceState(device, _localRule);
                    continue;
                }
                device.HasControl = false;
                device.IsRemoteControl = false;
                device.SetControlIndicators(null);
                device.SetTraffic(0, 0);
                device.ControlSummary = "No controls";
            }
            ApplyRemoteControls();
            SetStatus("Control session stopped and network restoration attempted.");
        }
        catch (Exception exception)
        {
            SetStatus($"The session could not stop cleanly: {exception.Message}");
        }
        finally
        {
            _isSessionTransitioning = false;
            RefreshCommandState();
        }
    }

    private async void RestoreButton_Click(object? sender, RoutedEventArgs e)
    {
        var policyName = OperatingSystem.IsWindows()
            ? "Windows firewall/QoS policies"
            : "Linux traffic-control and firewall policies";
        var confirmed = await new ConfirmationDialog(
            "Restore network",
            $"NetHog will stop the active control session, restore ARP entries, and remove its current-PC {policyName}. Continue?",
            "Restore network").ShowDialog<bool>(this);
        if (!confirmed) return;

        if (_enforcement.IsSessionActive) await StopSessionAsync();
        else if (_localRule is not null)
        {
            try
            {
                await _localPcControlService.RemoveRuleAsync(_snapshot);
                _localRule = null;
                var localDevice = Devices.FirstOrDefault(device => device.IsLocalDevice);
                if (localDevice is not null)
                {
                    CancelRuleExpiry(localDevice.MacAddress);
                    SetLocalDeviceState(localDevice, null);
                }
                SetStatus("Current-PC controls removed. Its regular outbound access is restored.");
                RefreshCommandState();
            }
            catch (Exception exception)
            {
                SetStatus($"Current-PC controls could not be removed: {exception.Message}");
            }
        }
        else SetStatus("No active control session. The network is already in its normal NetHog state.");
    }

    private async void ApplySelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = Devices.Where(device => device.IsBulkSelected
                                               && device.CanConfigure
                                               && !device.IsLocalDevice).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Select at least one eligible device first.");
            return;
        }
        await ConfigureDevicesAsync(selected);
    }

    private async void SetControls_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not NetworkDevice device) return;
        if (!device.CanConfigure)
        {
            SetStatus(device.IsLocalDevice
                ? LocalControlUnavailableText
                : "This device cannot receive a peer control in the current scan.");
            return;
        }
        await ConfigureDevicesAsync([device]);
    }

    private void NicknameTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: NetworkDevice device }) return;
        _profileStore.SaveNickname(device.MacAddress, device.Nickname);
        SetStatus(string.IsNullOrWhiteSpace(device.Nickname)
            ? $"Nickname cleared for {device.MacAddress}."
            : $"Nickname saved for {device.MacAddress}.");
    }

    private async Task ConfigureDevicesAsync(IReadOnlyList<NetworkDevice> devices)
    {
        var isSingleLocalDevice = devices.Count == 1 && devices[0].IsLocalDevice;
        if (!isSingleLocalDevice && !_enforcement.IsSessionActive)
        {
            SetStatus("Start a control session before applying a device rule.");
            return;
        }
        if (isSingleLocalDevice && (!_localPcControlService.IsSupported || _availability?.IsAdministrator != true))
        {
            SetStatus(LocalControlUnavailableText);
            return;
        }

        var existingRule = isSingleLocalDevice
            ? _localRule
            : devices.Count == 1 && _activeRules.TryGetValue(devices[0].MacAddress, out var storedRule)
                ? storedRule
                : null;
        var dialog = new ControlDialog(
            devices.Count == 1 ? $"{devices[0].DisplayName}  ·  {devices[0].AddressSummary}" : $"{devices.Count} devices selected",
            isSingleLocalDevice,
            existingRule,
            string.Join("  ·  ", devices.Take(3).Select(device => device.MacAddress))
                + (devices.Count > 3 ? "  ·  …" : string.Empty));
        var rule = await dialog.ShowDialog<DeviceControlRule?>(this);
        if (dialog.RemoveRequested)
        {
            try
            {
                foreach (var device in devices)
                {
                    CancelRuleExpiry(device.MacAddress);
                    if (device.IsLocalDevice)
                    {
                        await _localPcControlService.RemoveRuleAsync(_snapshot);
                        _localRule = null;
                    }
                    else
                    {
                        await _enforcement.RemoveRuleAsync(device.MacAddress);
                        if (_controlSessionId is not null)
                        {
                            await _remoteCoordinator.RemoveControlAsync(device.MacAddress, _controlSessionId);
                        }
                        _activeRules.Remove(device.MacAddress);
                    }
                    _localControlledMacs.Remove(device.MacAddress);
                    device.SetControlIndicators(null);
                    device.HasControl = false;
                    device.IsRemoteControl = false;
                    device.ControlSummary = "No controls";
                }
                SetStatus($"Controls removed from {devices.Count} device(s).");
                RefreshCommandState();
            }
            catch (Exception exception)
            {
                SetStatus($"The controls could not be removed: {exception.Message}");
            }
            return;
        }
        if (rule is null) return;

        try
        {
            foreach (var device in devices)
            {
                var deviceRule = rule with { DeviceMacAddress = device.MacAddress };
                if (device.IsLocalDevice)
                {
                    await _localPcControlService.ApplyRuleAsync(deviceRule, _snapshot);
                    _localRule = deviceRule;
                }
                else
                {
                    await _enforcement.ApplyRuleAsync(deviceRule);
                    if (_controlSessionId is not null)
                    {
                        await _remoteCoordinator.PublishControlAsync(device, deviceRule, _controlSessionId);
                    }
                    _activeRules[device.MacAddress] = deviceRule;
                }
                device.SetControlIndicators(deviceRule);
                device.HasControl = true;
                device.IsRemoteControl = false;
                device.ControlSummary = BuildControlSummary(deviceRule);
                _localControlledMacs.Add(device.MacAddress);
                ScheduleRuleExpiry(device, deviceRule);
                device.IsBulkSelected = false;
            }
            SetStatus(FormatControlAppliedStatus(devices, rule));
            RefreshCommandState();
        }
        catch (Exception exception)
        {
            SetStatus($"The control could not be applied: {exception.Message}");
        }
    }

    private void DevicesNav_Click(object? sender, RoutedEventArgs e) => SetStatus("Devices is the active workspace.");
    private async void SettingsNav_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings);
        dialog.ApplyRequested += (_, settings) => ApplySettings(settings, "Settings applied locally.");
        try
        {
            var result = await dialog.ShowDialog<NetHogSettings?>(this);
            if (result is not null) ApplySettings(result, "Settings saved locally.");
        }
        catch (Exception exception)
        {
            SetStatus($"Settings could not open: {exception.Message}");
        }
    }

    private void ApplySettings(NetHogSettings settings, string statusMessage)
    {
        _settings = settings;
        _settingsStore.Save(_settings);
        try { _historyStore.Prune(_settings.GetHistoryRetention()); }
        catch (Exception exception) { SetStatus($"History cleanup could not be completed: {exception.Message}"); }
        App.ApplyTheme(_settings.DarkModeEnabled);
        _chartDialog?.RefreshTheme();
        _domainDialog?.RefreshTheme();
        try { DesktopStartupManager.SetEnabled(_settings.StartWithWindows); }
        catch (Exception exception) { SetStatus($"Startup setting could not be saved: {exception.Message}"); }
        foreach (var device in Devices)
        {
            device.SetRateUnit(_settings.RateUnit);
            device.SetDataUnit(_settings.DataUnit);
        }
        UpdateTrafficOverlayVisibility();
        _trafficOverlayWindow?.UpdateConfiguration(
            _settings.OverlayPosition,
            _settings.OverlayOpacity,
            _settings.OverlayClickThrough,
            _settings.OverlayScreenName);
        SetStatus(statusMessage);
    }

    private void HistoryNav_Click(object? sender, RoutedEventArgs e)
    {
        if (_historyDialog is { IsVisible: true })
        {
            _historyDialog.Activate();
            _historyDialog.Focus();
            return;
        }

        var history = new HistoryDialog(_settings.DataUnit, _settings.GetHistoryRetention());
        _historyDialog = history;
        history.Closed += (_, _) =>
        {
            if (ReferenceEquals(_historyDialog, history)) _historyDialog = null;
        };
        if (!ShowOwnedWindow(history, "Session history")) _historyDialog = null;
    }

    private void DomainsNav_Click(object? sender, RoutedEventArgs e)
    {
        if (_domainDialog is { IsVisible: true })
        {
            _domainDialog.Activate();
            _domainDialog.Focus();
            return;
        }

        var domain = new DomainDialog(_enforcement, Devices);
        _domainDialog = domain;
        domain.Closed += (_, _) =>
        {
            if (ReferenceEquals(_domainDialog, domain)) _domainDialog = null;
        };
        if (!ShowOwnedWindow(domain, "Domains")) _domainDialog = null;
    }

    private void ChartNav_Click(object? sender, RoutedEventArgs e)
    {
        if (_chartDialog is { IsVisible: true })
        {
            _chartDialog.Activate();
            _chartDialog.Focus();
            return;
        }
        var chart = new ChartDialog(Devices, CreateTrafficSampleSnapshot());
        _chartDialog = chart;
        chart.Closed += (_, _) =>
        {
            if (ReferenceEquals(_chartDialog, chart)) _chartDialog = null;
        };
        if (!ShowOwnedWindow(chart, "Live chart")) _chartDialog = null;
    }

    private bool ShowOwnedWindow(Window window, string displayName)
    {
        try
        {
            window.Show(this);
            window.Activate();
            window.Focus();
            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"{displayName} could not open: {exception.Message}");
            return false;
        }
    }

    private async void RemoveMyControl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not NetworkDevice device) return;
        var leases = _remoteControls.Where(lease =>
            lease.TargetMacAddress.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (leases.Length == 0)
        {
            SetStatus("No remote control is currently asserted on this device.");
            return;
        }

        foreach (var lease in leases) await _remoteCoordinator.RequestReleaseAsync(lease);
        SetStatus("Release sent from this machine. The controller will remove the asserted rule without approval.");
    }

    private async void RemoveRemoteControls_Click(object? sender, RoutedEventArgs e)
    {
        var leases = _remoteControls.Where(lease => !lease.IsExpired).ToArray();
        if (leases.Length == 0) return;

        SetStatus("Removing controls from this PC…");
        try
        {
            var sent = false;
            foreach (var lease in leases) sent |= await _remoteCoordinator.RequestReleaseAsync(lease);
            SetStatus(sent
                ? "Remove command sent. Waiting for the controlling NetHog instance to release the network path…"
                : "The remove command could not be sent on the local network.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not send the remove command: {exception.Message}");
        }
    }

    private void TrafficTimer_Tick(object? sender, EventArgs e)
    {
        var traffic = _trafficReader.Read(SelectedAdapter?.Name);
        var deviceTraffic = _enforcement.GetTrafficSnapshot();
        var now = DateTimeOffset.UtcNow;
        var deviceElapsed = _previousTrafficAt == default
            ? 0
            : Math.Max(0.1, (now - _previousTrafficAt).TotalSeconds);
        var totalDownloadRate = 0d;
        var totalUploadRate = 0d;
        foreach (var device in Devices)
        {
            if (deviceTraffic.TryGetValue(device.MacAddress, out var observed))
            {
                var downloadRate = 0d;
                var uploadRate = 0d;
                if (deviceElapsed > 0 && _previousDeviceTraffic.TryGetValue(device.MacAddress, out var previous))
                {
                    downloadRate = Math.Max(0, observed.DownloadBytes - previous.DownloadBytes) * 8d / 1_000_000d / deviceElapsed;
                    uploadRate = Math.Max(0, observed.UploadBytes - previous.UploadBytes) * 8d / 1_000_000d / deviceElapsed;
                }
                device.SetRateUnit(_settings.RateUnit);
                device.SetDataUnit(_settings.DataUnit);
                device.SetTraffic(observed.DownloadBytes, observed.UploadBytes, downloadRate, uploadRate);
                CheckTrafficAlert(device, downloadRate + uploadRate);
                totalDownloadRate += downloadRate;
                totalUploadRate += uploadRate;
                _previousDeviceTraffic[device.MacAddress] = observed;
            }
        }
        if (_previousTrafficAt != default)
        {
            var seconds = Math.Max(0.1, (now - _previousTrafficAt).TotalSeconds);
            var downloadMbps = Math.Max(0, traffic.DownloadBytes - _previousReceived) * 8d / seconds / 1_000_000d;
            var uploadMbps = Math.Max(0, traffic.UploadBytes - _previousSent) * 8d / seconds / 1_000_000d;
            _trafficOverlayWindow?.UpdateRates(downloadMbps, uploadMbps, _settings.RateUnit);
            if (_enforcement.IsSessionActive)
            {
                HasTrafficMetrics = true;
                TrafficDownloadSummaryText = FormatRate(totalDownloadRate, _settings.RateUnit);
                TrafficUploadSummaryText = FormatRate(totalUploadRate, _settings.RateUnit);
                TrafficSummaryText = $"↓ {TrafficDownloadSummaryText}  ·  ↑ {TrafficUploadSummaryText}";
                RecordTrafficSamples(now, deviceTraffic);
            }
            else
            {
                HasTrafficMetrics = false;
                TrafficSummaryText = "Session not active";
                _previousDeviceTraffic.Clear();
            }
        }
        else
        {
            HasTrafficMetrics = false;
            _trafficOverlayWindow?.UpdateRates(null, null, _settings.RateUnit);
            TrafficSummaryText = _enforcement.IsSessionActive ? "Waiting for traffic…" : "Session not active";
        }
        _previousReceived = traffic.DownloadBytes;
        _previousSent = traffic.UploadBytes;
        _previousTrafficAt = now;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trafficTimer.Stop();
        CloseTrafficOverlay();
        _chartDialog?.Close();
        _historyDialog?.Close();
        _domainDialog?.Close();
        if (_enforcement.IsSessionActive)
        {
            try
            {
                await _enforcement.StopSessionAsync();
                SaveCompletedSession();
            }
            catch { /* Process exit remains best effort. */ }
        }
        try { await _localPcControlService.RemoveRuleAsync(_snapshot); }
        catch { /* Local policy cleanup remains best effort during process exit. */ }
        _localRule = null;
        CancelAllRuleExpiries();
        try { await _remoteCoordinator.StopAsync(); }
        catch { /* LAN coordination is best effort during process exit. */ }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (!_exitRequested && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            SetStatus("NetHog is still running in the notification area.");
        }
    }

    private TrayIcon? CreateTrayIcon()
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://NetHog/Assets/NetHog.ico"));
            var tray = new TrayIcon
            {
                ToolTipText = "NetHog",
                Icon = new WindowIcon(iconStream),
                IsVisible = true,
                Menu = new NativeMenu()
            };
            var showItem = new NativeMenuItem("Show NetHog");
            showItem.Click += (_, _) => ShowFromTray();
            var exitItem = new NativeMenuItem("Exit NetHog");
            exitItem.Click += (_, _) => ExitFromTray();
            tray.Menu.Items.Add(showItem);
            tray.Menu.Items.Add(new NativeMenuItemSeparator());
            tray.Menu.Items.Add(exitItem);
            tray.Clicked += (_, _) => ShowFromTray();
            var icons = new TrayIcons { tray };
            TrayIcon.SetIcons(App.Current!, icons);
            return tray;
        }
        catch
        {
            // Some minimal Linux environments do not expose a tray host.
            return null;
        }
    }

    private void ShowFromTray()
    {
        Show();
        Activate();
        UpdateTrafficOverlayVisibility();
        RequestFreshScan();
    }

    public void ActivateFromExternalLaunch()
    {
        if (_allowClose) return;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        RequestFreshScan();
    }

    private void RequestFreshScan()
    {
        if (!_isBusy && !_enforcement.IsSessionActive) _ = ScanAsync();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _allowClose = true;
        Close();
    }

    private void RemoteCoordinator_ActiveRemoteControlsChanged(IReadOnlyList<RemoteControlLease> leases)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _remoteControls = leases;
            OnPropertyChanged(nameof(HasRemoteControls));
            OnPropertyChanged(nameof(RemoteControlSummaryText));
            ApplyRemoteControls();
        });
    }

    private void ApplyRemoteControls()
    {
        foreach (var device in Devices)
        {
            var lease = _remoteControls.FirstOrDefault(candidate =>
                candidate.TargetMacAddress.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase) &&
                !candidate.IsExpired);
            if (lease is not null)
            {
                device.HasControl = true;
                device.IsRemoteControl = true;
                device.ControlSummary = $"Remote · {lease.ControlSummary}";
            }
            else if (!_localControlledMacs.Contains(device.MacAddress))
            {
                device.HasControl = false;
                device.IsRemoteControl = false;
                device.ControlSummary = "No controls";
            }
            else
            {
                device.IsRemoteControl = false;
            }
        }
    }

    private void RemoteCoordinator_ReleaseRequested(RemoteControlReleaseRequest request)
    {
        Dispatcher.UIThread.Post(() => _ = RemoveRemoteControlAsync(request));
    }

    private async Task RemoveRemoteControlAsync(RemoteControlReleaseRequest request)
    {
        await _remoteReleaseGate.WaitAsync();
        try
        {
            if (!_enforcement.IsSessionActive
                || string.IsNullOrWhiteSpace(_controlSessionId)
                || !request.SessionId.Equals(_controlSessionId, StringComparison.OrdinalIgnoreCase)
                || !_activeRules.ContainsKey(request.TargetMacAddress))
            {
                return;
            }

            var device = Devices.FirstOrDefault(candidate =>
                candidate.MacAddress.Equals(request.TargetMacAddress, StringComparison.OrdinalIgnoreCase));
            await _enforcement.RemoveRuleAsync(request.TargetMacAddress);
            _activeRules.Remove(request.TargetMacAddress);
            CancelRuleExpiry(request.TargetMacAddress);
            await _remoteCoordinator.RemoveControlAsync(request.TargetMacAddress, request.SessionId);
            _localControlledMacs.Remove(request.TargetMacAddress);
            if (device is not null)
            {
                device.HasControl = false;
                device.IsRemoteControl = false;
                device.SetControlIndicators(null);
                device.ControlSummary = "No controls";
            }
            SetStatus(device is null
                ? "Controls were removed by the controlled device."
                : $"Controls removed by {device.DisplayName}.");
        }
        catch (Exception exception)
        {
            SetStatus($"The remote release could not complete: {exception.Message}");
        }
        finally
        {
            _remoteReleaseGate.Release();
        }
    }

    private void SetStatus(string text) => StatusText = text;

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(SessionButtonText));
        OnPropertyChanged(nameof(IsControlSessionActive));
        OnPropertyChanged(nameof(IsEngineReady));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanConfirmNetwork));
        OnPropertyChanged(nameof(CanStartControlSession));
        OnPropertyChanged(nameof(CanApplySelected));
        OnPropertyChanged(nameof(CanRestoreNetwork));
        OnPropertyChanged(nameof(CanToggleBulkSelection));
        OnPropertyChanged(nameof(IsAllEligibleSelected));
        RefreshSessionStatus();

        var selectedDeviceCount = Devices.Count(device => device.IsBulkSelected);
        var selectedPeerCount = Devices.Count(device =>
            device.IsBulkSelected && !device.IsLocalDevice && device.CanConfigure);
        SelectionSummaryText = selectedDeviceCount == 0
            ? "Select devices below to apply one control rule to several devices."
            : selectedDeviceCount == selectedPeerCount
                ? $"{selectedDeviceCount} device{(selectedDeviceCount == 1 ? string.Empty : "s")} selected for bulk controls."
                : $"{selectedDeviceCount} selected · {selectedPeerCount} eligible for bulk controls.";
        ApplySelectedText = selectedPeerCount > 0
            ? $"Apply to {selectedPeerCount} selected"
            : "Apply to selected";
    }

    private void RefreshSessionStatus()
    {
        if (_enforcement.IsSessionActive)
        {
            SessionStatusText = "Active on this network. Select a device and apply a temporary rule to route its traffic through this PC for reliable per-device rates. Uncontrolled Wi-Fi devices may bypass this PC.";
        }
        else if (_availability is { IsAvailable: false })
        {
            SessionStatusText = _availability.Explanation;
        }
        else if (_snapshot is null)
        {
            SessionStatusText = "Scan the network before starting a control session.";
        }
        else if (_snapshot.Devices.Count == 0)
        {
            SessionStatusText = "No visible devices to control. Scan again after other devices join the network.";
        }
        else if (string.IsNullOrWhiteSpace(_snapshot.GatewayMacAddress))
        {
            SessionStatusText = "The gateway MAC address was not found. Scan again after the network gateway responds.";
        }
        else if (IsNetworkAdminConfirmed)
        {
            SessionStatusText = "Ready. Applying a device rule temporarily redirects its IPv4 traffic through this PC.";
        }
        else
        {
            SessionStatusText = "Confirm that you administer this network to enable a control session.";
        }
    }

    private void NotifyNetworkContextChanged()
    {
        OnPropertyChanged(nameof(CurrentNetworkText));
        OnPropertyChanged(nameof(SidebarNetworkName));
        OnPropertyChanged(nameof(SidebarNetworkAddress));
    }

    private void Enforcement_SessionEndedUnexpectedly(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                SetStatus(message);
                try { await _enforcement.StopSessionAsync(); }
                catch { /* the enforcement engine already attempted cleanup */ }
                await _remoteCoordinator.RemoveAllControlsAsync();
                SaveCompletedSession();
                _controlSessionId = null;
                foreach (var device in Devices.Where(device => !device.IsLocalDevice))
                {
                    _localControlledMacs.Remove(device.MacAddress);
                    device.SetControlIndicators(null);
                    device.HasControl = false;
                    device.IsRemoteControl = false;
                    device.ControlSummary = "No controls";
                    device.SetTraffic(0, 0);
                    CancelRuleExpiry(device.MacAddress);
                }
                _activeRules.Clear();
                _previousDeviceTraffic.Clear();
                ResetTrafficSamples();
                EngineStatusText = "Ready";
                UpdateDeviceEligibility();
                ApplyRemoteControls();
                OnPropertyChanged(nameof(SessionButtonText));
            }
            catch (Exception exception)
            {
                SetStatus($"The control session ended unexpectedly: {exception.Message}");
            }
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckAsync();
            if (update is null || !IsVisible) return;
            var dialog = new UpdateDialog(update);
            var openRelease = await dialog.ShowDialog<bool>(this);
            if (openRelease)
            {
                Process.Start(new ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
            }
        }
        catch
        {
            // Update checks are optional and must never affect network controls.
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
        _notificationManager.Show(new Notification(
            "NetHog traffic alert",
            $"{device.DisplayName} is using {FormatAlertRate(combinedRateMbps)} combined.",
            NotificationType.Information,
            TimeSpan.FromSeconds(5)));
    }

    private string FormatAlertRate(double megabitsPerSecond) =>
        _settings.RateUnit == TrafficRateUnit.BytesPerSecond
            ? $"{megabitsPerSecond / 8d:0.0} MB/s"
            : megabitsPerSecond >= 1
                ? $"{megabitsPerSecond:0.0} Mbps"
                : $"{megabitsPerSecond * 1_000:0} Kbps";

    private void RecordTrafficSamples(
        DateTimeOffset at,
        IReadOnlyDictionary<string, DeviceTraffic> traffic)
    {
        foreach (var device in Devices)
        {
            if (!traffic.ContainsKey(device.MacAddress)) continue;
            if (!_trafficSamples.TryGetValue(device.MacAddress, out var samples))
            {
                samples = new List<TrafficSample>();
                _trafficSamples[device.MacAddress] = samples;
            }
            samples.Add(new TrafficSample(
                at,
                device.DownloadBytes,
                device.UploadBytes,
                device.DownloadRateMbps,
                device.UploadRateMbps));
            if (samples.Count > 1_800) samples.RemoveRange(0, samples.Count - 1_800);
        }

        _chartDialog?.UpdateDevices(Devices);
        _chartDialog?.UpdateSamples(CreateTrafficSampleSnapshot());
    }

    private IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> CreateTrafficSampleSnapshot() =>
        _trafficSamples.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TrafficSample>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

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
            if (cancellation.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(async () =>
            {
                try { await RemoveExpiredRuleAsync(device, rule); }
                catch (Exception exception) { SetStatus($"Automatic control removal failed: {exception.Message}"); }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
            : _activeRules.TryGetValue(device.MacAddress, out var current) && current == rule;
        if (!stillApplied) return;

        if (device.IsLocalDevice)
        {
            await _localPcControlService.RemoveRuleAsync(_snapshot);
            _localRule = null;
        }
        else
        {
            if (_enforcement.IsSessionActive) await _enforcement.RemoveRuleAsync(device.MacAddress);
            if (_controlSessionId is not null)
                await _remoteCoordinator.RemoveControlAsync(device.MacAddress, _controlSessionId);
            _activeRules.Remove(device.MacAddress);
        }
        _localControlledMacs.Remove(device.MacAddress);
        SetLocalDeviceState(device, null);
        RefreshCommandState();
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

    private void UpdateTrafficOverlayVisibility()
    {
        if (!_settings.ShowTrafficOverlay)
        {
            CloseTrafficOverlay();
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
        // Keep the overlay in the same application process, but do not make it
        // an owned window: owned windows are hidden when the main window is
        // minimized or sent to the notification area.
        overlay.Show();
    }

    private void CloseTrafficOverlay()
    {
        var overlay = _trafficOverlayWindow;
        _trafficOverlayWindow = null;
        overlay?.Close();
    }

    private void SaveCompletedSession()
    {
        if (_snapshot is null || _sessionStartedAt == default) return;
        var endedAt = DateTimeOffset.Now;
        var devices = Devices.Select(device => new SessionDeviceHistory(
            device.MacAddress,
            device.DisplayName,
            device.IpAddress,
            device.DownloadBytes,
            device.UploadBytes,
            device.ControlSummary)).ToList();
        _historyStore.Add(new SessionHistoryRecord(
            Guid.NewGuid(),
            _sessionStartedAt,
            endedAt,
            _snapshot.InterfaceName,
            _snapshot.LocalAddress,
            _snapshot.GatewayAddress,
            devices),
            _settings.GetHistoryRetention());
        _sessionStartedAt = default;
    }

    private static string BuildControlSummary(DeviceControlRule rule)
    {
        var parts = new List<string>();
        if (rule.BlockInternet) parts.Add("Blocked");
        if (rule.DownloadLimitMbps is { } download) parts.Add($"↓ {download} Mbps");
        if (rule.UploadLimitMbps is { } upload) parts.Add($"↑ {upload} Mbps");
        return string.Join(" · ", parts);
    }

    private static string FormatControlAppliedStatus(
        IReadOnlyList<NetworkDevice> devices,
        DeviceControlRule rule)
    {
        var target = devices.Count == 1 && devices[0].IsLocalDevice
            ? OperatingSystem.IsWindows()
                ? "Current PC controls applied through Windows local policy."
                : "Current PC controls applied through Linux traffic-control and firewall policy."
            : $"Controls applied to {devices.Count} device{(devices.Count == 1 ? string.Empty : "s")} for this session.";
        var duration = rule.DurationMinutes is > 0
            ? $" Auto-removal in {FormatDuration(rule.DurationMinutes.Value)}."
            : " They will remain until the session stops.";
        return target + duration;
    }

    private static string FormatDuration(int minutes) => minutes >= 60
        ? $"{minutes / 60} hour{(minutes / 60 == 1 ? string.Empty : "s")}"
        : $"{minutes} minutes";

    private static void SetLocalDeviceState(NetworkDevice device, DeviceControlRule? rule)
    {
        device.SetControlIndicators(rule);
        device.HasControl = rule is not null;
        device.IsRemoteControl = false;
        device.ControlSummary = rule is null ? "No controls" : BuildControlSummary(rule);
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

        return megabitsPerSecond switch
        {
            >= 1_000 => $"{megabitsPerSecond / 1_000:0.00} Gbps",
            >= 1 => $"{megabitsPerSecond:0.00} Mbps",
            >= 0.001 => $"{megabitsPerSecond * 1_000:0.0} Kbps",
            _ => $"{megabitsPerSecond * 1_000_000:0} bps"
        };
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
