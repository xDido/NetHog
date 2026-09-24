using System.Globalization;
using System.Windows;
using Forms = System.Windows.Forms;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private readonly NetHogSettings _settings;

    public SettingsWindow(AppSettingsStore store, NetHogSettings settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();
        RateUnitComboBox.SelectedIndex = settings.RateUnit == TrafficRateUnit.BytesPerSecond ? 1 : 0;
        DataUnitComboBox.SelectedIndex = settings.DataUnit == TrafficDataUnit.Binary ? 1 : 0;
        DarkModeCheckBox.IsChecked = settings.DarkModeEnabled;
        TrafficOverlayCheckBox.IsChecked = settings.ShowTrafficOverlay;
        OverlayPositionComboBox.SelectedIndex = Enum.IsDefined(settings.OverlayPosition)
            ? (int)settings.OverlayPosition
            : (int)TrafficOverlayPosition.BottomRight;
        OverlayOpacitySlider.Value = Math.Clamp(settings.OverlayOpacity, 0.35, 1) * 100;
        OverlayClickThroughCheckBox.IsChecked = settings.OverlayClickThrough;
        TrafficAlertsCheckBox.IsChecked = settings.TrafficAlertsEnabled;
        TrafficAlertThresholdTextBox.Text = Math.Clamp(settings.TrafficAlertThresholdMbps, 1, 10_000)
            .ToString(CultureInfo.CurrentCulture);
        LoadOverlayScreens(settings.OverlayScreenName);
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTrayOnClose;
        AutomaticUpdatesCheckBox.IsChecked = settings.AutomaticUpdatesEnabled;
        HistoryRetentionCheckBox.IsChecked = settings.HistoryRetentionEnabled;
        HistoryRetentionValueTextBox.Text = settings.HistoryRetentionValue.ToString(CultureInfo.CurrentCulture);
        HistoryRetentionUnitComboBox.SelectedIndex = settings.HistoryRetentionUnit == HistoryRetentionUnit.Hours ? 0 : 1;
        UpdateRetentionInputs();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void HistoryRetentionCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateRetentionInputs();

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayOpacityText is not null) OverlayOpacityText.Text = $"{e.NewValue:0}%";
    }

    private void LoadOverlayScreens(string selectedDeviceName)
    {
        var screens = Forms.Screen.AllScreens
            .Select((screen, index) => new OverlayScreenOption(
                screen.DeviceName,
                screen.Primary ? "Primary display" : $"Display {index + 1}"))
            .ToArray();
        OverlayScreenComboBox.ItemsSource = screens;
        OverlayScreenComboBox.SelectedItem = screens.FirstOrDefault(screen =>
            screen.DeviceName.Equals(selectedDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault(screen => screen.IsPrimary)
            ?? screens.FirstOrDefault();
    }

    private void UpdateRetentionInputs()
    {
        var isEnabled = HistoryRetentionCheckBox.IsChecked == true;
        HistoryRetentionControls.IsEnabled = isEnabled;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startWithWindows = StartWithWindowsCheckBox.IsChecked == true;
            var historyRetentionEnabled = HistoryRetentionCheckBox.IsChecked == true;
            var historyRetentionValue = _settings.HistoryRetentionValue;
            if (historyRetentionEnabled &&
                (!int.TryParse(HistoryRetentionValueTextBox.Text, NumberStyles.Integer,
                    CultureInfo.CurrentCulture, out historyRetentionValue) ||
                 historyRetentionValue is < 1 or > 3650))
            {
                MessageBox.Show(this, "Enter a whole number from 1 to 3650 for history retention.",
                    "Invalid history retention", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WindowsStartupManager.SetEnabled(startWithWindows);
            _settings.RateUnit = RateUnitComboBox.SelectedIndex == 1
                ? TrafficRateUnit.BytesPerSecond
                : TrafficRateUnit.BitsPerSecond;
            _settings.DataUnit = DataUnitComboBox.SelectedIndex == 1
                ? TrafficDataUnit.Binary
                : TrafficDataUnit.Decimal;
            _settings.DarkModeEnabled = DarkModeCheckBox.IsChecked == true;
            _settings.ShowTrafficOverlay = TrafficOverlayCheckBox.IsChecked == true;
            _settings.OverlayPosition = OverlayPositionComboBox.SelectedIndex is >= 0 and <= 7
                ? (TrafficOverlayPosition)OverlayPositionComboBox.SelectedIndex
                : TrafficOverlayPosition.BottomRight;
            _settings.OverlayOpacity = Math.Clamp(OverlayOpacitySlider.Value / 100, 0.35, 1);
            _settings.OverlayClickThrough = OverlayClickThroughCheckBox.IsChecked == true;
            _settings.OverlayScreenName = (OverlayScreenComboBox.SelectedItem as OverlayScreenOption)?.DeviceName
                                          ?? string.Empty;
            _settings.TrafficAlertsEnabled = TrafficAlertsCheckBox.IsChecked == true;
            if (!int.TryParse(TrafficAlertThresholdTextBox.Text, NumberStyles.Integer,
                    CultureInfo.CurrentCulture, out var alertThreshold)
                || alertThreshold is < 1 or > 10_000)
            {
                MessageBox.Show(this, "Enter a whole number from 1 to 10,000 Mbps for the traffic alert threshold.",
                    "Invalid traffic alert threshold", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings.TrafficAlertThresholdMbps = alertThreshold;
            _settings.StartWithWindows = startWithWindows;
            _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
            _settings.AutomaticUpdatesEnabled = AutomaticUpdatesCheckBox.IsChecked == true;
            _settings.HistoryRetentionEnabled = historyRetentionEnabled;
            _settings.HistoryRetentionValue = historyRetentionValue;
            _settings.HistoryRetentionUnit = HistoryRetentionUnitComboBox.SelectedIndex == 0
                ? HistoryRetentionUnit.Hours
                : HistoryRetentionUnit.Days;
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "NetHog settings could not be saved",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record OverlayScreenOption(string DeviceName, string Name)
    {
        public bool IsPrimary => Name.StartsWith("Primary display", StringComparison.OrdinalIgnoreCase);
    }
}
