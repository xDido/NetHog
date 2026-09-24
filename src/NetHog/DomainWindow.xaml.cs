using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class DomainWindow : Window
{
    private readonly ArpEnforcementService _enforcementService;
    private readonly ObservableCollection<NetworkDevice> _devices = new();
    private readonly DispatcherTimer _refreshTimer;

    public DomainWindow(ArpEnforcementService enforcementService, IEnumerable<NetworkDevice> devices)
    {
        _enforcementService = enforcementService;
        InitializeComponent();
        DeviceComboBox.ItemsSource = _devices;
        UpdateDevices(devices);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        RefreshObservations();
    }

    private void UpdateDevices(IEnumerable<NetworkDevice> devices)
    {
        var selectedMac = (DeviceComboBox.SelectedItem as NetworkDevice)?.MacAddress;
        _devices.Clear();
        foreach (var device in devices.Where(device => !device.IsLocalDevice && device.HasIpv4Address))
        {
            _devices.Add(device);
        }

        DeviceComboBox.SelectedItem = _devices.FirstOrDefault(device =>
            device.MacAddress.Equals(selectedMac, StringComparison.OrdinalIgnoreCase))
            ?? _devices.FirstOrDefault();
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e) => RefreshObservations();

    private void RefreshObservations()
    {
        var selected = DomainsGrid.SelectedItem as DomainObservation;
        var observations = _enforcementService.GetDomainObservations();
        DomainsGrid.ItemsSource = observations;
        if (selected is not null)
        {
            DomainsGrid.SelectedItem = observations.FirstOrDefault(observation =>
                observation.DeviceMacAddress.Equals(selected.DeviceMacAddress, StringComparison.OrdinalIgnoreCase)
                && observation.Domain.Equals(selected.Domain, StringComparison.OrdinalIgnoreCase));
        }

        DomainStatusText.Text = observations.Count == 0
            ? (_enforcementService.IsSessionActive
                ? "No destination metadata observed yet. New DNS, TLS, or HTTP traffic will appear here."
                : "Start a control session to observe live destination metadata.")
            : $"{observations.Count:N0} domain{(observations.Count == 1 ? string.Empty : "s")} observed. Rules are stored per device.";
        UpdateSelectedActions();
    }

    private void DomainsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedActions();

    private void UpdateSelectedActions()
    {
        var selected = DomainsGrid.SelectedItem as DomainObservation;
        BlockSelectedButton.IsEnabled = selected is not null && !selected.IsBlocked;
        UnblockSelectedButton.IsEnabled = selected is not null && selected.IsBlocked;
        if (selected is not null)
        {
            DomainTextBox.Text = selected.Domain;
            DeviceComboBox.SelectedItem = _devices.FirstOrDefault(device =>
                device.MacAddress.Equals(selected.DeviceMacAddress, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void BlockSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (DomainsGrid.SelectedItem is not DomainObservation selected) return;
        _enforcementService.SetDomainBlocked(selected.DeviceMacAddress, selected.Domain, true);
        RefreshObservations();
    }

    private void UnblockSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (DomainsGrid.SelectedItem is not DomainObservation selected) return;
        _enforcementService.SetDomainBlocked(selected.DeviceMacAddress, selected.Domain, false);
        RefreshObservations();
    }

    private void BlockDomainButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is not NetworkDevice device)
        {
            DomainStatusText.Text = "Select a device before adding a domain rule.";
            return;
        }

        var domain = DomainObservationStore.NormalizeDomain(DomainTextBox.Text);
        if (domain.Length == 0)
        {
            DomainStatusText.Text = "Enter a valid domain such as example.com.";
            return;
        }

        _enforcementService.SetDomainBlocked(device.MacAddress, domain, true);
        DomainTextBox.Text = domain;
        DomainStatusText.Text = $"Blocked {domain} for {device.DisplayName} and its subdomains.";
        RefreshObservations();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }
}
