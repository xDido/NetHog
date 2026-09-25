using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace NetHog.Avalonia;

internal static class WindowsTitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window, bool darkMode)
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var captionColor = ToColorRef(darkMode ? "#11171B" : "#F4F6F8");
        var textColor = ToColorRef(darkMode ? "#E9F1EF" : "#18212B");
        var borderColor = ToColorRef(darkMode ? "#304046" : "#E2E7EC");
        var darkModeValue = darkMode ? 1u : 0u;

        SetAttribute(handle, DwmwaUseImmersiveDarkMode, darkModeValue);
        SetAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, darkModeValue);
        SetAttribute(handle, DwmwaCaptionColor, captionColor);
        SetAttribute(handle, DwmwaTextColor, textColor);
        SetAttribute(handle, DwmwaBorderColor, borderColor);
    }

    private static uint ToColorRef(string hex)
    {
        var color = hex[1..];
        var red = Convert.ToUInt32(color[..2], 16);
        var green = Convert.ToUInt32(color[2..4], 16);
        var blue = Convert.ToUInt32(color[4..6], 16);
        return red | (green << 8) | (blue << 16);
    }

    private static void SetAttribute(IntPtr handle, int attribute, uint value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref uint value,
        int valueSize);
}
