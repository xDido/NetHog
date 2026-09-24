using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class HistoryDialog : Window, INotifyPropertyChanged
{
    private readonly SessionHistoryStore _store = new();

    public HistoryDialog()
    {
        InitializeComponent();
        DataContext = this;
        Reload();
    }

    public ObservableCollection<HistoryRow> Records { get; } = new();
    public string StatusText => Records.Count == 0 ? "No completed sessions yet." : $"{Records.Count} completed session(s).";
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Reload()
    {
        Records.Clear();
        foreach (var record in _store.Load()) Records.Add(new HistoryRow(record));
        OnPropertyChanged(nameof(StatusText));
    }

    private void DeleteAll_Click(object? sender, RoutedEventArgs e)
    {
        _store.DeleteAll();
        Reload();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed class HistoryRow
    {
        public HistoryRow(SessionHistoryRecord record)
        {
            StartedText = record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            InterfaceText = $"{record.InterfaceName} · {record.LocalAddress}";
            DurationText = record.Duration.ToString(record.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            DeviceCountText = $"{record.Devices.Count} device(s)";
            DownloadText = $"↓ {FormatBytes(record.DownloadBytes)}";
            UploadText = $"↑ {FormatBytes(record.UploadBytes)}";
            ControlSummary = string.Join(" · ", record.Devices.Select(device => $"{device.DisplayName}: {device.ControlSummary}"));
        }

        public string StartedText { get; }
        public string InterfaceText { get; }
        public string DurationText { get; }
        public string DeviceCountText { get; }
        public string DownloadText { get; }
        public string UploadText { get; }
        public string ControlSummary { get; }

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1_000_000_000 => $"{bytes / 1_000_000_000d:0.0} GB",
            >= 1_000_000 => $"{bytes / 1_000_000d:0.0} MB",
            >= 1_000 => $"{bytes / 1_000d:0.0} KB",
            _ => $"{bytes:N0} B"
        };
    }
}
