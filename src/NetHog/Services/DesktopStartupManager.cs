using System.Text;
using System.Reflection;
using NetHog.Services;

namespace NetHog.Avalonia.Services;

internal static class DesktopStartupManager
{
    private static string AutostartPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "nethog.desktop");

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsStartupManager.SetEnabled(enabled);
            return;
        }
        if (!OperatingSystem.IsLinux()) return;
        if (!enabled)
        {
            if (File.Exists(AutostartPath)) File.Delete(AutostartPath);
            return;
        }

        var command = BuildLinuxExecCommand();
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("The desktop session could not determine the NetHog executable path.");

        Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
        var desktopFile = new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine("Name=NetHog")
            .AppendLine("Comment=Network traffic control")
            .AppendLine($"Exec={command}")
            .AppendLine("Terminal=false")
            .AppendLine("X-GNOME-Autostart-enabled=true")
            .ToString();
        File.WriteAllText(AutostartPath, desktopFile);
    }

    private static string? BuildLinuxExecCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) return null;

        var entryAssembly = Assembly.GetEntryAssembly();
        var entryAssemblyPath = entryAssembly?.GetName().Name is { Length: > 0 } assemblyName
            ? Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")
            : null;
        var processName = Path.GetFileNameWithoutExtension(processPath);
        var isDotnetHost = processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                           || processName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost && !string.IsNullOrWhiteSpace(entryAssemblyPath) && File.Exists(entryAssemblyPath))
        {
            return $"\"{EscapeDesktopValue(processPath)}\" \"{EscapeDesktopValue(entryAssemblyPath)}\" --startup";
        }

        return $"\"{EscapeDesktopValue(processPath)}\" --startup";
    }

    private static string EscapeDesktopValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}
