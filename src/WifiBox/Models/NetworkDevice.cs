using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiBox.Models;

public sealed class NetworkDevice : INotifyPropertyChanged
{
    private string _discoveredName;
    private readonly List<string> _ipv6Addresses = new();
    private string _controlSummary = "No controls";
    private string _nickname = string.Empty;
    private long _downloadBytes;
    private long _uploadBytes;
    private double _downloadRateMbps;
    private double _uploadRateMbps;
    private TrafficRateUnit _rateUnit = TrafficRateUnit.BitsPerSecond;
    private string _proximitySummary = "Measuring…";
    private string _proximityDetails = "NetHog is taking a short network proximity sample.";
    private bool _hasControl;
    private bool _canConfigure;

    public NetworkDevice(string ipAddress, string macAddress, string neighborState, string displayName, bool isLocalDevice = false)
    {
        IpAddress = ipAddress;
        MacAddress = macAddress;
        NeighborState = neighborState;
        _discoveredName = displayName;
        IsLocalDevice = isLocalDevice;
    }

    public string IpAddress { get; }
    public string MacAddress { get; }
    public string NeighborState { get; }
    public IReadOnlyList<string> Ipv6Addresses => _ipv6Addresses;
    public bool HasIpv4Address => !string.IsNullOrWhiteSpace(IpAddress);
    public string NameLookupAddress => HasIpv4Address ? IpAddress : _ipv6Addresses.FirstOrDefault() ?? string.Empty;
    public string Ipv6Summary => string.Join("  ·  ", _ipv6Addresses);
    public string AddressSummary => string.IsNullOrWhiteSpace(IpAddress)
        ? Ipv6Summary
        : string.IsNullOrWhiteSpace(Ipv6Summary)
            ? IpAddress
            : $"{IpAddress}\n{Ipv6Summary}";
    public bool IsLocalDevice { get; }
    public string SuggestedName => _discoveredName;
    public bool HasSuggestedName => !_discoveredName.StartsWith("Device ", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? _discoveredName : Nickname;
    public string ProximitySummary => _proximitySummary;
    public string ProximityDetails => _proximityDetails;

    public string Nickname
    {
        get => _nickname;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetField(ref _nickname, normalized)) return;
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public void SetSuggestedName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0 || normalized.Equals(_discoveredName, StringComparison.OrdinalIgnoreCase)) return;
        _discoveredName = normalized;
        OnPropertyChanged(nameof(SuggestedName));
        OnPropertyChanged(nameof(HasSuggestedName));
        OnPropertyChanged(nameof(DisplayName));
    }

    public void AddIpv6Address(string address)
    {
        var normalized = address.Trim();
        if (normalized.Length == 0 || _ipv6Addresses.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
        _ipv6Addresses.Add(normalized);
        OnPropertyChanged(nameof(Ipv6Addresses));
        OnPropertyChanged(nameof(Ipv6Summary));
        OnPropertyChanged(nameof(AddressSummary));
    }

    public void SetProximity(string summary, string details)
    {
        var normalizedSummary = string.IsNullOrWhiteSpace(summary) ? "Unavailable" : summary.Trim();
        var normalizedDetails = string.IsNullOrWhiteSpace(details) ? normalizedSummary : details.Trim();
        SetField(ref _proximitySummary, normalizedSummary, nameof(ProximitySummary));
        SetField(ref _proximityDetails, normalizedDetails, nameof(ProximityDetails));
    }

    public long DownloadBytes
    {
        get => _downloadBytes;
        private set => SetField(ref _downloadBytes, value);
    }

    public long UploadBytes
    {
        get => _uploadBytes;
        private set => SetField(ref _uploadBytes, value);
    }

    public double DownloadRateMbps
    {
        get => _downloadRateMbps;
        private set => SetField(ref _downloadRateMbps, value);
    }

    public double UploadRateMbps
    {
        get => _uploadRateMbps;
        private set => SetField(ref _uploadRateMbps, value);
    }

    public string TrafficSummary => DownloadBytes == 0 && UploadBytes == 0
        ? "No traffic observed"
        : $"↓ {FormatRate(DownloadRateMbps, _rateUnit)}  ·  ↑ {FormatRate(UploadRateMbps, _rateUnit)}";

    public void SetRateUnit(TrafficRateUnit rateUnit)
    {
        if (_rateUnit == rateUnit) return;
        _rateUnit = rateUnit;
        OnPropertyChanged(nameof(TrafficSummary));
    }

    public void SetTraffic(long downloadBytes, long uploadBytes, double downloadRateMbps = 0, double uploadRateMbps = 0)
    {
        DownloadBytes = Math.Max(0, downloadBytes);
        UploadBytes = Math.Max(0, uploadBytes);
        DownloadRateMbps = Math.Max(0, downloadRateMbps);
        UploadRateMbps = Math.Max(0, uploadRateMbps);
        OnPropertyChanged(nameof(TrafficSummary));
    }

    public string ControlSummary
    {
        get => _controlSummary;
        set => SetField(ref _controlSummary, value);
    }

    public bool HasControl
    {
        get => _hasControl;
        set => SetField(ref _hasControl, value);
    }

    public bool CanConfigure
    {
        get => _canConfigure;
        set => SetField(ref _canConfigure, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
