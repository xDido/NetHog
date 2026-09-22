using System.Windows;
using WifiBox.Models;
using WifiBox.Services;

namespace WifiBox;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private readonly WifiBoxSettings _settings;

    public SettingsWindow(AppSettingsStore store, WifiBoxSettings settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();
        RateUnitComboBox.SelectedIndex = settings.RateUnit == TrafficRateUnit.BytesPerSecond ? 1 : 0;
        DataUnitComboBox.SelectedIndex = settings.DataUnit == TrafficDataUnit.Binary ? 1 : 0;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTrayOnClose;
        AutomaticUpdatesCheckBox.IsChecked = settings.AutomaticUpdatesEnabled;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startWithWindows = StartWithWindowsCheckBox.IsChecked == true;
            WindowsStartupManager.SetEnabled(startWithWindows);
            _settings.RateUnit = RateUnitComboBox.SelectedIndex == 1
                ? TrafficRateUnit.BytesPerSecond
                : TrafficRateUnit.BitsPerSecond;
            _settings.DataUnit = DataUnitComboBox.SelectedIndex == 1
                ? TrafficDataUnit.Binary
                : TrafficDataUnit.Decimal;
            _settings.StartWithWindows = startWithWindows;
            _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
            _settings.AutomaticUpdatesEnabled = AutomaticUpdatesCheckBox.IsChecked == true;
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "NetHog settings could not be saved",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
