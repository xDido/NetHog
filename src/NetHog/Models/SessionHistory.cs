namespace NetHog.Models;

public sealed record SessionDeviceHistory(
    string MacAddress,
    string DisplayName,
    string IpAddress,
    long DownloadBytes,
    long UploadBytes,
    string ControlSummary);

public sealed record SessionHistoryRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string InterfaceName,
    string LocalAddress,
    string GatewayAddress,
    IReadOnlyList<SessionDeviceHistory> Devices)
{
    public TimeSpan Duration => EndedAt - StartedAt;
    public long DownloadBytes => Devices.Sum(device => device.DownloadBytes);
    public long UploadBytes => Devices.Sum(device => device.UploadBytes);
}
