namespace NetHog.Models;

public sealed record ControlPreset(
    string Id,
    string Name,
    string Description,
    int? DownloadLimitMbps,
    int? UploadLimitMbps,
    bool BlockInternet);

public static class ControlPresetCatalog
{
    public static IReadOnlyList<ControlPreset> All { get; } =
    [
        new("custom", "Custom", "Choose your own limits or block.", null, null, false),
        new("pause", "Pause internet", "Block routed IPv4 internet traffic.", null, null, true),
        new("light", "Light limit", "Keep the device online at 5 Mbps each way.", 5, 5, false),
        new("strict", "Strict limit", "Keep the device online at 1 Mbps each way.", 1, 1, false)
    ];

    public static ControlPreset Find(string? id) =>
        All.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static ControlPreset Find(string? id, IEnumerable<ControlPreset> presets) =>
        presets.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? presets.FirstOrDefault(preset => preset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static bool IsSystemPreset(string? id) =>
        string.Equals(id, "custom", StringComparison.OrdinalIgnoreCase);

    public static bool IsBuiltIn(string? id) =>
        !IsSystemPreset(id)
        && All.Any(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string CreateCustomId() => $"custom-{Guid.NewGuid():N}";
}
