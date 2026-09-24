using System.IO;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

public sealed class NetHogSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public TrafficRateUnit RateUnit { get; set; } = TrafficRateUnit.BitsPerSecond;
    public TrafficDataUnit DataUnit { get; set; } = TrafficDataUnit.Decimal;
    public bool AutomaticUpdatesEnabled { get; set; } = true;
    public bool DarkModeEnabled { get; set; }
    public bool ShowTrafficOverlay { get; set; }
    public TrafficOverlayPosition OverlayPosition { get; set; } = TrafficOverlayPosition.BottomRight;
    public double OverlayOpacity { get; set; } = 0.78;
    public bool OverlayClickThrough { get; set; }
    public string OverlayScreenName { get; set; } = string.Empty;
    public bool TrafficAlertsEnabled { get; set; }
    public int TrafficAlertThresholdMbps { get; set; } = 50;
    public bool HistoryRetentionEnabled { get; set; }
    public int HistoryRetentionValue { get; set; } = 30;
    public HistoryRetentionUnit HistoryRetentionUnit { get; set; } = HistoryRetentionUnit.Days;

    public TimeSpan? GetHistoryRetention()
    {
        if (!HistoryRetentionEnabled) return null;

        var value = Math.Clamp(HistoryRetentionValue, 1, 3650);
        return HistoryRetentionUnit == Models.HistoryRetentionUnit.Hours
            ? TimeSpan.FromHours(value)
            : TimeSpan.FromDays(value);
    }
}

public sealed class AppSettingsStore
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetHog",
        "settings.json");
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiBox",
        "settings.json");

    public NetHogSettings Load()
    {
        lock (_sync)
        {
            try
            {
                var sourcePath = File.Exists(_path) ? _path : _legacyPath;
                if (!File.Exists(sourcePath)) return new NetHogSettings();
                return JsonSerializer.Deserialize<NetHogSettings>(File.ReadAllText(sourcePath))
                       ?? new NetHogSettings();
            }
            catch
            {
                return new NetHogSettings();
            }
        }
    }

    public void Save(NetHogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
    }
}
