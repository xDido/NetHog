using Avalonia.Controls;
using NetHog.Models;

namespace NetHog.Avalonia;

public partial class SessionComparisonDialog : Window
{
    public SessionComparisonDialog() : this(
        new SessionHistoryRecord(Guid.Empty, DateTimeOffset.Now, DateTimeOffset.Now, "", "", "", []),
        new SessionHistoryRecord(Guid.Empty, DateTimeOffset.Now, DateTimeOffset.Now, "", "", "", []),
        TrafficDataUnit.Decimal)
    {
    }

    public SessionComparisonDialog(SessionHistoryRecord first, SessionHistoryRecord second, TrafficDataUnit dataUnit)
    {
        InitializeComponent();
        First = new SessionSummary(first, dataUnit);
        Second = new SessionSummary(second, dataUnit);
        DataContext = this;
    }

    public SessionSummary First { get; }
    public SessionSummary Second { get; }

    public sealed class SessionSummary
    {
        public SessionSummary(SessionHistoryRecord record, TrafficDataUnit dataUnit)
        {
            DateText = record.StartedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
            NetworkText = $"{record.InterfaceName} · {record.LocalAddress} · gateway {record.GatewayAddress}";
            DurationText = $"Duration  {record.Duration:hh\\:mm\\:ss}";
            DevicesText = $"Devices  {record.Devices.Count}";
            DownloadText = $"↓ Download  {FormatBytes(record.DownloadBytes, dataUnit)}";
            UploadText = $"↑ Upload  {FormatBytes(record.UploadBytes, dataUnit)}";
        }

        public string DateText { get; }
        public string NetworkText { get; }
        public string DurationText { get; }
        public string DevicesText { get; }
        public string DownloadText { get; }
        public string UploadText { get; }

        private static string FormatBytes(long bytes, TrafficDataUnit unit)
        {
            var divisor = unit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
            var megabyte = divisor * divisor;
            var gigabyte = megabyte * divisor;
            if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {(unit == TrafficDataUnit.Binary ? "GiB" : "GB")}";
            if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(unit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
            return $"{bytes / divisor:0.0} {(unit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
        }
    }
}
