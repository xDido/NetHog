using System.Collections.ObjectModel;
using System.Windows;
using WifiBox.Models;
using WifiBox.Services;

namespace WifiBox;

public partial class HistoryWindow : Window
{
    private readonly TrafficDataUnit _dataUnit;
    private readonly ObservableCollection<SessionHistoryRecord> _sessions;

    public HistoryWindow(SessionHistoryStore store, TrafficDataUnit dataUnit)
    {
        InitializeComponent();
        _dataUnit = dataUnit;
        _sessions = new ObservableCollection<SessionHistoryRecord>(store.Load());
        SessionsGrid.ItemsSource = _sessions;
        if (_sessions.Count > 0) SessionsGrid.SelectedIndex = 0;
        else ShowEmptyState();
    }

    private void SessionsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionHistoryRecord session)
        {
            ShowEmptyState();
            return;
        }

        DetailTitleText.Text = session.InterfaceName;
        DetailSubtitleText.Text = $"{session.StartedAt:dd MMM yyyy HH:mm:ss} – {session.EndedAt:HH:mm:ss}  ·  {session.LocalAddress}  ·  gateway {session.GatewayAddress}";
        DownloadTotalText.Text = FormatBytes(session.DownloadBytes);
        UploadTotalText.Text = FormatBytes(session.UploadBytes);
        DevicesGrid.ItemsSource = session.Devices.Select(device => new HistoryDeviceRow(
            device.DisplayName,
            device.IpAddress,
            FormatBytes(device.DownloadBytes),
            FormatBytes(device.UploadBytes),
            device.ControlSummary)).ToArray();
    }

    private void ShowEmptyState()
    {
        DetailTitleText.Text = "No completed sessions yet";
        DetailSubtitleText.Text = "Start and stop a control session to build history for this PC.";
        DownloadTotalText.Text = "—";
        UploadTotalText.Text = "—";
        DevicesGrid.ItemsSource = Array.Empty<HistoryDeviceRow>();
    }

    private string FormatBytes(long bytes)
    {
        var divisor = _dataUnit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
        var kilobyte = divisor;
        var megabyte = divisor * divisor;
        var gigabyte = megabyte * divisor;
        if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "GiB" : "GB")}";
        if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
        if (bytes >= kilobyte) return $"{bytes / kilobyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
        return $"{bytes:N0} B";
    }

    private sealed record HistoryDeviceRow(
        string DisplayName,
        string IpAddress,
        string Download,
        string Upload,
        string ControlSummary);
}
