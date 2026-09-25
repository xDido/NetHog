using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class ControlDialog : Window, INotifyPropertyChanged
{
    private ControlPreset _selectedPreset;
    private string _downloadText = string.Empty;
    private string _uploadText = string.Empty;
    private bool _blockInternet;
    private string _validationText = string.Empty;
    private DurationOption _selectedDuration = new("Until session stops", null);
    private readonly DeviceControlRule? _existingRule;
    private readonly ControlPresetStore _presetStore = new();
    private readonly bool _isLocalDevice;
    private readonly string _macAddress;

    public ControlDialog() : this("Selected device")
    {
    }

    public ControlDialog(string targetDescription, DeviceControlRule? existingRule = null)
        : this(targetDescription, false, existingRule)
    {
    }

    public ControlDialog(string targetDescription, bool isLocalDevice, DeviceControlRule? existingRule = null)
        : this(targetDescription, isLocalDevice, existingRule, null)
    {
    }

    public ControlDialog(
        string targetDescription,
        bool isLocalDevice,
        DeviceControlRule? existingRule,
        string? macAddress)
    {
        InitializeComponent();
        TargetDescription = targetDescription;
        _isLocalDevice = isLocalDevice;
        _macAddress = macAddress ?? string.Empty;
        _existingRule = existingRule;
        Presets = new ObservableCollection<ControlPreset>(_presetStore.Load());
        _selectedPreset = ControlPresetCatalog.Find(existingRule?.PresetId, Presets);
        _downloadText = existingRule?.DownloadLimitMbps?.ToString() ?? string.Empty;
        _uploadText = existingRule?.UploadLimitMbps?.ToString() ?? string.Empty;
        _blockInternet = existingRule?.BlockInternet == true;
        _selectedDuration = Durations.FirstOrDefault(duration => duration.Minutes == existingRule?.DurationMinutes)
                            ?? Durations[0];
        DataContext = this;
    }

    public string DialogTitle => _isLocalDevice ? "Current PC controls" : "Device controls";
    public string TargetDescription { get; }
    public string MacAddressText => _macAddress;
    public bool HasMacAddress => !string.IsNullOrWhiteSpace(_macAddress);
    public string ScopeDescription => _isLocalDevice
        ? "Current PC controls"
        : "Temporary IPv4 controls for this device";
    public string ScopeNotice => _isLocalDevice
        ? OperatingSystem.IsWindows()
            ? "These controls use Windows local traffic policy. Upload can be throttled; Windows cannot shape this PC's incoming traffic here. Leave Download blank."
            : "These controls use Linux traffic-control and firewall policy. Upload can be throttled; inbound shaping is unavailable here. Leave Download blank."
        : "These settings last for this control session. Enter a whole Mbps value, or leave a direction blank for no limit.";
    public string DownloadHint => _isLocalDevice
        ? "Unavailable for the current PC"
        : "Leave blank for no limit";
    public string BlockDescription => _isLocalDevice
        ? OperatingSystem.IsWindows()
            ? "Blocks outbound internet traffic from this PC through Windows Firewall."
            : "Blocks outbound IPv4 internet traffic from this PC while preserving the local subnet."
        : "Blocks IPv4 internet traffic routed through this gateway.";
    public bool HasExistingRule => _existingRule is not null;
    public bool RemoveRequested { get; private set; }
    public ObservableCollection<ControlPreset> Presets { get; }
    public IReadOnlyList<DurationOption> Durations { get; } =
    [
        new("Until session stops", null),
        new("15 minutes", 15),
        new("30 minutes", 30),
        new("1 hour", 60),
        new("2 hours", 120)
    ];

    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set => SetField(ref _selectedDuration, value);
    }

    public ControlPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (ReferenceEquals(_selectedPreset, value)) return;
            _selectedPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetDescription));
        }
    }

    public string PresetDescription => SelectedPreset.Description;

    public string DownloadText
    {
        get => _downloadText;
        set
        {
            SetField(ref _downloadText, KeepAsciiDigits(value));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public string UploadText
    {
        get => _uploadText;
        set
        {
            SetField(ref _uploadText, KeepAsciiDigits(value));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool BlockInternet
    {
        get => _blockInternet;
        set
        {
            SetField(ref _blockInternet, value);
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool IsDownloadEnabled => !_isLocalDevice;
    public bool CanSave =>
        (!_isLocalDevice || string.IsNullOrWhiteSpace(DownloadText))
        && TryParseLimit(DownloadText, out var download)
        && TryParseLimit(UploadText, out var upload)
        && (BlockInternet || download is not null || upload is not null);

    public string ValidationText
    {
        get => _validationText;
        private set => SetField(ref _validationText, value);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private static void IntegerTextBox_TextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = string.IsNullOrEmpty(e.Text) || e.Text.Any(character => character is < '0' or > '9');
    }

    private void Preset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedPreset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase)) return;
        DownloadText = _isLocalDevice
            ? string.Empty
            : SelectedPreset.DownloadLimitMbps?.ToString() ?? string.Empty;
        UploadText = SelectedPreset.UploadLimitMbps?.ToString() ?? string.Empty;
        BlockInternet = SelectedPreset.BlockInternet;
        OnPropertyChanged(nameof(CanSave));
    }

    private async void ManagePresets_Click(object? sender, RoutedEventArgs e)
    {
        var selectedId = SelectedPreset.Id;
        var manager = new PresetManagerDialog(Presets);
        var saved = await manager.ShowDialog<bool>(this);
        if (!saved) return;

        Presets.Clear();
        foreach (var preset in _presetStore.Load()) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(preset =>
                             preset.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                         ?? Presets.FirstOrDefault()
                         ?? ControlPresetCatalog.All[0];
        OnPropertyChanged(nameof(PresetDescription));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void RemoveControl_Click(object? sender, RoutedEventArgs e)
    {
        RemoveRequested = true;
        Close(null);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        ValidationText = string.Empty;
        if (_isLocalDevice && !string.IsNullOrWhiteSpace(DownloadText))
        {
            ValidationText = OperatingSystem.IsWindows()
                ? "Download control is unavailable for the current PC on Windows. Leave it blank and set Upload instead."
                : "Download control is unavailable for the current PC on Linux. Leave it blank and set Upload instead.";
            return;
        }
        if (!TryParseLimit(DownloadText, out var download) || !TryParseLimit(UploadText, out var upload))
        {
            ValidationText = "Limits must be whole numbers from 1 to 10,000 Mbps.";
            return;
        }
        if (!BlockInternet && download is null && upload is null)
        {
            ValidationText = "Set at least one limit or enable the IPv4 block.";
            return;
        }
        Close(new DeviceControlRule(string.Empty, download, upload, BlockInternet, SelectedDuration.Minutes, SelectedPreset.Id));
    }

    private static bool TryParseLimit(string text, out int? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }
        if (int.TryParse(text, out var parsed) && parsed is >= 1 and <= 10_000)
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string KeepAsciiDigits(string? value) =>
        new((value ?? string.Empty).Where(character => character is >= '0' and <= '9').ToArray());

    public sealed record DurationOption(string Name, int? Minutes);
}
