using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using NetHog.Models;

namespace NetHog;

public partial class TrafficOverlayWindow : Window
{
    private TrafficOverlayPosition _position;
    private double _overlayOpacity;
    private bool _clickThrough;
    private string _screenDeviceName;
    private HwndSource? _source;

    public TrafficOverlayWindow(
        TrafficOverlayPosition position,
        double opacity,
        bool clickThrough,
        string? screenDeviceName)
    {
        _position = position;
        _overlayOpacity = Math.Clamp(opacity, 0.35, 1);
        _clickThrough = clickThrough;
        _screenDeviceName = screenDeviceName ?? string.Empty;
        InitializeComponent();
        Opacity = _overlayOpacity;
        Loaded += Window_Loaded;
        SourceInitialized += Window_SourceInitialized;
    }

    public void UpdateConfiguration(
        TrafficOverlayPosition position,
        double opacity,
        bool clickThrough,
        string? screenDeviceName)
    {
        _position = position;
        _overlayOpacity = Math.Clamp(opacity, 0.35, 1);
        _clickThrough = clickThrough;
        _screenDeviceName = screenDeviceName ?? string.Empty;
        Opacity = _overlayOpacity;
        ApplyClickThroughStyle();
        if (IsLoaded) PositionOverlay();
    }

    public void UpdateRates(double? downloadRateMbps, double? uploadRateMbps, TrafficRateUnit unit)
    {
        DownloadRateText.Text = downloadRateMbps is { } download
            ? FormatRate(download, unit)
            : "—";
        UploadRateText.Text = uploadRateMbps is { } upload
            ? FormatRate(upload, unit)
            : "—";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => PositionOverlay();

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WindowMessageHook);
        ApplyClickThroughStyle();
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void PositionOverlay()
    {
        const double margin = 14;
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var screen = screens.FirstOrDefault(candidate =>
                        candidate.DeviceName.Equals(_screenDeviceName, StringComparison.OrdinalIgnoreCase))
                     ?? Forms.Screen.PrimaryScreen
                     ?? screens[0];
        var dpi = VisualTreeHelper.GetDpi(this);
        var leftEdge = screen.WorkingArea.Left / dpi.DpiScaleX;
        var topEdge = screen.WorkingArea.Top / dpi.DpiScaleY;
        var rightEdge = screen.WorkingArea.Right / dpi.DpiScaleX;
        var bottomEdge = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        var workArea = new Rect(leftEdge, topEdge, rightEdge - leftEdge, bottomEdge - topEdge);
        var horizontal = _position switch
        {
            TrafficOverlayPosition.TopLeft or TrafficOverlayPosition.MiddleLeft or TrafficOverlayPosition.BottomLeft
                => workArea.Left + margin,
            TrafficOverlayPosition.TopCenter or TrafficOverlayPosition.BottomCenter
                => workArea.Left + (workArea.Width - Width) / 2,
            _ => workArea.Right - Width - margin
        };
        var vertical = _position switch
        {
            TrafficOverlayPosition.TopLeft or TrafficOverlayPosition.TopCenter or TrafficOverlayPosition.TopRight
                => workArea.Top + margin,
            TrafficOverlayPosition.MiddleLeft or TrafficOverlayPosition.MiddleRight
                => workArea.Top + (workArea.Height - Height) / 2,
            _ => workArea.Bottom - Height - margin
        };
        Left = Math.Clamp(horizontal, workArea.Left + margin, workArea.Right - Width - margin);
        Top = Math.Clamp(vertical, workArea.Top + margin, workArea.Bottom - Height - margin);
    }

    private void ApplyClickThroughStyle()
    {
        if (_source is null) return;
        var handle = _source.Handle;
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        if (_clickThrough)
        {
            style |= ExtendedTransparent | ExtendedNoActivate;
        }
        else
        {
            style &= ~ExtendedTransparent;
            style &= ~ExtendedNoActivate;
        }

        SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(style));
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_clickThrough && message == HitTestMessage)
        {
            handled = true;
            return new IntPtr(-1);
        }

        return IntPtr.Zero;
    }

    private static string FormatRate(double megabitsPerSecond, TrafficRateUnit unit)
    {
        if (unit == TrafficRateUnit.BytesPerSecond)
        {
            var bytesPerSecond = megabitsPerSecond * 1_000_000d / 8d;
            if (bytesPerSecond >= 1_000_000_000) return $"{bytesPerSecond / 1_000_000_000d:0.00} GB/s";
            if (bytesPerSecond >= 1_000_000) return $"{bytesPerSecond / 1_000_000d:0.00} MB/s";
            if (bytesPerSecond >= 1_000) return $"{bytesPerSecond / 1_000d:0.0} KB/s";
            return $"{bytesPerSecond:0} B/s";
        }

        if (megabitsPerSecond >= 1) return $"{megabitsPerSecond:0.00} Mbps";
        return $"{megabitsPerSecond * 1_000:0} Kbps";
    }

    private const int ExtendedStyleIndex = -20;
    private const long ExtendedTransparent = 0x00000020L;
    private const long ExtendedNoActivate = 0x08000000L;
    private const int HitTestMessage = 0x0084;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr",
        SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr",
        SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
