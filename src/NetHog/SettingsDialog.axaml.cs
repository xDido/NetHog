using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class SettingsDialog : Window, INotifyPropertyChanged
{
    private readonly NetHogSettings _settings;
    private string _historyRetentionValue;
    private string _validationText = string.Empty;
    private string _applyFeedbackText = string.Empty;
    private TrafficRateUnit _selectedRateUnit;
    private TrafficDataUnit _selectedDataUnit;
    private HistoryRetentionUnit _selectedRetentionUnit;
    private int _selectedOverlayPositionIndex;
    private double _overlayOpacityPercent;
    private string _trafficAlertThresholdValue;

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
        _selectedOverlayPositionIndex = (int)settings.OverlayPosition;
        _overlayOpacityPercent = Math.Clamp(settings.OverlayOpacity * 100, 35, 100);
        _trafficAlertThresholdValue = settings.TrafficAlertThresholdMbps.ToString();
        DataContext = this;
        Opened += (_, _) =>
        {
            RefreshOverlayScreens();
            WindowsTitleBarTheme.Apply(this, _settings.DarkModeEnabled);
        };
    }

    public IReadOnlyList<TrafficRateUnit> RateUnits { get; } = Enum.GetValues<TrafficRateUnit>();
    public IReadOnlyList<TrafficDataUnit> DataUnits { get; } = Enum.GetValues<TrafficDataUnit>();
    public IReadOnlyList<HistoryRetentionUnit> RetentionUnits { get; } = Enum.GetValues<HistoryRetentionUnit>();

    public string SettingsSubtitle => OperatingSystem.IsWindows()
        ? "Choose how NetHog behaves when Windows starts and when you close the window."
        : "Choose how NetHog behaves when your desktop session starts and when you close the window.";
    public TrafficRateUnit SelectedRateUnit { get => _selectedRateUnit; set => SetField(ref _selectedRateUnit, value); }
    public TrafficDataUnit SelectedDataUnit { get => _selectedDataUnit; set => SetField(ref _selectedDataUnit, value); }
    public HistoryRetentionUnit SelectedRetentionUnit { get => _selectedRetentionUnit; set => SetField(ref _selectedRetentionUnit, value); }
    public int SelectedRateUnitIndex
    {
        get => SelectedRateUnit == TrafficRateUnit.BytesPerSecond ? 1 : 0;
        set => SelectedRateUnit = value == 1 ? TrafficRateUnit.BytesPerSecond : TrafficRateUnit.BitsPerSecond;
    }
    public int SelectedDataUnitIndex
    {
        get => SelectedDataUnit == TrafficDataUnit.Binary ? 1 : 0;
        set => SelectedDataUnit = value == 1 ? TrafficDataUnit.Binary : TrafficDataUnit.Decimal;
    }
    public int SelectedRetentionUnitIndex
    {
        get => SelectedRetentionUnit == HistoryRetentionUnit.Hours ? 0 : 1;
        set => SelectedRetentionUnit = value == 0 ? HistoryRetentionUnit.Hours : HistoryRetentionUnit.Days;
    }
    public bool AutomaticUpdatesEnabled { get => _settings.AutomaticUpdatesEnabled; set { _settings.AutomaticUpdatesEnabled = value; OnPropertyChanged(); } }
    public string StartupTitle => OperatingSystem.IsWindows()
        ? "Start NetHog with Windows"
        : "Start NetHog with the desktop session";
    public string StartupDescription => OperatingSystem.IsWindows()
        ? "NetHog will launch in the background using the Windows per-user startup list."
        : "NetHog will launch automatically when you sign in to your desktop session.";
    public bool StartWithDesktop { get => _settings.StartWithWindows; set { _settings.StartWithWindows = value; OnPropertyChanged(); } }
    public bool MinimizeToTray { get => _settings.MinimizeToTrayOnClose; set { _settings.MinimizeToTrayOnClose = value; OnPropertyChanged(); } }
    public bool DarkModeEnabled { get => _settings.DarkModeEnabled; set { _settings.DarkModeEnabled = value; OnPropertyChanged(); } }
    public bool ShowTrafficOverlay { get => _settings.ShowTrafficOverlay; set { _settings.ShowTrafficOverlay = value; OnPropertyChanged(); } }
    public IReadOnlyList<string> OverlayPositions { get; } =
        ["Top-left", "Top-center", "Top-right", "Middle-left", "Middle-right", "Bottom-left", "Bottom-center", "Bottom-right"];
    public ObservableCollection<OverlayScreenOption> OverlayScreens { get; } = new();
    public OverlayScreenOption? SelectedOverlayScreen { get; set; }
    public int SelectedOverlayPositionIndex
    {
        get => _selectedOverlayPositionIndex;
        set => SetField(ref _selectedOverlayPositionIndex, Math.Clamp(value, 0, 7));
    }
    public double OverlayOpacityPercent
    {
        get => _overlayOpacityPercent;
        set => SetField(ref _overlayOpacityPercent, Math.Clamp(value, 35, 100));
    }
    public bool OverlayClickThrough { get => _settings.OverlayClickThrough; set { _settings.OverlayClickThrough = value; OnPropertyChanged(); } }
    public bool TrafficAlertsEnabled { get => _settings.TrafficAlertsEnabled; set { _settings.TrafficAlertsEnabled = value; OnPropertyChanged(); } }
    public string TrafficAlertThresholdValue
    {
        get => _trafficAlertThresholdValue;
        set => SetField(ref _trafficAlertThresholdValue, KeepAsciiDigits(value));
    }
    public bool HistoryRetentionEnabled { get => _settings.HistoryRetentionEnabled; set { _settings.HistoryRetentionEnabled = value; OnPropertyChanged(); } }
    public string HistoryRetentionValue
    {
        get => _historyRetentionValue;
        set => SetField(ref _historyRetentionValue, KeepAsciiDigits(value));
    }
    public string ValidationText { get => _validationText; private set => SetField(ref _validationText, value); }
    public string ApplyFeedbackText { get => _applyFeedbackText; private set => SetField(ref _applyFeedbackText, value); }

    public new event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<NetHogSettings>? ApplyRequested;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private static void IntegerTextBox_TextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = string.IsNullOrEmpty(e.Text) || e.Text.Any(character => character is < '0' or > '9');
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings)) return;
        ApplyRequested?.Invoke(this, settings);
        ApplyFeedbackText = "Settings applied.";
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings)) return;
        Close(settings);
    }

    private bool TryBuildSettings(out NetHogSettings settings)
    {
        ApplyFeedbackText = string.Empty;
        if (!int.TryParse(HistoryRetentionValue, out var retention) || retention is < 1 or > 3650)
        {
            if (HistoryRetentionEnabled)
            {
                ValidationText = "History retention must be a whole number from 1 to 3650.";
                settings = null!;
                return false;
            }

            retention = Math.Clamp(_settings.HistoryRetentionValue, 1, 3650);
        }
        if (!int.TryParse(TrafficAlertThresholdValue, out var alertThreshold) || alertThreshold is < 1 or > 10_000)
        {
            ValidationText = "Traffic alert threshold must be a whole number from 1 to 10,000 Mbps.";
            settings = null!;
            return false;
        }

        _settings.RateUnit = SelectedRateUnit;
        _settings.DataUnit = SelectedDataUnit;
        _settings.OverlayPosition = (TrafficOverlayPosition)SelectedOverlayPositionIndex;
        _settings.OverlayOpacity = OverlayOpacityPercent / 100d;
        _settings.OverlayScreenName = SelectedOverlayScreen?.DeviceName ?? string.Empty;
        _settings.TrafficAlertThresholdMbps = alertThreshold;
        _settings.HistoryRetentionValue = retention;
        _settings.HistoryRetentionUnit = SelectedRetentionUnit;
        settings = CreateSettingsSnapshot();
        ValidationText = string.Empty;
        return true;
    }

    private NetHogSettings CreateSettingsSnapshot() => new()
    {
        StartWithWindows = _settings.StartWithWindows,
        MinimizeToTrayOnClose = _settings.MinimizeToTrayOnClose,
        RateUnit = _settings.RateUnit,
        DataUnit = _settings.DataUnit,
        AutomaticUpdatesEnabled = _settings.AutomaticUpdatesEnabled,
        DarkModeEnabled = _settings.DarkModeEnabled,
        ShowTrafficOverlay = _settings.ShowTrafficOverlay,
        OverlayPosition = _settings.OverlayPosition,
        OverlayOpacity = _settings.OverlayOpacity,
        OverlayClickThrough = _settings.OverlayClickThrough,
        OverlayScreenName = _settings.OverlayScreenName,
        TrafficAlertsEnabled = _settings.TrafficAlertsEnabled,
        TrafficAlertThresholdMbps = _settings.TrafficAlertThresholdMbps,
        HistoryRetentionEnabled = _settings.HistoryRetentionEnabled,
        HistoryRetentionValue = _settings.HistoryRetentionValue,
        HistoryRetentionUnit = _settings.HistoryRetentionUnit
    };

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

    private void RefreshOverlayScreens()
    {
        var screens = Screens?.All ?? Array.Empty<Screen>();
        OverlayScreens.Clear();
        var options = screens.Select((screen, index) =>
        {
            // Some Linux/X11 and Wayland backends expose the primary monitor
            // without setting Avalonia's IsPrimary flag. Treat the first
            // reported screen as primary in that case, matching Windows.
            var isPrimary = screen.IsPrimary || index == 0;
            return new OverlayScreenOption(
                string.IsNullOrWhiteSpace(screen.DisplayName) ? $"screen-{index + 1}" : screen.DisplayName,
                isPrimary ? "Primary display" : $"Display {index + 1}",
                isPrimary);
        }).ToArray();
        foreach (var option in options) OverlayScreens.Add(option);

        SelectedOverlayScreen = options.FirstOrDefault(option =>
            option.DeviceName.Equals(_settings.OverlayScreenName, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option => option.IsPrimary)
            ?? options.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedOverlayScreen));
    }

    public sealed record OverlayScreenOption(string DeviceName, string Name, bool IsPrimary);
}
