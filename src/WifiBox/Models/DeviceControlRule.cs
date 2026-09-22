namespace WifiBox.Models;

public sealed record DeviceControlRule(
    string DeviceMacAddress,
    int? DownloadLimitMbps,
    int? UploadLimitMbps,
    bool BlockInternet);
