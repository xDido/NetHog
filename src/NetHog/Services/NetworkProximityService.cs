using System.Net;
using System.Net.NetworkInformation;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Measures network proximity with a short ICMP round-trip sample. This is
/// intentionally not presented as physical distance: that requires Wi-Fi CSI
/// or another RF sensor that ordinary Windows adapters do not expose.
/// </summary>
public sealed class NetworkProximityService
{
    public async Task MeasureAsync(IEnumerable<NetworkDevice> devices, CancellationToken cancellationToken = default)
    {
        var measurements = devices.Select(device => MeasureDeviceAsync(device, cancellationToken));
        await Task.WhenAll(measurements);
    }

    private static async Task MeasureDeviceAsync(NetworkDevice device, CancellationToken cancellationToken)
    {
        if (device.IsLocalDevice)
        {
            device.SetProximity("This PC", "This is the computer running NetHog.");
            return;
        }

        var targetAddress = string.IsNullOrWhiteSpace(device.IpAddress)
            ? device.NameLookupAddress
            : device.IpAddress;
        if (!IPAddress.TryParse(targetAddress, out var address))
        {
            device.SetProximity("Unavailable", "No IPv4 address is available for a proximity sample.");
            return;
        }

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(
                address,
                TimeSpan.FromMilliseconds(650),
                new byte[32],
                new PingOptions(),
                cancellationToken);
            if (reply.Status != IPStatus.Success)
            {
                device.SetProximity("No response", "The device did not answer the short network proximity sample.");
                return;
            }

            var milliseconds = reply.RoundtripTime;
            var label = milliseconds < 2
                ? "Same LAN · <2 ms"
                : $"Same LAN · {milliseconds} ms";
            device.SetProximity(
                label,
                $"Network round-trip latency from this PC: {milliseconds} ms. This does not measure physical distance; physical distance needs Wi-Fi CSI or RF sensors.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            device.SetProximity("Unavailable", "The device could not be sampled for network proximity.");
        }
    }
}
