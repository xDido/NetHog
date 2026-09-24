using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class HistoryWindow : Window
{
    private readonly SessionHistoryStore _store;
    private readonly TrafficDataUnit _dataUnit;
    private readonly ObservableCollection<SessionHistoryRecord> _sessions;

    public HistoryWindow(SessionHistoryStore store, TrafficDataUnit dataUnit, TimeSpan? retention = null)
    {
        _store = store;
        InitializeComponent();
        _dataUnit = dataUnit;
        _sessions = new ObservableCollection<SessionHistoryRecord>(store.Load(retention));
        SessionsGrid.ItemsSource = _sessions;
        if (_sessions.Count > 0) SessionsGrid.SelectedIndex = 0;
        else ShowEmptyState();
        UpdateHistoryActions();
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
        UpdateHistoryActions();
    }

    private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionHistoryRecord session) return;

        var result = MessageBox.Show(this,
            $"Delete the session from {session.StartedAt:dd MMM yyyy HH:mm}? This cannot be undone.",
            "Delete session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var selectedIndex = SessionsGrid.SelectedIndex;
            if (!_store.Delete(session.Id)) return;
            _sessions.Remove(session);
            if (_sessions.Count == 0)
            {
                ShowEmptyState();
                return;
            }

            SessionsGrid.SelectedIndex = Math.Min(selectedIndex, _sessions.Count - 1);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Session could not be deleted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteAllHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.Count == 0) return;

        var result = MessageBox.Show(this,
            $"Delete all {_sessions.Count} completed sessions? This cannot be undone.",
            "Delete all history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _store.DeleteAll();
            _sessions.Clear();
            ShowEmptyState();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "History could not be deleted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowEmptyState()
    {
        DetailTitleText.Text = "No completed sessions yet";
        DetailSubtitleText.Text = "Start and stop a control session to build history for this PC.";
        DownloadTotalText.Text = "—";
        UploadTotalText.Text = "—";
        DevicesGrid.ItemsSource = Array.Empty<HistoryDeviceRow>();
        UpdateHistoryActions();
    }

    private void UpdateHistoryActions()
    {
        DeleteSessionButton.IsEnabled = SessionsGrid.SelectedItem is SessionHistoryRecord;
        DeleteAllHistoryButton.IsEnabled = _sessions.Count > 0;
        CompareSessionsButton.IsEnabled = SessionsGrid.SelectedItems.Count == 2;
    }

    private void CompareSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SessionsGrid.SelectedItems
            .OfType<SessionHistoryRecord>()
            .Take(2)
            .ToArray();
        if (selected.Length != 2) return;
        new SessionComparisonWindow(selected[0], selected[1], _dataUnit) { Owner = this }.ShowDialog();
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"nethog-session-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Started,Ended,Duration,Interface,Local address,Gateway,Devices,Download bytes,Upload bytes");
            foreach (var session in _sessions)
            {
                builder.AppendLine(string.Join(",",
                    Csv(session.StartedAt.ToString("O")),
                    Csv(session.EndedAt.ToString("O")),
                    Csv(session.Duration.ToString()),
                    Csv(session.InterfaceName),
                    Csv(session.LocalAddress),
                    Csv(session.GatewayAddress),
                    session.Devices.Count,
                    session.DownloadBytes,
                    session.UploadBytes));
            }
            File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
            SetStatus($"Exported {_sessions.Count} sessions to {dialog.FileName}.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "History export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON file (*.json)|*.json",
            FileName = $"nethog-session-history-{DateTime.Now:yyyyMMdd-HHmm}.json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_sessions,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            SetStatus($"Exported {_sessions.Count} sessions to {dialog.FileName}.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "History export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void SetStatus(string message)
    {
        HistoryStatusText.Text = message;
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
