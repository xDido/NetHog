using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetHog.Models;

namespace NetHog;

public partial class TrafficChartWindow : Window
{
    private readonly ObservableCollection<NetworkDevice> _devices = new();
    private IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> _samples =
        new Dictionary<string, IReadOnlyList<TrafficSample>>(StringComparer.OrdinalIgnoreCase);
    private string? _selectedMac;
    private bool _updatingSelection;

    public TrafficChartWindow()
    {
        InitializeComponent();
        DeviceComboBox.ItemsSource = _devices;
        App.ThemeChanged += App_ThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= App_ThemeChanged;
    }

    public void RefreshTheme() => RenderChart();

    private void App_ThemeChanged(object? sender, EventArgs e) => RenderChart();

    public void UpdateDevices(IEnumerable<NetworkDevice> devices)
    {
        var incoming = devices.ToArray();
        var selected = incoming.FirstOrDefault(device =>
            device.MacAddress.Equals(_selectedMac, StringComparison.OrdinalIgnoreCase))
            ?? incoming.FirstOrDefault();
        _devices.Clear();
        foreach (var device in incoming) _devices.Add(device);
        if (selected is not null)
        {
            _selectedMac = selected.MacAddress;
            _updatingSelection = true;
            DeviceComboBox.SelectedItem = selected;
            _updatingSelection = false;
        }
        RenderChart();
    }

    public void UpdateSamples(IReadOnlyDictionary<string, IReadOnlyList<TrafficSample>> samples)
    {
        _samples = samples;
        RenderChart();
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        _selectedMac = (DeviceComboBox.SelectedItem as NetworkDevice)?.MacAddress;
        RenderChart();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

    private void RenderChart()
    {
        if (!IsInitialized) return;
        ChartCanvas.Children.Clear();
        var selected = DeviceComboBox.SelectedItem as NetworkDevice
                       ?? _devices.FirstOrDefault(device =>
                           device.MacAddress.Equals(_selectedMac, StringComparison.OrdinalIgnoreCase));
        var samples = selected is not null && _samples.TryGetValue(selected.MacAddress, out var values)
            ? values
            : Array.Empty<TrafficSample>();
        EmptyChartState.Visibility = samples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (samples.Count == 0)
        {
            ChartContextText.Text = selected is null
                ? "No device selected."
                : $"No samples for {selected.DisplayName} yet.";
            ChartMaxRateText.Text = "—";
            ChartMidRateText.Text = "—";
            ChartMinRateText.Text = "—";
            ChartStartTimeText.Text = "—";
            ChartMidTimeText.Text = "—";
            ChartEndTimeText.Text = "—";
            ChartMetricPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var width = Math.Max(1, ChartCanvas.ActualWidth);
        var height = Math.Max(1, ChartCanvas.ActualHeight);
        var observedMaxRate = samples.Max(sample =>
            Math.Max(sample.DownloadRateMbps, sample.UploadRateMbps));
        var maxRate = GetChartMaxRate(observedMaxRate);
        var plotWidth = Math.Max(1, width - 1);
        var plotHeight = Math.Max(1, height - 1);
        var axisColor = GetThemeColor("LineBrush", "#E2E7EC");
        var gridColor = GetThemeColor("TableAlternateBrush", "#EEF1F2");

        AddGuideLine(ChartCanvas, 0, plotHeight, plotWidth, axisColor);
        AddGuideLine(ChartCanvas, 0, plotHeight * 0.75, plotWidth, gridColor, dashed: true);
        AddGuideLine(ChartCanvas, 0, plotHeight / 2, plotWidth, gridColor, dashed: true);
        AddGuideLine(ChartCanvas, 0, plotHeight * 0.25, plotWidth, gridColor, dashed: true);
        AddGuideLine(ChartCanvas, 0, 0, plotWidth, gridColor, dashed: true);
        AddVerticalGuideLine(ChartCanvas, plotWidth * 0.25, plotHeight, gridColor);
        AddVerticalGuideLine(ChartCanvas, plotWidth * 0.5, plotHeight, gridColor);
        AddVerticalGuideLine(ChartCanvas, plotWidth * 0.75, plotHeight, gridColor);
        AddPolyline(samples, plotWidth, plotHeight, maxRate, sample => sample.DownloadRateMbps, GetThemeColor("DownloadBrush", "#126B5A"));
        AddPolyline(samples, plotWidth, plotHeight, maxRate, sample => sample.UploadRateMbps, GetThemeColor("UploadBrush", "#946200"));

        var first = samples[0].At.LocalDateTime;
        var middle = samples[(samples.Count - 1) / 2].At.LocalDateTime;
        var last = samples[^1].At.LocalDateTime;
        ChartMaxRateText.Text = FormatRate(maxRate);
        ChartMidRateText.Text = FormatRate(maxRate / 2);
        ChartMinRateText.Text = FormatRate(0);
        ChartStartTimeText.Text = first.ToString("HH:mm:ss");
        ChartMidTimeText.Text = middle.ToString("HH:mm:ss");
        ChartEndTimeText.Text = last.ToString("HH:mm:ss");
        ChartContextText.Text =
            $"{selected?.DisplayName ?? "Selected device"} · {first:HH:mm:ss}–{last:HH:mm:ss} · {samples.Count} samples";
        ChartDownloadSummaryText.Text = $"↓ Download  {FormatRate(samples[^1].DownloadRateMbps)}";
        ChartUploadSummaryText.Text = $"↑ Upload  {FormatRate(samples[^1].UploadRateMbps)}";
        ChartMetricPanel.Visibility = Visibility.Visible;
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
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (var index = 0; index < samples.Count; index++)
        {
            var x = samples.Count == 1 ? 0 : width * index / (samples.Count - 1);
            var y = height - (Math.Clamp(selector(samples[index]), 0, maxRate) / maxRate * height);
            line.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(line);
    }

    private static void AddGuideLine(Canvas canvas, double x, double y, double width, Color color, bool dashed = false)
    {
        var line = new Line
        {
            X1 = x,
            Y1 = y,
            X2 = x + width,
            Y2 = y,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1
        };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 2, 4 };
        canvas.Children.Add(line);
    }

    private static void AddVerticalGuideLine(Canvas canvas, double x, double height, Color color)
    {
        var line = new Line
        {
            X1 = x,
            Y1 = 0,
            X2 = x,
            Y2 = height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 4 }
        };
        canvas.Children.Add(line);
    }

    private static double GetChartMaxRate(double observedMaxRate)
    {
        if (observedMaxRate <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(observedMaxRate)));
        var normalized = observedMaxRate / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private static Color GetThemeColor(string key, string fallback)
    {
        return Application.Current.Resources[key] is SolidColorBrush brush
            ? brush.Color
            : (Color)ColorConverter.ConvertFromString(fallback);
    }

    private static string FormatRate(double megabitsPerSecond) =>
        megabitsPerSecond >= 1
            ? $"{megabitsPerSecond:0.00} Mbps"
            : $"{megabitsPerSecond * 1_000:0.0} Kbps";
}
