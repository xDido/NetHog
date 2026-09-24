using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Models;

namespace NetHog.Avalonia;

public partial class ChartDialog : Window
{
    public ChartDialog() : this(Array.Empty<TrafficSample>())
    {
    }

    public ChartDialog(IEnumerable<TrafficSample> samples)
    {
        InitializeComponent();
        foreach (var sample in samples.OrderByDescending(sample => sample.At).Take(120)) Samples.Add(new SampleRow(sample));
        DataContext = this;
    }

    public ObservableCollection<SampleRow> Samples { get; } = new();
    public string LatestDownloadText => Samples.FirstOrDefault()?.DownloadText ?? "↓ No samples";
    public string LatestUploadText => Samples.FirstOrDefault()?.UploadText ?? "↑ No samples";
    public string StatusText => Samples.Count == 0 ? "Start a session to collect traffic samples." : $"{Samples.Count} samples retained in memory.";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    public sealed class SampleRow
    {
        public SampleRow(TrafficSample sample)
        {
            TimeText = sample.At.ToLocalTime().ToString("HH:mm:ss");
            DownloadText = $"↓ {FormatRate(sample.DownloadRateMbps)}";
            UploadText = $"↑ {FormatRate(sample.UploadRateMbps)}";
        }

        public string TimeText { get; }
        public string DownloadText { get; }
        public string UploadText { get; }

        private static string FormatRate(double value) => value >= 1
            ? $"{value:0.00} Mbps"
            : $"{value * 1_000:0.0} Kbps";
    }
}
