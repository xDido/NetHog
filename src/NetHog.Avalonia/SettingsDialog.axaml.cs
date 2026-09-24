using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class SettingsDialog : Window, INotifyPropertyChanged
{
    private readonly NetHogSettings _settings;
    private string _historyRetentionValue;
    private string _validationText = string.Empty;
    private TrafficRateUnit _selectedRateUnit;
    private TrafficDataUnit _selectedDataUnit;
    private HistoryRetentionUnit _selectedRetentionUnit;

    public SettingsDialog() : this(new NetHogSettings())
    {
    }

    public SettingsDialog(NetHogSettings settings)
    {
        InitializeComponent();
        _settings = new NetHogSettings
        {
            StartWithWindows = settings.StartWithWindows,
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            RateUnit = settings.RateUnit,
            DataUnit = settings.DataUnit,
            AutomaticUpdatesEnabled = settings.AutomaticUpdatesEnabled,
            DarkModeEnabled = settings.DarkModeEnabled,
            ShowTrafficOverlay = settings.ShowTrafficOverlay,
            OverlayPosition = settings.OverlayPosition,
            OverlayOpacity = settings.OverlayOpacity,
            OverlayClickThrough = settings.OverlayClickThrough,
            OverlayScreenName = settings.OverlayScreenName,
            TrafficAlertsEnabled = settings.TrafficAlertsEnabled,
            TrafficAlertThresholdMbps = settings.TrafficAlertThresholdMbps,
            HistoryRetentionEnabled = settings.HistoryRetentionEnabled,
            HistoryRetentionValue = settings.HistoryRetentionValue,
            HistoryRetentionUnit = settings.HistoryRetentionUnit
        };
        _historyRetentionValue = settings.HistoryRetentionValue.ToString();
        _selectedRateUnit = settings.RateUnit;
        _selectedDataUnit = settings.DataUnit;
        _selectedRetentionUnit = settings.HistoryRetentionUnit;
        DataContext = this;
    }

    public IReadOnlyList<TrafficRateUnit> RateUnits { get; } = Enum.GetValues<TrafficRateUnit>();
    public IReadOnlyList<TrafficDataUnit> DataUnits { get; } = Enum.GetValues<TrafficDataUnit>();
    public IReadOnlyList<HistoryRetentionUnit> RetentionUnits { get; } = Enum.GetValues<HistoryRetentionUnit>();

    public TrafficRateUnit SelectedRateUnit { get => _selectedRateUnit; set => SetField(ref _selectedRateUnit, value); }
    public TrafficDataUnit SelectedDataUnit { get => _selectedDataUnit; set => SetField(ref _selectedDataUnit, value); }
    public HistoryRetentionUnit SelectedRetentionUnit { get => _selectedRetentionUnit; set => SetField(ref _selectedRetentionUnit, value); }
    public bool AutomaticUpdatesEnabled { get => _settings.AutomaticUpdatesEnabled; set { _settings.AutomaticUpdatesEnabled = value; OnPropertyChanged(); } }
    public bool StartWithDesktop { get => _settings.StartWithWindows; set { _settings.StartWithWindows = value; OnPropertyChanged(); } }
    public bool DarkModeEnabled { get => _settings.DarkModeEnabled; set { _settings.DarkModeEnabled = value; OnPropertyChanged(); } }
    public bool HistoryRetentionEnabled { get => _settings.HistoryRetentionEnabled; set { _settings.HistoryRetentionEnabled = value; OnPropertyChanged(); } }
    public string HistoryRetentionValue { get => _historyRetentionValue; set => SetField(ref _historyRetentionValue, value); }
    public string ValidationText { get => _validationText; private set => SetField(ref _validationText, value); }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HistoryRetentionValue, out var retention) || retention is < 1 or > 3650)
        {
            ValidationText = "History retention must be a whole number from 1 to 3650.";
            return;
        }

        _settings.RateUnit = SelectedRateUnit;
        _settings.DataUnit = SelectedDataUnit;
        _settings.HistoryRetentionValue = retention;
        _settings.HistoryRetentionUnit = SelectedRetentionUnit;
        Close(_settings);
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
