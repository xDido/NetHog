using System.Windows;
using WifiBox.Models;

namespace WifiBox;

public partial class DeviceControlWindow : Window
{
    private readonly NetworkDevice _device;

    public DeviceControlWindow(NetworkDevice device, DeviceControlRule? existingRule)
    {
        InitializeComponent();
        _device = device;
        DeviceIdentityText.Text = $"{device.DisplayName}  ·  {device.AddressSummary}";
        MacAddressText.Text = device.MacAddress;
        if (existingRule is not null)
        {
            DownloadLimitTextBox.Text = existingRule.DownloadLimitMbps?.ToString() ?? string.Empty;
            UploadLimitTextBox.Text = existingRule.UploadLimitMbps?.ToString() ?? string.Empty;
            BlockInternetCheckBox.IsChecked = existingRule.BlockInternet;
            RemoveControlButton.IsEnabled = true;
        }

        UpdateApplyButton();
    }

    public DeviceControlRule? AppliedRule { get; private set; }
    public bool RemoveRequested { get; private set; }

    private void LimitTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded) UpdateApplyButton();
    }

    private void ControlSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateApplyButton();
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

        AppliedRule = new DeviceControlRule(_device.MacAddress, download, upload, block);
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
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private void ShowHint(string message)
    {
        ValidationText.Text = message;
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }
}
