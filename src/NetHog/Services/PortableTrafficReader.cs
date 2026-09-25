using System.Net.NetworkInformation;

namespace NetHog.Avalonia.Services;

public sealed class PortableTrafficReader
{
    public (long DownloadBytes, long UploadBytes) Read(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName)) return (0, 0);
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate => candidate.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
        if (adapter is null) return (0, 0);
        try
        {
            var statistics = adapter.GetIPv4Statistics();
            return (statistics.BytesReceived, statistics.BytesSent);
        }
        catch
        {
            return (0, 0);
        }
    }
}
