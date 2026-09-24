using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private readonly RemoteControlCoordinator _remoteCoordinator = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly SessionHistoryStore _historyStore = new();
    private readonly DeviceProfileStore _profileStore = new();
    private readonly DispatcherTimer _trafficTimer;
    private readonly List<TrafficSample> _trafficSamples = new();
    private readonly HashSet<string> _localControlledMacs = new(StringComparer.OrdinalIgnoreCase);
    private NetHogSettings _settings;
    private NetworkSnapshot? _snapshot;
    private NetworkAdapterOption? _selectedAdapter;
    private bool _isNetworkAdminConfirmed;
    private bool _isBusy;
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

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        DataContext = this;
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trafficTimer.Tick += TrafficTimer_Tick;
        _remoteCoordinator.ActiveRemoteControlsChanged += RemoteCoordinator_ActiveRemoteControlsChanged;
        _remoteCoordinator.ReleaseRequested += RemoteCoordinator_ReleaseRequested;
        Opened += async (_, _) => await InitializeAsync();
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
            AdapterDiagnosticsText = value?.DiagnosticSummary ?? "No connected adapter selected.";
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentNetworkText));
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

    public string TrafficSummaryText
    {
        get => _trafficSummaryText;
        private set => SetField(ref _trafficSummaryText, value);
    }

    public string DeviceCountText => Devices.Count.ToString();
    public string CurrentNetworkText => SelectedAdapter is null
        ? "No network selected"
        : $"{SelectedAdapter.Name} · {SelectedAdapter.LocalAddress}";
    public string SessionButtonText => _enforcement.IsSessionActive
        ? "Stop control session"
        : "Start control session";

    public new event PropertyChangedEventHandler? PropertyChanged;

    private async Task InitializeAsync()
    {
        try
        {
            RefreshAdapters();
            await RefreshAvailabilityAsync();
            await ScanAsync();
            try { await _remoteCoordinator.StartAsync(); }
            catch (Exception exception) { SetStatus($"LAN control visibility is unavailable: {exception.Message}"); }
            _trafficTimer.Start();
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
        EngineStatusText = availability.IsAvailable ? "Ready" : "Setup required";
        AvailabilityText = availability.Explanation;
        foreach (var device in Devices) device.CanConfigure = availability.IsAvailable && !device.IsLocalDevice && device.HasIpv4Address;
    }

    private async Task ScanAsync()
    {
        if (_isBusy || SelectedAdapter is null) return;
        _isBusy = true;
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
                device.CanConfigure = !_snapshot.LocalAddress.Equals(device.IpAddress, StringComparison.OrdinalIgnoreCase)
                    && device.HasIpv4Address;
                Devices.Add(device);
            }
            ApplyRemoteControls();
            OnPropertyChanged(nameof(DeviceCountText));
            OnPropertyChanged(nameof(CurrentNetworkText));
            await RefreshAvailabilityAsync();
            SetStatus($"Scan complete at {_snapshot.ScannedAt:HH:mm}. {Devices.Count} device(s) visible.");
        }
        catch (Exception exception)
        {
            SetStatus($"Scan could not complete: {exception.Message}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void ScanButton_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private void AdapterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_enforcement.IsSessionActive)
        {
            SetStatus("Stop the active control session before switching adapters.");
            return;
        }
        if (SelectedAdapter is not null) SetStatus($"Adapter selected: {SelectedAdapter.Name}. Scan this network before starting a session.");
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

        try
        {
            SetStatus("Opening the packet-capture adapter and starting a temporary IPv4 session…");
            await _enforcement.StartSessionAsync(_snapshot);
            _sessionStartedAt = DateTimeOffset.Now;
            _controlSessionId = Guid.NewGuid().ToString("N");
            _trafficSamples.Clear();
            EngineStatusText = "Active";
            SetStatus("Session active. Select devices and apply a temporary rule.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        OnPropertyChanged(nameof(SessionButtonText));
    }

    private async Task StopSessionAsync()
    {
        try
        {
            SetStatus("Stopping the session and restoring device ARP entries…");
            await _enforcement.StopSessionAsync();
            await _remoteCoordinator.RemoveAllControlsAsync();
            SaveCompletedSession();
            _controlSessionId = null;
            _localControlledMacs.Clear();
            EngineStatusText = "Ready";
            foreach (var device in Devices)
            {
                device.HasControl = false;
                device.SetControlIndicators(null);
                device.ControlSummary = "No controls";
            }
            ApplyRemoteControls();
            SetStatus("Control session stopped and network restoration attempted.");
        }
        catch (Exception exception)
        {
            SetStatus($"The session could not stop cleanly: {exception.Message}");
        }
        OnPropertyChanged(nameof(SessionButtonText));
    }

    private async void RestoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_enforcement.IsSessionActive) await StopSessionAsync();
        else SetStatus("No active control session. The network is already in its normal NetHog state.");
    }

    private void SelectAllButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var device in Devices.Where(device => device.CanConfigure)) device.IsBulkSelected = true;
        SetStatus($"{Devices.Count(device => device.IsBulkSelected)} device(s) selected.");
    }

    private void ClearSelectionButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var device in Devices) device.IsBulkSelected = false;
        SetStatus("Device selection cleared.");
    }

    private async void ApplySelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = Devices.Where(device => device.IsBulkSelected && device.CanConfigure).ToList();
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
            SetStatus("This device cannot receive a peer control in the current scan.");
            return;
        }
        await ConfigureDevicesAsync([device]);
    }

    private async Task ConfigureDevicesAsync(IReadOnlyList<NetworkDevice> devices)
    {
        if (!_enforcement.IsSessionActive)
        {
            SetStatus("Start a control session before applying a device rule.");
            return;
        }

        var dialog = new ControlDialog(devices.Count == 1 ? devices[0].DisplayName : $"{devices.Count} selected devices");
        var rule = await dialog.ShowDialog<DeviceControlRule?>(this);
        if (rule is null) return;

        try
        {
            foreach (var device in devices)
            {
                var deviceRule = rule with { DeviceMacAddress = device.MacAddress };
                await _enforcement.ApplyRuleAsync(deviceRule);
                if (_controlSessionId is not null)
                {
                    await _remoteCoordinator.PublishControlAsync(device, deviceRule, _controlSessionId);
                }
                device.SetControlIndicators(deviceRule);
                device.HasControl = true;
                device.ControlSummary = BuildControlSummary(deviceRule);
                _localControlledMacs.Add(device.MacAddress);
                device.IsBulkSelected = false;
            }
            SetStatus($"Temporary control applied to {devices.Count} device(s).");
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
        var result = await dialog.ShowDialog<NetHogSettings?>(this);
        if (result is null) return;
        _settings = result;
        _settingsStore.Save(_settings);
        foreach (var device in Devices)
        {
            device.SetRateUnit(_settings.RateUnit);
            device.SetDataUnit(_settings.DataUnit);
        }
        SetStatus("Settings saved locally.");
    }

    private async void HistoryNav_Click(object? sender, RoutedEventArgs e)
    {
        await new HistoryDialog().ShowDialog(this);
    }

    private async void DomainsNav_Click(object? sender, RoutedEventArgs e)
    {
        await new DomainDialog(_enforcement.GetDomainObservations()).ShowDialog(this);
    }

    private async void ChartNav_Click(object? sender, RoutedEventArgs e)
    {
        await new ChartDialog(_trafficSamples).ShowDialog(this);
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

    private void TrafficTimer_Tick(object? sender, EventArgs e)
    {
        var traffic = _trafficReader.Read(SelectedAdapter?.Name);
        var deviceTraffic = _enforcement.GetTrafficSnapshot();
        foreach (var device in Devices)
        {
            if (deviceTraffic.TryGetValue(device.MacAddress, out var observed))
            {
                device.SetTraffic(observed.DownloadBytes, observed.UploadBytes);
            }
        }
        if (_previousTrafficAt != default)
        {
            var seconds = Math.Max(0.1, (DateTimeOffset.Now - _previousTrafficAt).TotalSeconds);
            var downloadMbps = Math.Max(0, traffic.DownloadBytes - _previousReceived) * 8d / seconds / 1_000_000d;
            var uploadMbps = Math.Max(0, traffic.UploadBytes - _previousSent) * 8d / seconds / 1_000_000d;
            TrafficSummaryText = $"↓ {FormatRate(downloadMbps)}  ·  ↑ {FormatRate(uploadMbps)}";
            if (_enforcement.IsSessionActive)
            {
                _trafficSamples.Add(new TrafficSample(DateTimeOffset.Now, traffic.DownloadBytes, traffic.UploadBytes, downloadMbps, uploadMbps));
                if (_trafficSamples.Count > 900) _trafficSamples.RemoveRange(0, _trafficSamples.Count - 900);
            }
        }
        _previousReceived = traffic.DownloadBytes;
        _previousSent = traffic.UploadBytes;
        _previousTrafficAt = DateTimeOffset.Now;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trafficTimer.Stop();
        if (_enforcement.IsSessionActive)
        {
            try
            {
                await _enforcement.StopSessionAsync();
                SaveCompletedSession();
            }
            catch { /* Process exit remains best effort. */ }
        }
        try { await _remoteCoordinator.StopAsync(); }
        catch { /* LAN coordination is best effort during process exit. */ }
    }

    private void RemoteCoordinator_ActiveRemoteControlsChanged(IReadOnlyList<RemoteControlLease> leases)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _remoteControls = leases;
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
                device.ControlSummary = $"Remote · {lease.ControlSummary}";
            }
            else if (!_localControlledMacs.Contains(device.MacAddress))
            {
                device.HasControl = false;
                device.ControlSummary = "No controls";
            }
        }
    }

    private void RemoteCoordinator_ReleaseRequested(RemoteControlReleaseRequest request)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await _enforcement.RemoveRuleAsync(request.TargetMacAddress);
                await _remoteCoordinator.RemoveControlAsync(request.TargetMacAddress, request.SessionId);
                _localControlledMacs.Remove(request.TargetMacAddress);
                var device = Devices.FirstOrDefault(candidate =>
                    candidate.MacAddress.Equals(request.TargetMacAddress, StringComparison.OrdinalIgnoreCase));
                if (device is not null)
                {
                    device.HasControl = false;
                    device.SetControlIndicators(null);
                    device.ControlSummary = "No controls";
                }
                SetStatus("A controlled NetHog instance removed its own control locally.");
            }
            catch (Exception exception)
            {
                SetStatus($"The remote release could not complete: {exception.Message}");
            }
        });
    }

    private void SetStatus(string text) => StatusText = text;

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

    private static string FormatRate(double megabitsPerSecond) => megabitsPerSecond switch
    {
        >= 1_000 => $"{megabitsPerSecond / 1_000:0.00} Gbps",
        >= 1 => $"{megabitsPerSecond:0.00} Mbps",
        >= 0.001 => $"{megabitsPerSecond * 1_000:0.0} Kbps",
        _ => $"{megabitsPerSecond * 1_000_000:0} bps"
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
