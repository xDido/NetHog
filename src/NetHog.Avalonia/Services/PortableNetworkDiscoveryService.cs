using System.Diagnostics;
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
        if (neighbors.Count == 0)
        {
            await ProbeSubnetAsync(address.Address, address.PrefixLength, cancellationToken);
            neighbors = await ReadNeighborTableAsync(adapter.Name, cancellationToken);
        }

        var gatewayMac = neighbors.FirstOrDefault(neighbor =>
            neighbor.IpAddress.Equals(gateway.ToString(), StringComparison.OrdinalIgnoreCase))?.MacAddress ?? string.Empty;
        var devices = new List<NetworkDevice>
        {
            new(address.Address.ToString(), localMac, "local", "This PC", isLocalDevice: true)
        };

        foreach (var neighbor in neighbors
                     .Where(neighbor => !neighbor.IpAddress.Equals(address.Address.ToString(), StringComparison.OrdinalIgnoreCase))
                     .GroupBy(neighbor => neighbor.MacAddress, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = await ResolveNameAsync(neighbor.IpAddress, cancellationToken);
            devices.Add(new NetworkDevice(
                neighbor.IpAddress,
                neighbor.MacAddress,
                neighbor.State,
                displayName ?? $"Device {neighbor.IpAddress}"));
        }

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
            if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || mac.Length == 0) continue;
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
        if (prefixLength < 16 || prefixLength > 30) return;
        var localBytes = localAddress.GetAddressBytes();
        var hostBits = 32 - prefixLength;
        var hostCount = 1 << hostBits;
        if (hostCount > 512) hostCount = 512;
        var network = BitConverter.ToUInt32(localBytes.Reverse().ToArray(), 0) & (uint.MaxValue << hostBits);
        await Parallel.ForEachAsync(
            Enumerable.Range(1, Math.Max(1, hostCount - 2)),
            new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken },
            async (host, token) =>
            {
                var candidate = network + (uint)host;
                var bytes = BitConverter.GetBytes(candidate).Reverse().ToArray();
                using var ping = new Ping();
                try { await ping.SendPingAsync(new IPAddress(bytes), 250); }
                catch { /* Offline clients are expected during discovery. */ }
            });
    }

    private static async Task<string?> ResolveNameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
            return string.Equals(entry.HostName, ipAddress, StringComparison.OrdinalIgnoreCase)
                ? null
                : entry.HostName;
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

    private sealed record NeighborRecord(string IpAddress, string MacAddress, string State);
}
