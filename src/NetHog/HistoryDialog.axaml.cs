using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class HistoryDialog : Window, INotifyPropertyChanged
{
    private readonly SessionHistoryStore _store = new();
    private readonly TrafficDataUnit _dataUnit;
    private readonly TimeSpan? _retention;
    private HistoryRow? _selectedRecord;
    private string _statusText = "History is stored as app data. Automatic cleanup is available in Settings.";

    public HistoryDialog() : this(TrafficDataUnit.Decimal, null)
    {
    }

    public HistoryDialog(TrafficDataUnit dataUnit, TimeSpan? retention = null)
    {
        _dataUnit = dataUnit;
        _retention = retention;
        InitializeComponent();
        DataContext = this;
        Reload();
    }

    public ObservableCollection<HistoryRow> Records { get; } = new();
    public ObservableCollection<HistoryDeviceRow> SelectedDevices { get; } = new();
    public HistoryRow? SelectedRecord
    {
        get => _selectedRecord;
        private set
        {
            if (ReferenceEquals(_selectedRecord, value)) return;
            _selectedRecord = value;
            SelectedDevices.Clear();
            if (value is not null)
            {
                foreach (var device in value.Devices) SelectedDevices.Add(device);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedDownloadText));
            OnPropertyChanged(nameof(SelectedUploadText));
        }
    }

    public string SelectedTitle => SelectedRecord?.Record.InterfaceName
        ?? (HasRecords ? "Select a session" : "No completed sessions yet");
    public string SelectedSubtitle => SelectedRecord is null
        ? "Start and stop a control session to build history for this PC."
        : $"{SelectedRecord.Record.StartedAt.ToLocalTime():dd MMM yyyy HH:mm:ss} – {SelectedRecord.Record.EndedAt.ToLocalTime():HH:mm:ss}  ·  {SelectedRecord.Record.LocalAddress}  ·  gateway {SelectedRecord.Record.GatewayAddress}";
    public string SelectedDownloadText => SelectedRecord is null ? "—" : FormatBytes(SelectedRecord.Record.DownloadBytes);
    public string SelectedUploadText => SelectedRecord is null ? "—" : FormatBytes(SelectedRecord.Record.UploadBytes);
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public bool HasRecords => Records.Count > 0;
    private IEnumerable<HistoryRow> SelectedRows => SessionsList.SelectedItems?.OfType<HistoryRow>() ?? Enumerable.Empty<HistoryRow>();
    public bool HasSelection => SessionsList.SelectedItems?.Count > 0;
    public bool CanCompare => SelectedRows.Take(3).Count() == 2;
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Reload()
    {
        Records.Clear();
        foreach (var record in _store.Load(_retention)) Records.Add(new HistoryRow(record, _dataUnit));
        if (Records.Count > 0)
        {
            SessionsList.SelectedIndex = 0;
            SelectedRecord = Records[0];
        }
        else
        {
            SelectedRecord = null;
        }
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCompare));
    }

    private void Sessions_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectedRecord = SelectedRows.FirstOrDefault();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCompare));
    }

    private async void Compare_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedRows.Take(2).ToArray();
        if (selected.Length != 2) return;
        await new SessionComparisonDialog(selected[0].Record, selected[1].Record, _dataUnit).ShowDialog(this);
    }

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedRows.FirstOrDefault();
        if (selected is null) return;
        var confirmed = await new ConfirmationDialog(
            "Delete session",
            $"Delete the session from {selected.Record.StartedAt.ToLocalTime():dd MMM yyyy HH:mm}? This cannot be undone.",
            "Delete session").ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            var selectedIndex = Records.IndexOf(selected);
            if (!_store.Delete(selected.Record.Id)) return;
            Records.Remove(selected);
            StatusText = "Selected session deleted.";
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            if (Records.Count == 0)
            {
                SelectedRecord = null;
            }
            else
            {
                SessionsList.SelectedIndex = Math.Min(selectedIndex, Records.Count - 1);
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Session could not be deleted: {exception.Message}";
        }
    }

    private async void DeleteAll_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasRecords) return;
        var confirmed = await new ConfirmationDialog(
            "Delete all history",
            $"Delete all {Records.Count} completed sessions? This cannot be undone.",
            "Delete all").ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            _store.DeleteAll();
            Records.Clear();
            SelectedRecord = null;
            StatusText = "All completed sessions deleted.";
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanCompare));
        }
        catch (Exception exception)
        {
            StatusText = $"History could not be deleted: {exception.Message}";
        }
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder()
            .AppendLine("Started,Ended,Duration,Interface,Local address,Gateway,Devices,Download bytes,Upload bytes");
        foreach (var row in Records)
        {
            var record = row.Record;
            csv.AppendLine(string.Join(",",
                Csv(record.StartedAt.ToString("O")), Csv(record.EndedAt.ToString("O")), Csv(record.Duration.ToString()),
                Csv(record.InterfaceName), Csv(record.LocalAddress), Csv(record.GatewayAddress), record.Devices.Count,
                record.DownloadBytes, record.UploadBytes));
        }
        await ExportTextAsync(csv.ToString(), "csv", "NetHog session history (*.csv)", "nethog-session-history.csv");
    }

    private async void ExportJson_Click(object? sender, RoutedEventArgs e)
    {
        var json = JsonSerializer.Serialize(Records.Select(row => row.Record).ToArray(), new JsonSerializerOptions { WriteIndented = true });
        await ExportTextAsync(json, "json", "NetHog session history (*.json)", "nethog-session-history.json");
    }

    private async Task ExportTextAsync(string content, string extension, string description, string fileName)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = fileName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes);
            StatusText = $"Exported {Records.Count} sessions to {file.Name}.";
        }
        catch (Exception exception)
        {
            StatusText = $"History export failed: {exception.Message}";
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private string FormatBytes(long bytes)
    {
        var divisor = _dataUnit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
        var megabyte = divisor * divisor;
        var gigabyte = megabyte * divisor;
        if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "GiB" : "GB")}";
        if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
        if (bytes >= divisor) return $"{bytes / divisor:0.0} {(_dataUnit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
        return $"{bytes:N0} B";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed class HistoryRow
    {
        public HistoryRow(SessionHistoryRecord record, TrafficDataUnit dataUnit)
        {
            Record = record;
            StartedText = record.StartedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm");
            InterfaceText = $"{record.InterfaceName} · {record.LocalAddress}";
            DurationText = record.Duration.ToString(record.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            DeviceCountText = $"{record.Devices.Count} devices";
            DownloadText = $"↓ {FormatBytes(record.DownloadBytes, dataUnit)}";
            UploadText = $"↑ {FormatBytes(record.UploadBytes, dataUnit)}";
            Devices = record.Devices.Select(device => new HistoryDeviceRow(device, dataUnit)).ToArray();
        }

        public SessionHistoryRecord Record { get; }
        public string StartedText { get; }
        public string InterfaceText { get; }
        public string DurationText { get; }
        public string DeviceCountText { get; }
        public string DownloadText { get; }
        public string UploadText { get; }
        public IReadOnlyList<HistoryDeviceRow> Devices { get; }

        private static string FormatBytes(long bytes, TrafficDataUnit unit)
        {
            var divisor = unit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
            var megabyte = divisor * divisor;
            var gigabyte = megabyte * divisor;
            if (bytes >= gigabyte) return $"{bytes / gigabyte:0.0} {(unit == TrafficDataUnit.Binary ? "GiB" : "GB")}";
            if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(unit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
            if (bytes >= divisor) return $"{bytes / divisor:0.0} {(unit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
            return $"{bytes:N0} B";
        }
    }

    public sealed class HistoryDeviceRow
    {
        public HistoryDeviceRow(SessionDeviceHistory device, TrafficDataUnit dataUnit)
        {
            DisplayName = device.DisplayName;
            IpAddress = device.IpAddress;
            ControlSummary = device.ControlSummary;
            DownloadText = FormatBytes(device.DownloadBytes, dataUnit);
            UploadText = FormatBytes(device.UploadBytes, dataUnit);
        }

        public string DisplayName { get; }
        public string IpAddress { get; }
        public string ControlSummary { get; }
        public string DownloadText { get; }
        public string UploadText { get; }

        private static string FormatBytes(long bytes, TrafficDataUnit unit)
        {
            var divisor = unit == TrafficDataUnit.Binary ? 1_024d : 1_000d;
            var megabyte = divisor * divisor;
            if (bytes >= megabyte) return $"{bytes / megabyte:0.0} {(unit == TrafficDataUnit.Binary ? "MiB" : "MB")}";
            if (bytes >= divisor) return $"{bytes / divisor:0.0} {(unit == TrafficDataUnit.Binary ? "KiB" : "KB")}";
            return $"{bytes:N0} B";
        }
    }
}
