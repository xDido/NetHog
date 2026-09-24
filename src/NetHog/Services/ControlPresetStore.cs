using System.IO;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

public sealed class ControlPresetStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetHog",
        "control-presets.json");
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiBox",
        "control-presets.json");

    public IReadOnlyList<ControlPreset> Load()
    {
        List<ControlPreset>? saved = null;
        try
        {
            var sourcePath = File.Exists(_path) ? _path : _legacyPath;
            if (File.Exists(sourcePath))
            {
                saved = JsonSerializer.Deserialize<List<ControlPreset>>(File.ReadAllText(sourcePath));
            }
        }
        catch
        {
            // Fall back to the built-in presets if app data is unavailable or malformed.
        }

        var result = new List<ControlPreset>();
        foreach (var builtIn in ControlPresetCatalog.All)
        {
            if (ControlPresetCatalog.IsSystemPreset(builtIn.Id))
            {
                result.Add(builtIn);
                continue;
            }

            var savedPreset = saved?.FirstOrDefault(preset =>
                preset.Id.Equals(builtIn.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(savedPreset is not null && IsValidPreset(savedPreset) ? savedPreset : builtIn);
        }

        if (saved is not null)
        {
            foreach (var preset in saved.Where(IsValidPreset))
            {
                if (ControlPresetCatalog.IsSystemPreset(preset.Id)
                    || result.Any(existing => existing.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(preset);
            }
        }

        return result;
    }

    public void Save(IEnumerable<ControlPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var normalized = presets
            .Where(preset => !ControlPresetCatalog.IsSystemPreset(preset.Id))
            .Where(IsValidPreset)
            .GroupBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static bool IsValidPreset(ControlPreset preset) =>
        !string.IsNullOrWhiteSpace(preset.Id)
        && !string.IsNullOrWhiteSpace(preset.Name)
        && (preset.DownloadLimitMbps is null or >= 1 and <= 10_000)
        && (preset.UploadLimitMbps is null or >= 1 and <= 10_000)
        && (preset.BlockInternet || preset.DownloadLimitMbps is not null || preset.UploadLimitMbps is not null);
}
