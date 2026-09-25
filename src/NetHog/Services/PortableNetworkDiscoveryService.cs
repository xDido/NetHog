using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NetHog.Models;

namespace NetHog.Avalonia.Services;

/// <summary>
/// Network discovery that uses the .NET adapter APIs and the native neighbor
/// table on both Windows and Linux. It deliberately avoids a shell so adapter
/// names containing spaces remain safe.
/// </summary>
public sealed class PortableNetworkDiscoveryService
{
    private static readonly Regex LinuxNeighborPattern = new(
        @"^\s*(?<ip>[^\s]+)\s+dev\s+(?<dev>[^\s]+)(?:\s+lladdr\s+(?<mac>[^\s]+))?\s*(?<state>[^\s]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsNeighborPattern = new(
        @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9a-f:-]{11,17})\s+(?<state>\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<NetworkAdapterOption> GetAdapters()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsCandidateAdapter)
            .Select(CreateAdapterOption)
            .Where(option => option is not null)
            .Cast<NetworkAdapterOption>()
            .ToList();

        var defaultIndex = adapters.FindIndex(option => !string.IsNullOrWhiteSpace(option.GatewayAddress));
        return adapters.Select((option, index) => option with { IsDefaultRoute = index == (defaultIndex < 0 ? 0 : defaultIndex) }).ToList();
    }

    public async Task<NetworkSnapshot> ScanAsync(
        string? interfaceId,
        CancellationToken cancellationToken = default)
    {
        var adapter = FindAdapter(interfaceId);
        var properties = adapter.GetIPProperties();
        var address = properties.UnicastAddresses.FirstOrDefault(info =>
            info.Address.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) throw new InvalidOperationException("The selected adapter has no IPv4 address.");

        var gateway = properties.GatewayAddresses.FirstOrDefault(info =>
            info.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
        if (gateway is null) throw new InvalidOperationException("The selected adapter has no IPv4 gateway.");

        var localMac = FormatMac(adapter.GetPhysicalAddress());
        var neighbors = await ReadNeighborTableAsync(adapter.Name, cancellationToken);
        // The neighbor table is only a cache. Phones often sleep, roam, or
        // rotate their private Wi-Fi MAC, so an existing table can still be
        // incomplete. Probe the selected subnet on every scan to refresh ARP
        // entries before building the device list.
        await ProbeSubnetAsync(address.Address, address.PrefixLength, cancellationToken);
        neighbors = await ReadNeighborTableAsync(adapter.Name, cancellationToken);

        var gatewayMac = neighbors.FirstOrDefault(neighbor =>
            neighbor.IpAddress.Equals(gateway.ToString(), StringComparison.OrdinalIgnoreCase))?.MacAddress ?? string.Empty;
        var devices = new List<NetworkDevice>
        {
            new(address.Address.ToString(), localMac, "local", "This PC", isLocalDevice: true)
        };

        var remoteNeighbors = neighbors
            .Where(neighbor => !neighbor.IpAddress.Equals(address.Address.ToString(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(neighbor => neighbor.MacAddress, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        using var nameGate = new SemaphoreSlim(16);
        var remoteDevices = await Task.WhenAll(remoteNeighbors.Select(async neighbor =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await nameGate.WaitAsync(cancellationToken);
            try
            {
                var displayName = await ResolveNameAsync(neighbor.IpAddress, cancellationToken);
                return new NetworkDevice(
                    neighbor.IpAddress,
                    neighbor.MacAddress,
                    neighbor.State,
                    displayName ?? $"Device {neighbor.IpAddress}");
            }
            finally
            {
                nameGate.Release();
            }
        }));
        devices.AddRange(remoteDevices);

        var hasIpv6 = properties.UnicastAddresses.Any(info =>
            info.Address.AddressFamily == AddressFamily.InterNetworkV6 && !info.Address.IsIPv6LinkLocal);
        return new NetworkSnapshot(
            adapter.Name,
            adapter.Description,
            GetInterfaceId(adapter),
            address.Address.ToString(),
            localMac,
            gateway.ToString(),
            gatewayMac,
            address.PrefixLength,
            hasIpv6,
            Math.Max(0, devices.Count - 1),
            DateTimeOffset.Now,
            devices);
    }

    private static NetworkInterface FindAdapter(string? interfaceId)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsCandidateAdapter)
            .FirstOrDefault(candidate =>
                string.IsNullOrWhiteSpace(interfaceId) ||
                string.Equals(GetInterfaceId(candidate), interfaceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Name, interfaceId, StringComparison.OrdinalIgnoreCase));
        return adapter ?? throw new InvalidOperationException("The selected network adapter is no longer available.");
    }

    private static NetworkAdapterOption? CreateAdapterOption(NetworkInterface adapter)
    {
        try
        {
            var properties = adapter.GetIPProperties();
            var address = properties.UnicastAddresses.FirstOrDefault(info =>
                info.Address.AddressFamily == AddressFamily.InterNetwork);
            var gateway = properties.GatewayAddresses.FirstOrDefault(info =>
                info.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (address is null || gateway is null) return null;
            return new NetworkAdapterOption(
                GetInterfaceId(adapter),
                adapter.Name,
                adapter.Description,
                address.Address.ToString(),
                gateway.ToString(),
                false);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCandidateAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up) return false;
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        try
        {
            return adapter.GetIPProperties().UnicastAddresses.Any(info =>
                       info.Address.AddressFamily == AddressFamily.InterNetwork) &&
                   adapter.GetIPProperties().GatewayAddresses.Any(info =>
                       info.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return false;
        }
    }

    private static string GetInterfaceId(NetworkInterface adapter) =>
        OperatingSystem.IsLinux() ? adapter.Name : adapter.Id;

    private static async Task<IReadOnlyList<NeighborRecord>> ReadNeighborTableAsync(
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var output = OperatingSystem.IsLinux()
            ? await RunCommandAsync("ip", ["-4", "neigh", "show", "dev", interfaceName], cancellationToken)
            : await RunCommandAsync("arp", ["-a"], cancellationToken);
        var records = new List<NeighborRecord>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = (OperatingSystem.IsLinux() ? LinuxNeighborPattern : WindowsNeighborPattern).Match(line);
            if (!match.Success) continue;
            var ip = match.Groups["ip"].Value;
            var mac = NormalizeMac(match.Groups["mac"].Value);
            if (!IPAddress.TryParse(ip, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork
                || !IsUsableMac(mac)) continue;
            var state = match.Groups["state"].Value;
            if (state.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || state.Equals("incomplete", StringComparison.OrdinalIgnoreCase)) continue;
            records.Add(new NeighborRecord(ip, mac, string.IsNullOrWhiteSpace(state) ? "unknown" : state));
        }
        return records.GroupBy(record => $"{record.IpAddress}|{record.MacAddress}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static async Task ProbeSubnetAsync(IPAddress localAddress, int prefixLength, CancellationToken cancellationToken)
    {
        if (prefixLength >= 31) return;

        var localValue = BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var networkValue = localValue & mask;
        var broadcastValue = networkValue | ~mask;
        var firstHost = networkValue + 1;
        var lastHost = broadcastValue - 1;

        // Keep discovery bounded on unusually large networks while still
        // covering the full range on normal home /23 and /24 networks.
        const uint maximumProbeCount = 1022;
        if (lastHost - firstHost + 1 > maximumProbeCount)
        {
            var halfWindow = maximumProbeCount / 2;
            firstHost = localValue > halfWindow
                ? Math.Max(firstHost, localValue - halfWindow)
                : firstHost;
            lastHost = Math.Min(lastHost, firstHost + maximumProbeCount - 1);
        }

        // A normal home LAN is usually a /24. Probe more hosts concurrently
        // and use a shorter timeout for silent addresses so discovery does not
        // spend most of its time waiting on hosts that do not answer ICMP.
        using var gate = new SemaphoreSlim(96);
        var probeTasks = new List<Task>();
        for (var candidate = firstHost; candidate <= lastHost; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate == localValue) continue;

            var addressBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(addressBytes, candidate);
            probeTasks.Add(ProbeAddressAsync(new IPAddress(addressBytes), gate, cancellationToken));
            if (candidate == uint.MaxValue) break;
        }

        await Task.WhenAll(probeTasks);
    }

    private static async Task ProbeAddressAsync(
        IPAddress address,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var ping = new Ping();
            try { await ping.SendPingAsync(address, 250); }
            catch (PingException) { /* Silent clients are expected during discovery. */ }
            catch (InvalidOperationException) { /* The adapter may have changed mid-scan. */ }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> ResolveNameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromMilliseconds(750));
            var entry = await Dns.GetHostEntryAsync(ipAddress, timeoutCancellation.Token);
            return string.Equals(entry.HostName, ipAddress, StringComparison.OrdinalIgnoreCase)
                ? null
                : entry.HostName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunCommandAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return string.Empty;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return await outputTask;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatMac(PhysicalAddress address) => NormalizeMac(address.ToString());

    private static string NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var hex = new string(value.Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length != 12) return string.Empty;
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2).ToUpperInvariant()));
    }

    private static bool IsUsableMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6
               && parts.All(part => byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out _))
               && byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var firstByte)
               && firstByte != 0
               && (firstByte & 1) == 0;
    }

    private sealed record NeighborRecord(string IpAddress, string MacAddress, string State);
}
