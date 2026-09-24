using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Probes a bounded set of IPv4 addresses on the active Ethernet or Wi-Fi subnet and reads
/// the resulting Windows IPv4 and IPv6 neighbor table. This does not intercept or alter traffic.
/// </summary>
public sealed class NetworkDiscoveryService : INetworkDiscoveryService
{
    public IReadOnlyList<NetworkAdapterOption> GetAdapters()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<NetworkAdapterOption>();

        var defaultRouteIndex = GetDefaultRouteInterfaceIndex();
        return GetCandidateAdapters()
            .Select(adapter =>
            {
                var properties = adapter.GetIPProperties();
                var localAddress = properties.UnicastAddresses
                    .Select(address => address.Address)
                    .First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                      && !IPAddress.IsLoopback(address));
                var gateway = properties.GatewayAddresses
                    .Select(address => address.Address)
                    .First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                      && !address.Equals(IPAddress.Any));
                var interfaceIndex = properties.GetIPv4Properties()?.Index;
                return new NetworkAdapterOption(
                    adapter.Id,
                    adapter.Name,
                    adapter.Description,
                    localAddress.ToString(),
                    gateway.ToString(),
                    interfaceIndex == defaultRouteIndex);
            })
            .OrderByDescending(adapter => adapter.IsDefaultRoute)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<NetworkSnapshot> ScanAsync(
        string? interfaceId = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NetHog device discovery is available on Windows 10 and 11.");
        }

        var adapter = FindActiveNetworkAdapter(interfaceId);
        var properties = adapter.GetIPProperties();
        var localAddress = properties.UnicastAddresses
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                       && !IPAddress.IsLoopback(address));
        var gateway = properties.GatewayAddresses
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                       && !address.Equals(IPAddress.Any));
        var localIpv6Addresses = properties.UnicastAddresses
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                              && !IPAddress.IsLoopback(address))
            .Select(address => address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gatewayIpv6Addresses = properties.GatewayAddresses
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                              && !IPAddress.IsLoopback(address))
            .Select(address => address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasIpv6 = properties.UnicastAddresses
            .Any(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                            && !address.Address.IsIPv6LinkLocal
                            && !address.Address.IsIPv6SiteLocal
                            && !address.Address.Equals(IPAddress.IPv6Loopback));

        if (localAddress is null || gateway is null)
        {
            throw new InvalidOperationException("The active network adapter does not have an IPv4 address and gateway.");
        }

        var interfaceIndex = properties.GetIPv4Properties().Index;
        var localMacAddress = FormatMac(adapter.GetPhysicalAddress().GetAddressBytes());
        if (!IsUsableMac(localMacAddress))
        {
            throw new InvalidOperationException("Windows did not report a usable MAC address for the active network adapter.");
        }

        var prefixLength = properties.UnicastAddresses
            .First(address => address.Address.Equals(localAddress)).PrefixLength;
        var hostsProbed = await ProbeLocalSubnetAsync(localAddress, properties, cancellationToken);
        using var gatewayProbeGate = new SemaphoreSlim(1);
        await ProbeAddressAsync(gateway, gatewayProbeGate, cancellationToken);
        var (devices, gatewayMacAddress) = await ReadNeighborTableAsync(
            interfaceIndex,
            localAddress,
            localMacAddress,
            gateway,
            localIpv6Addresses,
            gatewayIpv6Addresses,
            cancellationToken);

        return new NetworkSnapshot(
            adapter.Name,
            adapter.Description,
            adapter.Id,
            localAddress.ToString(),
            localMacAddress,
            gateway.ToString(),
            gatewayMacAddress,
            prefixLength,
            hasIpv6,
            hostsProbed,
            DateTimeOffset.Now,
            devices);
    }

    private static async Task<int> ProbeLocalSubnetAsync(
        IPAddress localAddress,
        IPInterfaceProperties properties,
        CancellationToken cancellationToken)
    {
        var localUnicast = properties.UnicastAddresses.FirstOrDefault(address => address.Address.Equals(localAddress));
        if (localUnicast is null || localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return 0;
        }

        var prefixLength = localUnicast.PrefixLength;
        if (prefixLength >= 31)
        {
            return 0;
        }

        var localValue = BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var networkValue = localValue & mask;
        var broadcastValue = networkValue | ~mask;
        var firstHost = networkValue + 1;
        var lastHost = broadcastValue - 1;

        // Keep discovery bounded on unusually large private networks. For a
        // normal home /24, this probes the full usable subnet.
        const uint maximumProbeCount = 1022;
        if (lastHost - firstHost + 1 > maximumProbeCount)
        {
            var halfWindow = maximumProbeCount / 2;
            firstHost = localValue > halfWindow ? Math.Max(firstHost, localValue - halfWindow) : firstHost;
            lastHost = Math.Min(lastHost, firstHost + maximumProbeCount - 1);
        }

        using var gate = new SemaphoreSlim(48);
        var probeTasks = new List<Task>();
        for (var candidate = firstHost; candidate <= lastHost; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate == localValue)
            {
                continue;
            }

            var addressBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(addressBytes, candidate);
            var target = new IPAddress(addressBytes);

            probeTasks.Add(ProbeAddressAsync(target, gate, cancellationToken));
            if (candidate == uint.MaxValue)
            {
                break;
            }
        }

        await Task.WhenAll(probeTasks);
        return probeTasks.Count;
    }

    private static async Task ProbeAddressAsync(IPAddress address, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var ping = new Ping();
            try
            {
                await ping.SendPingAsync(address, 350);
            }
            catch (PingException)
            {
                // A silent or firewall protected host can still be present in
                // the Windows neighbor cache after its ARP resolution attempt.
            }
            catch (InvalidOperationException)
            {
                // A probe can fail if the network interface changes mid-scan.
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static NetworkInterface FindActiveNetworkAdapter(string? interfaceId)
    {
        var candidates = GetCandidateAdapters();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                "No connected Ethernet or Wi-Fi adapter with an IPv4 gateway was found. Connect to your network, then scan again.");
        }

        if (!string.IsNullOrWhiteSpace(interfaceId))
        {
            var selected = candidates.FirstOrDefault(candidate =>
                candidate.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new InvalidOperationException(
                    "The selected network adapter is no longer connected. Refresh the adapter list and scan again.");
            }

            return selected;
        }

        var defaultRouteIndex = GetDefaultRouteInterfaceIndex();
        var activeRouteAdapter = defaultRouteIndex is null
            ? null
            : candidates.FirstOrDefault(candidate =>
                candidate.GetIPProperties().GetIPv4Properties()?.Index == defaultRouteIndex);
        return activeRouteAdapter ?? candidates
            .OrderBy(candidate => candidate.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .First();
    }

    private static NetworkInterface[] GetCandidateAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(candidate => candidate.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
            .Where(candidate => candidate.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address)))
            .Where(candidate => candidate.GetIPProperties().GatewayAddresses.Any(address =>
                address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !address.Address.Equals(IPAddress.Any)))
            .ToArray();
    }

    private static int? GetDefaultRouteInterfaceIndex()
    {
        try
        {
            // Use Windows' route decision for a public IPv4 destination. This
            // picks Ethernet when it carries the default route, even if a Wi-Fi
            // adapter is also up. If a VPN owns the route, fall back to an
            // available physical adapter above.
            const uint publicDestination = 0x08080808; // 8.8.8.8, network byte order
            return GetBestInterface(publicDestination, out var index) == 0 ? checked((int)index) : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NetworkInformationException)
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint interfaceIndex);

    private static async Task<(IReadOnlyList<NetworkDevice> Devices, string GatewayMacAddress)> ReadNeighborTableAsync(
        int interfaceIndex,
        IPAddress localAddress,
        string localMacAddress,
        IPAddress gateway,
        IReadOnlySet<string> localIpv6Addresses,
        IReadOnlySet<string> gatewayIpv6Addresses,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"@(Get-NetNeighbor -InterfaceIndex {interfaceIndex} -AddressFamily IPv4; " +
            $"Get-NetNeighbor -InterfaceIndex {interfaceIndex} -AddressFamily IPv6) | " +
            "Select-Object IPAddress,LinkLayerAddress,State | ConvertTo-Json -Compress");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows could not start the device discovery command.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "Windows could not read the network neighbor table."
                        : $"Windows could not read the network neighbor table: {error.Trim()}");
            }

            var neighbors = ParseNeighbors(output);
            var gatewayMac = neighbors
                .FirstOrDefault(device => string.Equals(device.IpAddress, gateway.ToString(), StringComparison.OrdinalIgnoreCase))
                ?.MacAddress ?? string.Empty;
            var devices = neighbors
                .Where(device => !string.Equals(device.IpAddress, localAddress.ToString(), StringComparison.OrdinalIgnoreCase))
                .Where(device => !string.Equals(device.IpAddress, gateway.ToString(), StringComparison.OrdinalIgnoreCase))
                .Where(device => !localIpv6Addresses.Contains(device.IpAddress))
                .Where(device => !gatewayIpv6Addresses.Contains(device.IpAddress))
                .GroupBy(device => device.MacAddress, StringComparer.OrdinalIgnoreCase)
                .Select(CreateDevice)
                .ToList();
            if (IsUsableMac(localMacAddress))
            {
                var localDevice = new NetworkDevice(
                    localAddress.ToString(),
                    localMacAddress,
                    "Local",
                    $"{Environment.MachineName} (This PC)",
                    isLocalDevice: true);
                foreach (var address in localIpv6Addresses) localDevice.AddIpv6Address(address);
                devices.Add(localDevice);
            }

            var orderedDevices = devices
                .OrderByDescending(device => device.IsLocalDevice)
                .ThenBy(device => IPAddress.Parse(device.NameLookupAddress).GetAddressBytes(), ByteArrayComparer.Instance)
                .ToArray();
            await PopulateSuggestedNamesAsync(orderedDevices, cancellationToken);
            return (orderedDevices, gatewayMac);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task PopulateSuggestedNamesAsync(
        IReadOnlyList<NetworkDevice> devices,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(devices.Select(async device =>
        {
            try
            {
                if (device.IsLocalDevice) return;
                var lookupAddress = device.NameLookupAddress;
                if (string.IsNullOrWhiteSpace(lookupAddress)) return;
                var name = await TryResolveReverseDnsNameAsync(lookupAddress, cancellationToken)
                           ?? await TryResolveMdnsNameAsync(lookupAddress, cancellationToken)
                           ?? await TryResolveNetBiosNameAsync(lookupAddress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(name)
                    && !IPAddress.TryParse(name, out _)
                    && !name.Equals(lookupAddress, StringComparison.OrdinalIgnoreCase))
                {
                    device.SetSuggestedName(name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Reverse DNS is optional. Many phones and privacy-hardened
                // networks do not publish a local hostname.
            }
        }));
    }

    private static async Task<string?> TryResolveReverseDnsNameAsync(
        string ipAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromMilliseconds(450));
            var host = await Dns.GetHostEntryAsync(ipAddress, timeoutCancellation.Token);
            var name = host.HostName.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _)
                ? null
                : name;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> TryResolveMdnsNameAsync(
        string ipAddress,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipAddress, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            var reverseName = string.Join('.', address.GetAddressBytes().Reverse().Select(value => value.ToString()))
                              + ".in-addr.arpa";
            var query = CreateDnsQuery(reverseName);
            using var client = new UdpClient(AddressFamily.InterNetwork);
            await client.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromMilliseconds(650));
            var response = (await client.ReceiveAsync(timeoutCancellation.Token)).Buffer;
            return TryReadPtrAnswer(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // mDNS is optional and may be blocked by the network or firewall.
            return null;
        }
    }

    private static byte[] CreateDnsQuery(string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        foreach (var label in name.Split('.'))
        {
            writer.Write((byte)label.Length);
            writer.Write(Encoding.ASCII.GetBytes(label));
        }

        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)12); // PTR
        writer.Write((byte)0x80);
        writer.Write((byte)1); // IN + unicast response requested
        return stream.ToArray();
    }

    private static string? TryReadPtrAnswer(byte[] response)
    {
        if (response.Length < 12) return null;
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));
        var offset = 12;
        for (var question = 0; question < questionCount; question++)
        {
            if (!TrySkipDnsName(response, ref offset) || offset + 4 > response.Length) return null;
            offset += 4;
        }

        for (var answer = 0; answer < answerCount; answer++)
        {
            if (!TrySkipDnsName(response, ref offset) || offset + 10 > response.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8, 2));
            offset += 10;
            if (offset + dataLength > response.Length) return null;
            if (type == 12 && TryReadDnsName(response, offset, out var name))
            {
                var trimmed = name.TrimEnd('.');
                return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            }

            offset += dataLength;
        }

        return null;
    }

    private static bool TrySkipDnsName(byte[] packet, ref int offset)
    {
        var guard = 0;
        while (offset < packet.Length && guard++ < 128)
        {
            var length = packet[offset++];
            if (length == 0) return true;
            if ((length & 0xC0) == 0xC0)
            {
                if (offset >= packet.Length) return false;
                offset++;
                return true;
            }

            if (length > 63 || offset + length > packet.Length) return false;
            offset += length;
        }

        return false;
    }

    private static bool TryReadDnsName(byte[] packet, int offset, out string name)
    {
        var labels = new List<string>();
        var visited = new HashSet<int>();
        var jumps = 0;
        while (offset < packet.Length && jumps++ < 128)
        {
            if (!visited.Add(offset)) break;
            var length = packet[offset++];
            if (length == 0)
            {
                name = string.Join('.', labels);
                return labels.Count > 0;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset >= packet.Length) break;
                var pointer = ((length & 0x3F) << 8) | packet[offset];
                if (!TryReadDnsName(packet, pointer, out var pointedName)) break;
                if (pointedName.Length > 0) labels.AddRange(pointedName.Split('.'));
                name = string.Join('.', labels);
                return labels.Count > 0;
            }

            if (length > 63 || offset + length > packet.Length) break;
            labels.Add(Encoding.ASCII.GetString(packet, offset, length));
            offset += length;
        }

        name = string.Empty;
        return false;
    }

    private static async Task<string?> TryResolveNetBiosNameAsync(
        string ipAddress,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nbtstat.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-A");
        process.StartInfo.ArgumentList.Add(ipAddress);

        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken));
            if (completed != waitTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { process.Kill(entireProcessTree: true); }
                catch { /* the probe may have exited between the timeout and cleanup */ }
                try { await waitTask; }
                catch { /* a timed-out probe is optional */ }
                return null;
            }

            await waitTask;
            var output = await outputTask;
            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                var marker = line.IndexOf("<00>", StringComparison.OrdinalIgnoreCase);
                if (marker <= 0) continue;
                var name = line[..marker].Trim();
                var isGroup = line.Contains("GROUP", StringComparison.OrdinalIgnoreCase);
                if (name.Length > 0 && !isGroup && !name.Equals("__MSBROWSE__", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The optional NetBIOS probe may have exited while cancellation was handled.
            }
            throw;
        }
        catch (Exception)
        {
            // NetBIOS is optional and is commonly disabled on modern phones.
        }

        return null;
    }

    private static NetworkDevice CreateDevice(IGrouping<string, NeighborRecord> group)
    {
        var entries = group.ToArray();
        var ipv4 = entries.FirstOrDefault(entry => IPAddress.Parse(entry.IpAddress).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var firstAddress = ipv4?.IpAddress ?? entries[0].IpAddress;
        var displayName = $"Device {group.Key[^5..]}";
        var device = new NetworkDevice(firstAddress, group.Key, entries[0].State, displayName);
        foreach (var entry in entries.Where(entry => IPAddress.Parse(entry.IpAddress).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            device.AddIpv6Address(entry.IpAddress);
        }
        return device;
    }

    private static IReadOnlyList<NeighborRecord> ParseNeighbors(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return Array.Empty<NeighborRecord>();
        }

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : new[] { document.RootElement };

        return rows
            .Select(ReadNeighbor)
            .Where(device => device is not null)
            .Cast<NeighborRecord>()
            .OrderBy(device => IPAddress.Parse(device.IpAddress).GetAddressBytes(), ByteArrayComparer.Instance)
            .ToArray();
    }

    private static string FormatMac(byte[] bytes) => bytes.Length == 6
        ? string.Join(':', bytes.Select(value => value.ToString("X2")))
        : string.Empty;

    private static NeighborRecord? ReadNeighbor(JsonElement row)
    {
        var ipText = ReadString(row, "IPAddress");
        var macText = ReadString(row, "LinkLayerAddress");
        var state = ReadString(row, "State");

        if (!IPAddress.TryParse(ipText, out var ipAddress)
            || ipAddress.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
            || !IsUsableMac(macText))
        {
            return null;
        }

        var mac = macText!.Replace('-', ':').ToUpperInvariant();
        return new NeighborRecord(ipAddress.ToString(), mac, state ?? "Unknown");
    }

    private static string? ReadString(JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool IsUsableMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("00-00-00-00-00-00", StringComparison.OrdinalIgnoreCase)
            || value.Equals("00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6
               && parts.All(part => byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out _))
               && byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var firstByte)
               && firstByte != 0
               && (firstByte & 1) == 0;
    }

    private sealed record NeighborRecord(string IpAddress, string MacAddress, string State);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
