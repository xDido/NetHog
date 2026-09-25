using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class DomainDialog : Window, INotifyPropertyChanged
{
    private readonly IEnforcementService _enforcement;
    private readonly DispatcherTimer _refreshTimer;
    private NetworkDevice? _selectedDevice;
    private DomainRow? _selectedObservation;
    private string _domainText = string.Empty;
    private string _statusText = "Start a control session to observe live destination metadata.";
    private bool _suppressDeviceRefresh;

    public DomainDialog() : this(new ArpEnforcementService(), Array.Empty<NetworkDevice>())
    {
    }

    public DomainDialog(IEnforcementService enforcement, IEnumerable<NetworkDevice> devices)
    {
        _enforcement = enforcement;
        InitializeComponent();
        foreach (var device in devices.Where(device => !device.IsLocalDevice && device.HasIpv4Address))
            Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshObservations();
        Opened += (_, _) =>
        {
            RefreshObservations();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    public ObservableCollection<NetworkDevice> Devices { get; } = new();
    public ObservableCollection<DomainRow> Observations { get; } = new();
    public NetworkDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (EqualityComparer<NetworkDevice?>.Default.Equals(_selectedDevice, value)) return;
            _selectedDevice = value;
            OnPropertyChanged();
            if (_suppressDeviceRefresh) return;
            RefreshObservations();
        }
    }
    public DomainRow? SelectedObservation
    {
        get => _selectedObservation;
        set
        {
            if (ReferenceEquals(_selectedObservation, value)) return;
            _selectedObservation = value;
            if (value is not null)
            {
                DomainText = value.Domain;
                _suppressDeviceRefresh = true;
                try
                {
                    SelectedDevice = Devices.FirstOrDefault(device =>
                        device.MacAddress.Equals(value.DeviceMacAddress, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    _suppressDeviceRefresh = false;
                }
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanBlockSelected));
            OnPropertyChanged(nameof(CanUnblockSelected));
        }
    }
    public string DomainText { get => _domainText; set => SetField(ref _domainText, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public bool CanBlockSelected => SelectedObservation is { IsBlocked: false };
    public bool CanUnblockSelected => SelectedObservation is { IsBlocked: true };
    public new event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateDevices(IEnumerable<NetworkDevice> devices)
    {
        var incoming = devices
            .Where(device => !device.IsLocalDevice && device.HasIpv4Address)
            .ToArray();
        var selectedMac = SelectedDevice?.MacAddress;

        Devices.Clear();
        foreach (var device in incoming) Devices.Add(device);

        SelectedDevice = selectedMac is null
            ? incoming.FirstOrDefault()
            : incoming.FirstOrDefault(device =>
                device.MacAddress.Equals(selectedMac, StringComparison.OrdinalIgnoreCase))
              ?? incoming.FirstOrDefault();
        RefreshObservations();
    }

    public void RefreshTheme() => RefreshObservations();

    private void RefreshObservations()
    {
        var selectedMac = SelectedObservation?.DeviceMacAddress;
        var selectedDomain = SelectedObservation?.Domain;
        var deviceMac = SelectedDevice?.MacAddress;
        Observations.Clear();
        foreach (var observation in _enforcement.GetDomainObservations()
                     .Where(observation => deviceMac is null
                         || observation.DeviceMacAddress.Equals(deviceMac, StringComparison.OrdinalIgnoreCase)))
            Observations.Add(new DomainRow(observation));
        SelectedObservation = selectedMac is null || selectedDomain is null
            ? null
            : Observations.FirstOrDefault(row =>
                row.DeviceMacAddress.Equals(selectedMac, StringComparison.OrdinalIgnoreCase)
                && row.Domain.Equals(selectedDomain, StringComparison.OrdinalIgnoreCase));
        StatusText = Observations.Count == 0
            ? (_enforcement.IsSessionActive
                ? SelectedDevice is null
                    ? "No destination metadata observed yet. New DNS, TLS, or HTTP traffic will appear here."
                    : $"No destination metadata observed yet for {SelectedDevice.DisplayName}. New DNS, TLS, or HTTP traffic will appear here."
                : "Start a control session to observe live destination metadata.")
            : SelectedDevice is null
                ? $"{Observations.Count:N0} domain{(Observations.Count == 1 ? string.Empty : "s")} observed."
                : $"{Observations.Count:N0} domain{(Observations.Count == 1 ? string.Empty : "s")} observed for {SelectedDevice.DisplayName}.";
        OnPropertyChanged(nameof(CanBlockSelected));
        OnPropertyChanged(nameof(CanUnblockSelected));
    }

    private void Observation_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanBlockSelected));
        OnPropertyChanged(nameof(CanUnblockSelected));
    }

    private void BlockDomain_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is null)
        {
            StatusText = "Select a device before adding a domain rule.";
            return;
        }
        var domain = DomainObservationStore.NormalizeDomain(DomainText);
        if (domain.Length == 0)
        {
            StatusText = "Enter a valid domain such as example.com.";
            return;
        }
        _enforcement.SetDomainBlocked(SelectedDevice.MacAddress, domain, true);
        DomainText = domain;
        StatusText = $"Blocked {domain} for {SelectedDevice.DisplayName} and its subdomains.";
        RefreshObservations();
    }

    private void BlockSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedObservation is null) return;
        _enforcement.SetDomainBlocked(SelectedObservation.DeviceMacAddress, SelectedObservation.Domain, true);
        RefreshObservations();
    }

    private void UnblockSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedObservation is null) return;
        _enforcement.SetDomainBlocked(SelectedObservation.DeviceMacAddress, SelectedObservation.Domain, false);
        RefreshObservations();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed class DomainRow
    {
        public DomainRow(DomainObservation observation)
        {
            DeviceMacAddress = observation.DeviceMacAddress;
            DeviceText = observation.DeviceDisplayName;
            Domain = observation.Domain;
            DeviceAddressText = observation.DeviceMacAddress;
            LastSeenText = observation.LastSeen.ToLocalTime().ToString("HH:mm:ss");
            SeenText = observation.SeenCount.ToString();
            IsBlocked = observation.IsBlocked;
            StateText = observation.IsBlocked ? "Blocked" : "Observed";
            StateBrush = GetThemeBrush(
                observation.IsBlocked ? "DangerBrush" : "GoodBrush",
                observation.IsBlocked ? "#B33D43" : "#16745F");
        }

        public string DeviceMacAddress { get; }
        public string DeviceText { get; }
        public string DeviceAddressText { get; }
        public string Domain { get; }
        public string LastSeenText { get; }
        public string SeenText { get; }
        public bool IsBlocked { get; }
        public string StateText { get; }
        public IBrush StateBrush { get; }

        private static IBrush GetThemeBrush(string key, string fallback) =>
            Application.Current?.Resources[key] is SolidColorBrush brush
                ? brush
                : new SolidColorBrush(Color.Parse(fallback));
    }
}
