using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using NetHog.Models;
using System.Runtime.InteropServices;

namespace NetHog.Avalonia;

public partial class TrafficOverlayWindow : Window
{
    private TrafficOverlayPosition _position;
    private double _overlayOpacity;
    private bool _clickThrough;
    private string _screenName;

    public TrafficOverlayWindow()
        : this(TrafficOverlayPosition.BottomRight, 0.78, false, null)
    {
    }

    public TrafficOverlayWindow(
        TrafficOverlayPosition position,
        double opacity,
        bool clickThrough,
        string? screenName)
    {
        _position = position;
        _overlayOpacity = Math.Clamp(opacity, 0.35, 1);
        _clickThrough = clickThrough;
        _screenName = screenName ?? string.Empty;
        InitializeComponent();
        Opacity = _overlayOpacity;
        IsHitTestVisible = !_clickThrough;
        Opened += (_, _) =>
        {
            ApplyLinuxUtilityWindowHints();
            PositionOverlay();
        };
    }

    public void UpdateConfiguration(
        TrafficOverlayPosition position,
        double opacity,
        bool clickThrough,
        string? screenName)
    {
        _position = position;
        _overlayOpacity = Math.Clamp(opacity, 0.35, 1);
        _clickThrough = clickThrough;
        _screenName = screenName ?? string.Empty;
        Opacity = _overlayOpacity;
        IsHitTestVisible = !_clickThrough;
        if (IsVisible) PositionOverlay();
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

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_clickThrough && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void PositionOverlay()
    {
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0) return;
        var selected = screens
            .Select((candidate, index) => new { candidate, index })
            .FirstOrDefault(item => string.Equals(
                GetScreenName(item.candidate, item.index),
                _screenName,
                StringComparison.OrdinalIgnoreCase));
        var screen = selected?.candidate ?? Screens?.Primary ?? screens[0];
        if (screen is null) return;

        var scale = Math.Max(1, screen.Scaling);
        var area = screen.WorkingArea;
        var width = Width * scale;
        var height = Height * scale;
        var margin = 14 * scale;
        var left = _position switch
        {
            TrafficOverlayPosition.TopLeft or TrafficOverlayPosition.MiddleLeft or TrafficOverlayPosition.BottomLeft
                => area.X + margin,
            TrafficOverlayPosition.TopCenter or TrafficOverlayPosition.BottomCenter
                => area.X + (area.Width - width) / 2,
            _ => area.Right - width - margin
        };
        var top = _position switch
        {
            TrafficOverlayPosition.TopLeft or TrafficOverlayPosition.TopCenter or TrafficOverlayPosition.TopRight
                => area.Y + margin,
            TrafficOverlayPosition.MiddleLeft or TrafficOverlayPosition.MiddleRight
                => area.Y + (area.Height - height) / 2,
            _ => area.Bottom - height - margin
        };

        Position = new PixelPoint(
            (int)Math.Round(Math.Clamp(left, area.X + margin, area.Right - width - margin)),
            (int)Math.Round(Math.Clamp(top, area.Y + margin, area.Bottom - height - margin)));
    }

    private static string GetScreenName(Screen screen, int index) =>
        string.IsNullOrWhiteSpace(screen.DisplayName) ? $"screen-{index + 1}" : screen.DisplayName;

    private void ApplyLinuxUtilityWindowHints()
    {
        if (!OperatingSystem.IsLinux()) return;

        var platformHandle = TryGetPlatformHandle();
        if (platformHandle is null
            || platformHandle.Handle == IntPtr.Zero
            || !string.Equals(platformHandle.HandleDescriptor, "XID", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            X11WindowHints.ApplyUtilityHints(platformHandle.Handle);
        }
        catch (DllNotFoundException)
        {
            // The native X11 library is not present on native Wayland setups.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the overlay usable if the display server does not expose Xlib.
        }
    }

    private static class X11WindowHints
    {
        private const int PropModeReplace = 0;

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XInternAtom(
            IntPtr display,
            [MarshalAs(UnmanagedType.LPStr)] string atomName,
            bool onlyIfExists);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XChangeProperty(
            IntPtr display,
            IntPtr window,
            IntPtr property,
            IntPtr type,
            int format,
            int mode,
            IntPtr[] data,
            int elementCount);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XFlush(IntPtr display);

        [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int XCloseDisplay(IntPtr display);

        public static void ApplyUtilityHints(IntPtr window)
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;

            try
            {
                var atomType = XInternAtom(display, "ATOM", false);
                var windowType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
                var utilityType = XInternAtom(display, "_NET_WM_WINDOW_TYPE_UTILITY", false);
                var state = XInternAtom(display, "_NET_WM_STATE", false);
                var skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
                var skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);

                XChangeProperty(
                    display,
                    window,
                    windowType,
                    atomType,
                    32,
                    PropModeReplace,
                    [utilityType],
                    1);
                XChangeProperty(
                    display,
                    window,
                    state,
                    atomType,
                    32,
                    PropModeReplace,
                    [skipTaskbar, skipPager],
                    2);
                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
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

        return megabitsPerSecond >= 1
            ? $"{megabitsPerSecond:0.00} Mbps"
            : $"{megabitsPerSecond * 1_000:0} Kbps";
    }
}
