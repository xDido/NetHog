using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using NetHog.Models;

namespace NetHog.Avalonia;

public partial class ChartDialog : Window, INotifyPropertyChanged
{
    private IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> _samples =
        new Dictionary<string, IReadOnlyList<TrafficSample>>(StringComparer.OrdinalIgnoreCase);
    private NetworkDevice? _selectedDevice;

    public ChartDialog() : this(Array.Empty<NetworkDevice>(),
        new Dictionary<string, IReadOnlyList<TrafficSample>>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public ChartDialog(
        IEnumerable<NetworkDevice> devices,
        IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> samples)
    {
        InitializeComponent();
        foreach (var device in devices) Devices.Add(device);
        _selectedDevice = Devices.FirstOrDefault();
        _samples = samples;
        DataContext = this;
        Opened += (_, _) => RenderChart();
    }

    public ObservableCollection<NetworkDevice> Devices { get; } = new();
    public NetworkDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (ReferenceEquals(_selectedDevice, value)) return;
            _selectedDevice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDevice)));
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateDevices(IEnumerable<NetworkDevice> devices)
    {
        var incoming = devices.ToArray();
        var selectedMac = _selectedDevice?.MacAddress;
        Devices.Clear();
        foreach (var device in incoming) Devices.Add(device);
        SelectedDevice = incoming.FirstOrDefault(device =>
            device.MacAddress.Equals(selectedMac, StringComparison.OrdinalIgnoreCase))
            ?? incoming.FirstOrDefault();
        RenderChart();
    }

    public void UpdateSamples(IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> samples)
    {
        _samples = samples;
        RenderChart();
    }

    public void RefreshTheme() => RenderChart();

    private void Device_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectedDevice = (sender as ComboBox)?.SelectedItem as NetworkDevice;
        RenderChart();
    }

    private void ChartCanvas_SizeChanged(object? sender, SizeChangedEventArgs e) => RenderChart();

    private void RenderChart()
    {
        if (!IsInitialized) return;
        ChartCanvas.Children.Clear();
        var selected = _selectedDevice ?? Devices.FirstOrDefault();
        var samples = selected is not null && _samples.TryGetValue(selected.MacAddress, out var values)
            ? values
            : Array.Empty<TrafficSample>();
        EmptyChartState.IsVisible = samples.Count == 0;
        if (samples.Count == 0)
        {
            ChartContextText.Text = selected is null ? "No device selected." : $"No samples for {selected.DisplayName} yet.";
            ChartMaxRateText.Text = "—";
            ChartMidRateText.Text = "—";
            ChartMinRateText.Text = "—";
            ChartStartTimeText.Text = "—";
            ChartMidTimeText.Text = "—";
            ChartEndTimeText.Text = "—";
            ChartMetricPanel.IsVisible = false;
            return;
        }

        var width = Math.Max(1, ChartCanvas.Bounds.Width);
        var height = Math.Max(1, ChartCanvas.Bounds.Height);
        var observedMaxRate = samples.Max(sample => Math.Max(sample.DownloadRateMbps, sample.UploadRateMbps));
        var maxRate = GetChartMaxRate(observedMaxRate);
        var plotWidth = Math.Max(1, width - 1);
        var plotHeight = Math.Max(1, height - 1);
        var axisColor = GetThemeColor("LineBrush", "#E2E7EC");
        var gridColor = GetThemeColor("TableAlternateBrush", "#EEF1F2");

        AddGuideLine(0, plotHeight, plotWidth, axisColor);
        AddGuideLine(0, plotHeight * 0.75, plotWidth, gridColor, true);
        AddGuideLine(0, plotHeight / 2, plotWidth, gridColor, true);
        AddGuideLine(0, plotHeight * 0.25, plotWidth, gridColor, true);
        AddGuideLine(0, 0, plotWidth, gridColor, true);
        AddVerticalGuideLine(plotWidth * 0.25, plotHeight, gridColor);
        AddVerticalGuideLine(plotWidth * 0.5, plotHeight, gridColor);
        AddVerticalGuideLine(plotWidth * 0.75, plotHeight, gridColor);
        AddPolyline(samples, plotWidth, plotHeight, maxRate, sample => sample.DownloadRateMbps,
            GetThemeColor("DownloadBrush", "#126B5A"));
        AddPolyline(samples, plotWidth, plotHeight, maxRate, sample => sample.UploadRateMbps,
            GetThemeColor("UploadBrush", "#946200"));

        var first = samples[0].At.ToLocalTime();
        var middle = samples[(samples.Count - 1) / 2].At.ToLocalTime();
        var last = samples[^1].At.ToLocalTime();
        ChartMaxRateText.Text = FormatRate(maxRate);
        ChartMidRateText.Text = FormatRate(maxRate / 2);
        ChartMinRateText.Text = FormatRate(0);
        ChartStartTimeText.Text = first.ToString("HH:mm:ss");
        ChartMidTimeText.Text = middle.ToString("HH:mm:ss");
        ChartEndTimeText.Text = last.ToString("HH:mm:ss");
        ChartContextText.Text = $"{selected?.DisplayName ?? "Selected device"} · {first:HH:mm:ss}–{last:HH:mm:ss} · {samples.Count} samples";
        ChartDownloadSummaryText.Text = $"↓ Download  {FormatRate(samples[^1].DownloadRateMbps)}";
        ChartUploadSummaryText.Text = $"↑ Upload  {FormatRate(samples[^1].UploadRateMbps)}";
        ChartMetricPanel.IsVisible = true;
    }

    private void AddPolyline(
        IReadOnlyList<TrafficSample> samples,
        double width,
        double height,
        double maxRate,
        Func<TrafficSample, double> selector,
        Color color)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2
        };
        for (var index = 0; index < samples.Count; index++)
        {
            var x = samples.Count == 1 ? 0 : width * index / (samples.Count - 1);
            var y = height - Math.Clamp(selector(samples[index]), 0, maxRate) / maxRate * height;
            line.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(line);
    }

    private void AddGuideLine(double x, double y, double width, Color color, bool dashed = false)
    {
        var line = new Line { StartPoint = new Point(x, y), EndPoint = new Point(x + width, y), Stroke = new SolidColorBrush(color), StrokeThickness = 1 };
        if (dashed) line.StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 2, 4 };
        ChartCanvas.Children.Add(line);
    }

    private void AddVerticalGuideLine(double x, double height, Color color)
    {
        var line = new Line { StartPoint = new Point(x, 0), EndPoint = new Point(x, height), Stroke = new SolidColorBrush(color), StrokeThickness = 1, StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 2, 4 } };
        ChartCanvas.Children.Add(line);
    }

    private static double GetChartMaxRate(double observedMaxRate)
    {
        if (observedMaxRate <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(observedMaxRate)));
        var normalized = observedMaxRate / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private static Color GetThemeColor(string key, string fallback) =>
        Application.Current?.Resources[key] is SolidColorBrush brush ? brush.Color : Color.Parse(fallback);

    private static string FormatRate(double value) => value >= 1
        ? $"{value:0.00} Mbps"
        : $"{value * 1_000:0.0} Kbps";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
