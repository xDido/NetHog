using System.IO;
using Microsoft.Win32;

namespace NetHog.Services;

public static class WindowsStartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "NetHog";
    private const string LegacyValueName = "WifiBox";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (!enabled)
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        var publishedExecutable = Path.Combine(AppContext.BaseDirectory, "NetHog.exe");
        if (File.Exists(publishedExecutable)) executablePath = publishedExecutable;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Windows could not determine the NetHog executable path.");
        }

        key?.SetValue(ValueName, $"\"{executablePath}\" --startup");
    }
}
