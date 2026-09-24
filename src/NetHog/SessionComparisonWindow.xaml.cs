using System.Windows;
using NetHog.Models;

namespace NetHog;

public partial class SessionComparisonWindow : Window
{
    private readonly TrafficDataUnit _dataUnit;

    public SessionComparisonWindow(
        SessionHistoryRecord first,
        SessionHistoryRecord second,
        TrafficDataUnit dataUnit)
    {
        InitializeComponent();
        _dataUnit = dataUnit;
        SetColumn(first, FirstDateText, FirstNetworkText, FirstDurationText, FirstDevicesText, FirstDownloadText, FirstUploadText);
        SetColumn(second, SecondDateText, SecondNetworkText, SecondDurationText, SecondDevicesText, SecondDownloadText, SecondUploadText);
    }

    private void SetColumn(
        SessionHistoryRecord session,
        System.Windows.Controls.TextBlock dateText,
        System.Windows.Controls.TextBlock networkText,
        System.Windows.Controls.TextBlock durationText,
        System.Windows.Controls.TextBlock devicesText,
        System.Windows.Controls.TextBlock downloadText,
        System.Windows.Controls.TextBlock uploadText)
    {
        dateText.Text = session.StartedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
        networkText.Text = $"{session.InterfaceName} · {session.LocalAddress} · gateway {session.GatewayAddress}";
        durationText.Text = $"Duration  {session.Duration:hh\\:mm\\:ss}";
        devicesText.Text = $"Devices  {session.Devices.Count}";
        downloadText.Text = $"↓ Download  {FormatBytes(session.DownloadBytes)}";
        uploadText.Text = $"↑ Upload  {FormatBytes(session.UploadBytes)}";
    }

    private string FormatBytes(long bytes)
    {
        var divisor = _dataUnit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
        var megabyte = divisor * divisor;
        var gigabyte = megabyte * divisor;
        if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "GiB" : "GB")}";
        if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
        return $"{bytes / divisor:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
    }
}
