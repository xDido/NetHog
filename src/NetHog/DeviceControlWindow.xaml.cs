using System.Windows;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class DeviceControlWindow : Window
{
    private readonly IReadOnlyList<NetworkDevice> _devices;
    private readonly NetworkDevice _device;
    private readonly ControlPresetStore _presetStore = new();
    private IReadOnlyList<ControlPreset> _presets = Array.Empty<ControlPreset>();
    private bool _loading;
    private bool _updatingFromPreset;

    private static readonly IReadOnlyList<DurationOption> Durations =
    [
        new("Until session stops", null),
        new("15 minutes", 15),
        new("30 minutes", 30),
        new("1 hour", 60),
        new("2 hours", 120)
    ];

    public DeviceControlWindow(NetworkDevice device, DeviceControlRule? existingRule)
        : this(new[] { device }, existingRule)
    {
    }

    public DeviceControlWindow(IReadOnlyList<NetworkDevice> devices, DeviceControlRule? existingRule)
    {
        _devices = devices.Where(device => device is not null).ToArray();
        if (_devices.Count == 0) throw new ArgumentException("At least one device is required.", nameof(devices));
        _device = _devices[0];
        InitializeComponent();
        _loading = true;
        LoadPresets();
        DurationComboBox.ItemsSource = Durations;
        PresetComboBox.SelectedIndex = 0;
        DurationComboBox.SelectedIndex = 0;
        DeviceIdentityText.Text = _devices.Count == 1
            ? $"{_device.DisplayName}  ·  {_device.AddressSummary}"
            : $"{_devices.Count} devices selected";
        MacAddressText.Text = _devices.Count == 1
            ? _device.MacAddress
            : string.Join("  ·  ", _devices.Take(3).Select(device => device.MacAddress))
              + (_devices.Count > 3 ? "  ·  …" : string.Empty);
        if (_devices.Count == 1 && _device.IsLocalDevice)
        {
            Title = "Current PC controls";
            ControlScopeText.Text = "These controls use Windows local traffic policy. Upload can be throttled; Windows cannot shape this PC's incoming traffic here. Leave Download blank.";
            DownloadLimitTextBox.IsEnabled = false;
            DownloadHintText.Text = "Unavailable for the current PC";
            BlockDescriptionText.Text = "Blocks outbound internet traffic from this PC through Windows Firewall.";
        }
        if (existingRule is not null)
        {
            PresetComboBox.SelectedItem = ControlPresetCatalog.Find(existingRule.PresetId, _presets);
            DownloadLimitTextBox.Text = existingRule.DownloadLimitMbps?.ToString() ?? string.Empty;
            UploadLimitTextBox.Text = existingRule.UploadLimitMbps?.ToString() ?? string.Empty;
            BlockInternetCheckBox.IsChecked = existingRule.BlockInternet;
            DurationComboBox.SelectedItem = Durations.FirstOrDefault(duration =>
                duration.Minutes == existingRule.DurationMinutes) ?? Durations[0];
            RemoveControlButton.IsEnabled = true;
        }
        else if (_devices.Count > 1)
        {
            Title = "Bulk device controls";
            ControlScopeText.Text = "These controls will be applied to every selected device. They last for this control session or until the selected duration expires.";
            RemoveControlButton.IsEnabled = true;
        }

        _loading = false;
        UpdateApplyButton();
    }

    public DeviceControlRule? AppliedRule { get; private set; }
    public bool RemoveRequested { get; private set; }

    private void LimitTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SelectCustomPresetIfNeeded();
            UpdateApplyButton();
        }
    }

    private void ControlSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            SelectCustomPresetIfNeeded();
            UpdateApplyButton();
        }
    }

    private void PresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || PresetComboBox.SelectedItem is not ControlPreset preset || preset.Id == "custom") return;
        _updatingFromPreset = true;
        try
        {
            DownloadLimitTextBox.Text = preset.DownloadLimitMbps?.ToString() ?? string.Empty;
            UploadLimitTextBox.Text = preset.UploadLimitMbps?.ToString() ?? string.Empty;
            BlockInternetCheckBox.IsChecked = preset.BlockInternet;
        }
        finally
        {
            _updatingFromPreset = false;
        }

        UpdateApplyButton();
    }

    private void ManagePresetsButton_Click(object sender, RoutedEventArgs e)
    {
        var manager = new PresetManagerWindow(_presetStore.Load()) { Owner = this };
        if (manager.ShowDialog() != true) return;

        var selectedId = (PresetComboBox.SelectedItem as ControlPreset)?.Id;
        _loading = true;
        try
        {
            LoadPresets(selectedId);
        }
        finally
        {
            _loading = false;
        }

        UpdateApplyButton();
    }

    private void LoadPresets(string? selectedId = null)
    {
        _presets = _presetStore.Load();
        PresetComboBox.ItemsSource = _presets;
        PresetComboBox.SelectedItem = _presets.FirstOrDefault(preset =>
            preset.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _presets.FirstOrDefault(preset => preset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase))
            ?? _presets.FirstOrDefault();
    }

    private void SelectCustomPresetIfNeeded()
    {
        if (_loading || _updatingFromPreset) return;
        if (PresetComboBox.SelectedItem is not ControlPreset selected
            || ControlPresetCatalog.IsSystemPreset(selected.Id)) return;

        var custom = _presets.FirstOrDefault(preset => ControlPresetCatalog.IsSystemPreset(preset.Id));
        if (custom is not null) PresetComboBox.SelectedItem = custom;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadLimit(DownloadLimitTextBox.Text, "Download", out var download)
            || !TryReadLimit(UploadLimitTextBox.Text, "Upload", out var upload)) return;

        var block = BlockInternetCheckBox.IsChecked == true;
        if (!block && download is null && upload is null)
        {
            ShowValidation("Set at least one speed limit or turn on Block internet.");
            return;
        }

        var preset = PresetComboBox.SelectedItem as ControlPreset ?? _presets[0];
        var duration = (DurationComboBox.SelectedItem as DurationOption)?.Minutes;
        AppliedRule = new DeviceControlRule(
            _device.MacAddress,
            download,
            upload,
            block,
            duration,
            preset.Id == "custom" ? null : preset.Id);
        DialogResult = true;
    }

    private void RemoveControlButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveRequested = true;
        DialogResult = true;
    }

    private bool TryReadLimit(string text, string direction, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var parsed) || parsed is < 1 or > 10_000)
        {
            ShowValidation($"{direction} must be a whole number from 1 to 10,000 Mbps, or left blank.");
            return false;
        }

        value = parsed;
        return true;
    }

    private void UpdateApplyButton()
    {
        var downloadValid = IsValidLimit(DownloadLimitTextBox.Text);
        var uploadValid = IsValidLimit(UploadLimitTextBox.Text);
        var hasAction = BlockInternetCheckBox.IsChecked == true
                        || !string.IsNullOrWhiteSpace(DownloadLimitTextBox.Text)
                        || !string.IsNullOrWhiteSpace(UploadLimitTextBox.Text);

        ApplyButton.IsEnabled = downloadValid && uploadValid && hasAction;
        if (!downloadValid)
        {
            ShowValidation("Download must be a whole number from 1 to 10,000 Mbps, or left blank.");
        }
        else if (!uploadValid)
        {
            ShowValidation("Upload must be a whole number from 1 to 10,000 Mbps, or left blank.");
        }
        else if (!hasAction)
        {
            ShowHint("Set at least one speed limit or turn on Block internet.");
        }
        else
        {
            ShowHint("Limits must be whole numbers from 1 to 10,000 Mbps.");
        }
    }

    private static bool IsValidLimit(string text) =>
        string.IsNullOrWhiteSpace(text)
        || (int.TryParse(text.Trim(), out var parsed) && parsed is >= 1 and <= 10_000);

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
    }

    private void ShowHint(string message)
    {
        ValidationText.Text = message;
        ValidationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
    }

    private sealed record DurationOption(string Name, int? Minutes);
}
