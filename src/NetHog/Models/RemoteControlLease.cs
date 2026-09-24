namespace NetHog.Models;

public sealed record RemoteControlLease(
    string ControllerId,
    string ControllerName,
    string ControllerAddress,
    string TargetMacAddress,
    string TargetIpAddress,
    int? DownloadLimitMbps,
    int? UploadLimitMbps,
    bool BlockInternet,
    DateTimeOffset? ExpiresAtUtc,
    string SessionId,
    string LeaseId,
    DateTimeOffset LastSeenUtc)
{
    public string IdentityKey =>
        $"{ControllerId}:{SessionId}:{LeaseId}:{TargetMacAddress}";

    public bool IsExpired =>
        ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow;

    public string ControlSummary
    {
        get
        {
            var parts = new List<string>();
            if (BlockInternet) parts.Add("Blocked");
            if (DownloadLimitMbps is { } download) parts.Add($"↓ {download} Mbps");
            if (UploadLimitMbps is { } upload) parts.Add($"↑ {upload} Mbps");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record RemoteControlReleaseRequest(
    string ControllerId,
    string SessionId,
    string LeaseId,
    string TargetMacAddress,
    string RequesterMacAddress,
    string RequesterIpAddress);
