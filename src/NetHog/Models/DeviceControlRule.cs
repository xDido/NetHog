namespace NetHog.Models;

public sealed record DeviceControlRule(
    string DeviceMacAddress,
    int? DownloadLimitMbps,
    int? UploadLimitMbps,
    bool BlockInternet,
    int? DurationMinutes = null,
    string? PresetId = null);
