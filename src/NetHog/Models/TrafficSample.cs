namespace NetHog.Models;

public sealed record TrafficSample(
    DateTimeOffset At,
    long DownloadBytes,
    long UploadBytes,
    double DownloadRateMbps,
    double UploadRateMbps);
