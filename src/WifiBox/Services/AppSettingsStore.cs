using System.IO;
using System.Text.Json;
using WifiBox.Models;

namespace WifiBox.Services;

public sealed class WifiBoxSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public TrafficRateUnit RateUnit { get; set; } = TrafficRateUnit.BitsPerSecond;
    public TrafficDataUnit DataUnit { get; set; } = TrafficDataUnit.Decimal;
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

    public WifiBoxSettings Load()
    {
        lock (_sync)
        {
            try
            {
                var sourcePath = File.Exists(_path) ? _path : _legacyPath;
                if (!File.Exists(sourcePath)) return new WifiBoxSettings();
                return JsonSerializer.Deserialize<WifiBoxSettings>(File.ReadAllText(sourcePath))
                       ?? new WifiBoxSettings();
            }
            catch
            {
                return new WifiBoxSettings();
            }
        }
    }

    public void Save(WifiBoxSettings settings)
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
