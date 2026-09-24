using System.IO;
using System.Text.Json;

namespace NetHog.Services;

/// <summary>Stores user-provided device nicknames locally, keyed by normalized MAC address.</summary>
public sealed class DeviceProfileStore
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetHog",
        "device-profiles.json");
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiBox",
        "device-profiles.json");

    public IReadOnlyDictionary<string, string> LoadNicknames()
    {
        lock (_sync)
        {
            try
            {
                var sourcePath = File.Exists(_path) ? _path : _legacyPath;
                if (!File.Exists(sourcePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(sourcePath);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return values is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void SaveNickname(string macAddress, string nickname)
    {
        var key = NormalizeMac(macAddress);
        if (key.Length == 0) return;

        lock (_sync)
        {
            var values = LoadNicknamesInternal();
            if (string.IsNullOrWhiteSpace(nickname)) values.Remove(key);
            else values[key] = nickname.Trim();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // A nickname is a convenience; a read-only profile directory must not affect scanning.
            }
        }
    }

    private Dictionary<string, string> LoadNicknamesInternal()
    {
        try
        {
            var sourcePath = File.Exists(_path) ? _path : _legacyPath;
            if (!File.Exists(sourcePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(sourcePath));
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeMac(string value)
    {
        var hex = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
        return hex.Length == 12 && hex.All(Uri.IsHexDigit)
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2).ToUpperInvariant()))
            : string.Empty;
    }
}
