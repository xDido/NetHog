using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using SharpPcap;
using SharpPcap.LibPcap;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Session-only IPv4 controls implemented by intercepting packets for opted-in
/// clients on the selected Ethernet or Wi-Fi LAN. Npcap is required on Windows;
/// libpcap is required on Linux.
/// </summary>
public sealed class ArpEnforcementService : IEnforcementService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _sendLock = new();
    private readonly Dictionary<string, TargetFlow> _flows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTraffic> _traffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly DomainObservationStore _domainObservations = new();
    private LibPcapLiveDevice? _device;
    private NetworkSnapshot? _network;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _maintenanceTask;
    private int _unexpectedStopStarted;

    public event Action<string>? SessionEndedUnexpectedly;

    public bool IsSessionActive
    {
        get { lock (_sync) return _device is not null; }
    }

    public IReadOnlyDictionary<string, DeviceTraffic> GetTrafficSnapshot()
    {
        lock (_sync)
        {
            return new Dictionary<string, DeviceTraffic>(_traffic, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<DomainObservation> GetDomainObservations() =>
        _domainObservations.GetObservations();

    public void SetDomainBlocked(string deviceMacAddress, string domain, bool blocked) =>
        _domainObservations.SetBlocked(deviceMacAddress, domain, blocked);

    public Task<EnforcementAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return Task.FromResult(new EnforcementAvailability(false, "Unsupported platform", "NetHog controls are currently available on Windows and Linux."));
        }

        var isAdministrator = IsAdministrator();
        var devices = GetPcapDevices();
        var npcapAvailable = devices.Count > 0;
        var captureProvider = OperatingSystem.IsWindows() ? "Npcap" : "libpcap";
        var details = !isAdministrator
            ? "Elevated permission is required. Launch NetHog as administrator on Windows or with sudo/root permission on Linux."
            : !npcapAvailable
                ? $"NetHog is portable, but traffic controls need {captureProvider} installed separately on this PC. Install it, then restart NetHog."
                : $"{captureProvider} is ready. Confirm that you administer this network before starting a temporary IPv4 control session.";

        return Task.FromResult(new EnforcementAvailability(
            isAdministrator && npcapAvailable,
            isAdministrator && npcapAvailable ? "Ready" : "Setup required",
            details,
            isAdministrator,
            npcapAvailable));
    }

    public async Task StartSessionAsync(NetworkSnapshot network, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (IsSessionActive)
            {
                throw new InvalidOperationException("A control session is already active. Stop it before switching networks.");
            }

            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException("Start NetHog with elevated permission to control network traffic.");
            }

            if (!TryParseMac(network.LocalMacAddress, out _) || !TryParseMac(network.GatewayMacAddress, out _))
            {
                throw new InvalidOperationException(
                    "NetHog could not identify the network adapter and gateway MAC addresses. Scan again after the gateway has responded.");
            }

            if (network.HasIpv6)
            {
                // The session can still be useful for IPv4, but the caller must present the bypass warning.
            }

            var adapterGuid = ExtractGuid(network.InterfaceId);
            var device = GetPcapDevices().FirstOrDefault(candidate =>
                (adapterGuid is not null && ExtractGuid(candidate.Name) == adapterGuid) ||
                string.Equals(candidate.Name, network.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.EndsWith($"\\{network.InterfaceId}", StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                throw new InvalidOperationException(
                    "The packet-capture library could not match the active network adapter. USB, virtual, bridged, or vendor-specific adapters may not be supported.");
            }

            var cancellation = new CancellationTokenSource();
            try
            {
                device.Open(new DeviceConfiguration
                {
                    Mode = DeviceModes.Promiscuous,
                    Immediate = true,
                    ReadTimeout = 250
                });

                if (!device.LinkType.ToString().Equals("Ethernet", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The packet-capture library reported an unsupported link type ({device.LinkType}). NetHog requires Ethernet frames from this adapter.");
                }

                _network = network;
                lock (_sync)
                {
                    _traffic.Clear();
                    _domainObservations.ClearObservations();
                    foreach (var candidate in network.Devices)
                    {
                        _traffic[NormalizeMac(candidate.MacAddress)] = new DeviceTraffic(0, 0);
                    }
                }
                _device = device;
                _sessionCancellation = cancellation;
                Interlocked.Exchange(ref _unexpectedStopStarted, 0);
                device.OnPacketArrival += OnPacketArrival;
                device.Filter = "arp or ip or ip6 or vlan";
                device.StartCapture();
                _maintenanceTask = MaintainSessionAsync(network, device, cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
                cancellation.Dispose();
                device.OnPacketArrival -= OnPacketArrival;
                if (device.Opened) device.Close();
                _network = null;
                _device = null;
                _sessionCancellation = null;
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await StopSessionCoreAsync();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task ApplyRuleAsync(DeviceControlRule rule, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRule(rule);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
        var (device, network) = GetActiveSession();

        var target = network.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.MacAddress, rule.DeviceMacAddress, StringComparison.OrdinalIgnoreCase));
        if (target is null || !IPAddress.TryParse(target.IpAddress, out var targetAddress))
        {
            throw new InvalidOperationException("This device is no longer in the latest network scan. Stop the session, scan again, then retry.");
        }

        if (!IsInLocalSubnet(targetAddress, network.LocalAddress, network.PrefixLength)
            || targetAddress.Equals(IPAddress.Parse(network.GatewayAddress)))
        {
            throw new InvalidOperationException("NetHog can only control a peer device on the current Ethernet or Wi-Fi subnet.");
        }

        if (!TryParseMac(target.MacAddress, out var targetMac)
            || target.MacAddress.Equals(network.LocalMacAddress, StringComparison.OrdinalIgnoreCase)
            || target.MacAddress.Equals(network.GatewayMacAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NetHog will not apply a rule to the local adapter or the network gateway.");
        }

        TargetFlow flow;
        lock (_sync)
        {
            var key = NormalizeMac(rule.DeviceMacAddress);
            if (_flows.TryGetValue(key, out flow!))
            {
                flow.SetRule(rule);
                return;
            }

            flow = new TargetFlow(
                rule,
                targetAddress,
                targetMac,
                packet => SendFrame(device, packet),
                exception => HandleFlowFailure(exception));
            _flows.Add(key, flow);
        }

        try
        {
            await SendPoisonPairAsync(device, network, flow, cancellationToken);
        }
        catch
        {
            lock (_sync) _flows.Remove(NormalizeMac(rule.DeviceMacAddress));
            await flow.DisposeAsync();
            try { await RestoreTargetAsync(device, network, flow, CancellationToken.None); }
            catch { /* a partial ARP update is possible if the adapter failed */ }
            throw;
        }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task RemoveRuleAsync(string deviceMacAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
        var (device, network) = GetActiveSession();
        TargetFlow? flow;
        lock (_sync)
        {
            _flows.Remove(NormalizeMac(deviceMacAddress), out flow);
        }

        if (flow is null) return;
        await flow.DisposeAsync();
        await RestoreTargetAsync(device, network, flow, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task StopSessionCoreAsync()
    {
        LibPcapLiveDevice? device;
        NetworkSnapshot? network;
        CancellationTokenSource? cancellation;
        Task? maintenance;
        TargetFlow[] flows;
        lock (_sync)
        {
            device = _device;
            network = _network;
            cancellation = _sessionCancellation;
            maintenance = _maintenanceTask;
            flows = _flows.Values.ToArray();
            _flows.Clear();
            _maintenanceTask = null;
        }

        if (device is null)
        {
            lock (_sync)
            {
                _network = null;
                _sessionCancellation = null;
                _maintenanceTask = null;
            }
            Interlocked.Exchange(ref _unexpectedStopStarted, 0);
            cancellation?.Dispose();
            return;
        }

        cancellation?.Cancel();
        try
        {
            device.OnPacketArrival -= OnPacketArrival;
            if (device.Started) await Task.Run(device.StopCapture);
        }
        catch
        {
            // Continue to restore ARP and release Npcap even if capture shutdown is partial.
        }

        if (maintenance is not null)
        {
            try { await maintenance; }
            catch (OperationCanceledException) { }
            catch { /* a failed maintenance send must not prevent ARP restoration */ }
        }

        foreach (var flow in flows) await flow.DisposeAsync();

        if (network is not null && device.Opened)
        {
            foreach (var flow in flows)
            {
                try { await RestoreTargetAsync(device, network, flow, CancellationToken.None); }
                catch { /* best-effort ARP cache restoration */ }
            }
        }

        try { if (device.Opened) device.Close(); }
        catch { /* the capture handle may already have been closed by Npcap */ }
        lock (_sync)
        {
            _device = null;
            _network = null;
            _sessionCancellation = null;
            _maintenanceTask = null;
        }
        Interlocked.Exchange(ref _unexpectedStopStarted, 0);
        cancellation?.Dispose();
    }

    private async Task MaintainSessionAsync(NetworkSnapshot network, LibPcapLiveDevice device, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (!IsNetworkStillPresent(network))
                {
                    EndSessionUnexpectedly("The network adapter or IPv4 address changed. NetHog stopped the control session and attempted to restore the affected devices.");
                    return;
                }

                TargetFlow[] flows;
                lock (_sync) flows = _flows.Values.ToArray();
                foreach (var flow in flows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await SendPoisonPairAsync(device, network, flow, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An Npcap or adapter error stops the session, restoring any active entries.
            EndSessionUnexpectedly("Npcap stopped responding. NetHog stopped the control session and attempted to restore the affected devices.");
        }
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        try
        {
            var frame = capture.GetPacket().Data;
            if (frame.Length < 14) return;

            var network = _network;
            if (network is null || !TryParseMac(network.LocalMacAddress, out var localMac)) return;

            var ipOffset = FindIpHeaderOffset(frame, out var isIpv6);
            if (ipOffset < 0) return;

            if (isIpv6)
            {
                if (frame.Length < ipOffset + 40 || (frame[ipOffset] >> 4) != 6) return;
                var sourceIpv6 = new IPAddress(frame.AsSpan(ipOffset + 8, 16));
                var destinationIpv6 = new IPAddress(frame.AsSpan(ipOffset + 24, 16));
                ObserveDomain(network, sourceIpv6, frame, ipOffset);
                RecordObservedTraffic(network, sourceIpv6, destinationIpv6, frame.Length);
                return;
            }

            if (frame.Length < ipOffset + 20 || (frame[ipOffset] >> 4) != 4) return;
            var hasGatewayMac = TryParseMac(network.GatewayMacAddress, out var gatewayMac);

            var headerLength = (frame[ipOffset] & 0x0F) * 4;
            if (headerLength < 20 || frame.Length < ipOffset + headerLength) return;
            var sourceIp = ReadIPv4(frame, ipOffset + 12);
            var destinationIp = ReadIPv4(frame, ipOffset + 16);
            var sourceMac = frame.AsSpan(6, 6);
            var destinationMac = frame.AsSpan(0, 6);

            RecordObservedTraffic(network, sourceIp, destinationIp, frame.Length);
            var sourceDevice = network.Devices.FirstOrDefault(candidate =>
                candidate.IpAddress.Equals(sourceIp.ToString(), StringComparison.Ordinal));
            var observedDomain = DomainTrafficInspector.TryReadDomain(frame, ipOffset);
            if (sourceDevice is not null && observedDomain is not null)
            {
                _domainObservations.Observe(
                    sourceDevice.MacAddress,
                    sourceDevice.DisplayName,
                    observedDomain);
            }

            TargetFlow? flow = null;
            var direction = PacketDirection.Upload;
            lock (_sync)
            {
                if (!hasGatewayMac || sourceMac.SequenceEqual(localMac) || destinationMac.SequenceEqual(gatewayMac)) return;
                foreach (var candidate in _flows.Values)
                {
                    if (sourceMac.SequenceEqual(candidate.MacBytes)
                        && destinationMac.SequenceEqual(localMac)
                        && sourceIp.Equals(candidate.Address)
                        && !IsInLocalSubnet(destinationIp, network.LocalAddress, network.PrefixLength))
                    {
                        flow = candidate;
                        direction = PacketDirection.Upload;
                        break;
                    }

                    if (sourceMac.SequenceEqual(gatewayMac)
                        && destinationMac.SequenceEqual(localMac)
                        && destinationIp.Equals(candidate.Address)
                        && !IsInLocalSubnet(sourceIp, network.LocalAddress, network.PrefixLength))
                    {
                        flow = candidate;
                        direction = PacketDirection.Download;
                        break;
                    }
                }
            }

            if (flow is null) return;
            var rule = flow.Rule;
            if (rule.BlockInternet) return;
            if (observedDomain is not null &&
                _domainObservations.IsBlocked(flow.Rule.DeviceMacAddress, observedDomain))
            {
                return;
            }

            var rewritten = frame.ToArray();
            var destination = direction == PacketDirection.Upload ? gatewayMac : flow.MacBytes;
            destination.CopyTo(rewritten.AsSpan(0, 6));
            localMac.CopyTo(rewritten.AsSpan(6, 6));
            flow.Enqueue(direction, rewritten);
        }
        catch
        {
            // Malformed or unsupported frames are ignored; do not crash Npcap's callback thread.
        }
    }

    private void RecordObservedTraffic(
        NetworkSnapshot network,
        IPAddress sourceIp,
        IPAddress destinationIp,
        int frameLength)
    {
        if (sourceIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (destinationIp.IsIPv6Multicast) return;
            var sourceDevice = FindDeviceForAddress(network, sourceIp);
            var destinationDevice = FindDeviceForAddress(network, destinationIp);
            if (sourceDevice is not null && destinationDevice is null)
            {
                AddTraffic(sourceDevice.MacAddress, frameLength, upload: true);
            }
            else if (destinationDevice is not null && sourceDevice is null)
            {
                AddTraffic(destinationDevice.MacAddress, frameLength, upload: false);
            }
            return;
        }

        // On Wi-Fi, bridges, and some virtual adapters, the Ethernet source
        // address seen by Npcap is not always the neighbor-table MAC. The
        // client IP is stable across those link-layer variations, so use it
        // as the primary attribution key and keep MACs for identity/storage.
        var destinationIsRouted = !IsInLocalSubnet(destinationIp, network.LocalAddress, network.PrefixLength);
        if (destinationIsRouted)
        {
            var uploadDevice = network.Devices.FirstOrDefault(candidate =>
                sourceIp.ToString().Equals(candidate.IpAddress, StringComparison.Ordinal));
            if (uploadDevice is not null)
            {
                AddTraffic(uploadDevice.MacAddress, frameLength, upload: true);
                return;
            }
        }

        if (IsInLocalSubnet(sourceIp, network.LocalAddress, network.PrefixLength)) return;
        var downloadDevice = network.Devices.FirstOrDefault(candidate =>
            destinationIp.ToString().Equals(candidate.IpAddress, StringComparison.Ordinal));
        if (downloadDevice is not null)
        {
            AddTraffic(downloadDevice.MacAddress, frameLength, upload: false);
        }
    }

    private static NetworkDevice? FindDeviceForAddress(NetworkSnapshot network, IPAddress address) =>
        network.Devices.FirstOrDefault(candidate =>
            (IPAddress.TryParse(candidate.IpAddress, out var ipv4) && ipv4.Equals(address))
            || candidate.Ipv6Addresses.Any(candidateAddress =>
                IPAddress.TryParse(candidateAddress, out var ipv6) && ipv6.Equals(address)));

    private void ObserveDomain(NetworkSnapshot network, IPAddress sourceAddress, byte[] frame, int ipOffset)
    {
        var sourceDevice = FindDeviceForAddress(network, sourceAddress);
        var domain = DomainTrafficInspector.TryReadDomain(frame, ipOffset);
        if (sourceDevice is not null && domain is not null)
        {
            _domainObservations.Observe(sourceDevice.MacAddress, sourceDevice.DisplayName, domain);
        }
    }

    private void AddTraffic(string macAddress, long bytes, bool upload)
    {
        lock (_sync)
        {
            var key = NormalizeMac(macAddress);
            _traffic.TryGetValue(key, out var existing);
            existing ??= new DeviceTraffic(0, 0);
            _traffic[key] = upload
                ? existing with { UploadBytes = existing.UploadBytes + bytes }
                : existing with { DownloadBytes = existing.DownloadBytes + bytes };
        }
    }

    private async Task SendPoisonPairAsync(
        LibPcapLiveDevice device,
        NetworkSnapshot network,
        TargetFlow flow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localMac = ParseMac(network.LocalMacAddress);
        var gatewayMac = ParseMac(network.GatewayMacAddress);
        var frameToClient = CreateArpReply(localMac, localMac, flow.MacBytes, IPAddress.Parse(network.GatewayAddress), flow.Address);
        var frameToGateway = CreateArpReply(localMac, localMac, gatewayMac, flow.Address, IPAddress.Parse(network.GatewayAddress));
        SendFramePair(device, frameToClient, frameToGateway, () => flow.IsActive);
        await Task.CompletedTask;
    }

    private async Task RestoreTargetAsync(
        LibPcapLiveDevice device,
        NetworkSnapshot network,
        TargetFlow flow,
        CancellationToken cancellationToken)
    {
        var localMac = ParseMac(network.LocalMacAddress);
        var gatewayMac = ParseMac(network.GatewayMacAddress);
        var toClient = CreateArpReply(localMac, gatewayMac, flow.MacBytes, IPAddress.Parse(network.GatewayAddress), flow.Address);
        var toGateway = CreateArpReply(localMac, flow.MacBytes, gatewayMac, flow.Address, IPAddress.Parse(network.GatewayAddress));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendFramePair(device, toClient, toGateway);
            if (attempt < 3) await Task.Delay(200, cancellationToken);
        }
    }

    private (LibPcapLiveDevice Device, NetworkSnapshot Network) GetActiveSession()
    {
        lock (_sync)
        {
            if (_device is null || _network is null || _sessionCancellation is null || _sessionCancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException("Start a network control session before changing device controls.");
            }

            return (_device, _network);
        }
    }

    private static List<LibPcapLiveDevice> GetPcapDevices()
    {
        try
        {
            var list = LibPcapLiveDeviceList.Instance;
            list.Refresh();
            return list.ToList();
        }
        catch
        {
            return new List<LibPcapLiveDevice>();
        }
    }

    private static bool IsAdministrator()
    {
        if (OperatingSystem.IsLinux())
        {
            try { return geteuid() == 0; }
            catch { return false; }
        }

        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    private static Guid? ExtractGuid(string value)
    {
        var start = value.LastIndexOf('{');
        var end = value.LastIndexOf('}');
        if (start >= 0 && end > start && Guid.TryParse(value[(start + 1)..end], out var guid)) return guid;
        return Guid.TryParse(value, out guid) ? guid : null;
    }

    private static bool IsNetworkStillPresent(NetworkSnapshot network)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.Id.Equals(network.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
                    adapter.Name.Equals(network.InterfaceId, StringComparison.OrdinalIgnoreCase))
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Any(adapter => adapter.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && address.Address.ToString() == network.LocalAddress));
        }
        catch { return false; }
    }

    private static void ValidateRule(DeviceControlRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (!TryParseMac(rule.DeviceMacAddress, out _)) throw new ArgumentException("The device MAC address is invalid.", nameof(rule));
        if (rule.DownloadLimitMbps is <= 0 or > 10_000 || rule.UploadLimitMbps is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "Speed limits must be from 1 to 10,000 Mbps.");
        }
        if (!rule.BlockInternet && rule.DownloadLimitMbps is null && rule.UploadLimitMbps is null)
        {
            throw new ArgumentException("Set at least one speed limit or turn on Block internet.", nameof(rule));
        }
    }

    private static bool TryParseMac(string value, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return false;
        var result = new byte[6];
        for (var index = 0; index < result.Length; index++)
        {
            if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.HexNumber, null, out result[index])) return false;
        }
        if (result.All(part => part == 0) || (result[0] & 1) != 0) return false;
        mac = result;
        return true;
    }

    private static byte[] ParseMac(string value) => TryParseMac(value, out var mac)
        ? mac
        : throw new InvalidOperationException("The current network has an invalid MAC address.");

    private static string NormalizeMac(string value) => string.Join(':', ParseMac(value).Select(part => part.ToString("X2")));

    private static byte[] CreateArpReply(
        byte[] ethernetSourceMac,
        byte[] senderMac,
        byte[] targetMac,
        IPAddress senderIp,
        IPAddress targetIp)
    {
        var packet = new byte[42];
        targetMac.CopyTo(packet, 0);
        ethernetSourceMac.CopyTo(packet, 6);
        packet[12] = 0x08;
        packet[13] = 0x06;
        packet[14] = 0x00;
        packet[15] = 0x01;
        packet[16] = 0x08;
        packet[17] = 0x00;
        packet[18] = 6;
        packet[19] = 4;
        packet[20] = 0x00;
        packet[21] = 0x02;
        senderMac.CopyTo(packet, 22);
        senderIp.GetAddressBytes().CopyTo(packet, 28);
        targetMac.CopyTo(packet, 32);
        targetIp.GetAddressBytes().CopyTo(packet, 38);
        return packet;
    }

    private static bool IsInLocalSubnet(IPAddress address, string localAddress, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var localBytes = IPAddress.Parse(localAddress).GetAddressBytes();
        if (addressBytes.Length != 4 || localBytes.Length != 4) return false;
        var bitsRemaining = prefixLength;
        for (var index = 0; index < 4; index++)
        {
            var bits = Math.Clamp(bitsRemaining, 0, 8);
            var mask = bits == 0 ? 0 : 0xFF << (8 - bits) & 0xFF;
            if ((addressBytes[index] & mask) != (localBytes[index] & mask)) return false;
            bitsRemaining -= bits;
        }
        return true;
    }

    private static ushort ReadUInt16(byte[] packet, int offset) => (ushort)((packet[offset] << 8) | packet[offset + 1]);

    private static IPAddress ReadIPv4(byte[] packet, int offset) => new(packet.AsSpan(offset, 4));

    private static int FindIpHeaderOffset(byte[] packet, out bool isIpv6)
    {
        isIpv6 = false;
        if (packet.Length < 14) return -1;

        var etherType = ReadUInt16(packet, 12);
        var offset = 14;
        for (var vlanDepth = 0; vlanDepth < 2 && IsVlanEtherType(etherType); vlanDepth++)
        {
            if (packet.Length < offset + 4) return -1;
            etherType = ReadUInt16(packet, offset + 2);
            offset += 4;
        }

        if (etherType == 0x0800) return offset;
        if (etherType == 0x86DD)
        {
            isIpv6 = true;
            return offset;
        }
        return -1;
    }

    private static bool IsVlanEtherType(ushort etherType) =>
        etherType is 0x8100 or 0x88A8 or 0x9100;

    private void SendFrame(LibPcapLiveDevice device, byte[] packet)
    {
        lock (_sendLock)
        {
            if (!device.Opened) throw new InvalidOperationException("Npcap closed the network capture handle.");
            device.SendPacket(packet);
        }
    }

    private void SendFramePair(LibPcapLiveDevice device, byte[] first, byte[] second, Func<bool>? shouldSend = null)
    {
        lock (_sendLock)
        {
            if (shouldSend is not null && !shouldSend()) return;
            if (!device.Opened) throw new InvalidOperationException("Npcap closed the network capture handle.");
            device.SendPacket(first);
            device.SendPacket(second);
        }
    }

    private void HandleFlowFailure(Exception exception)
    {
        if (!IsSessionActive) return;
        EndSessionUnexpectedly($"Npcap could not forward a device packet ({exception.Message}). NetHog stopped the session and attempted to restore ARP entries.");
    }

    private void EndSessionUnexpectedly(string message)
    {
        if (Interlocked.Exchange(ref _unexpectedStopStarted, 1) != 0) return;
        try { SessionEndedUnexpectedly?.Invoke(message); }
        catch { /* a UI notification must not interrupt session cleanup */ }
        _ = StopSessionAfterFailureAsync();
    }

    private async Task StopSessionAfterFailureAsync()
    {
        try { await StopSessionAsync(CancellationToken.None); }
        catch { /* cleanup is best effort; the foreground UI also attempts it */ }
    }

    private enum PacketDirection { Download, Upload }

    private sealed class TargetFlow : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Channel<byte[]> _download = CreateQueue();
        private readonly Channel<byte[]> _upload = CreateQueue();
        private readonly Task _downloadWorker;
        private readonly Task _uploadWorker;
        private readonly Action<byte[]> _send;
        private readonly Action<Exception> _onFailure;
        private DeviceControlRule _rule;
        private int _isActive = 1;

        public TargetFlow(DeviceControlRule rule, IPAddress address, byte[] macBytes, Action<byte[]> send, Action<Exception> onFailure)
        {
            _rule = rule;
            Address = address;
            MacBytes = macBytes;
            _send = send;
            _onFailure = onFailure;
            _downloadWorker = RunQueueAsync(_download.Reader, true, _cancellation.Token);
            _uploadWorker = RunQueueAsync(_upload.Reader, false, _cancellation.Token);
        }

        public IPAddress Address { get; }
        public byte[] MacBytes { get; }
        public DeviceControlRule Rule => Volatile.Read(ref _rule);
        public bool IsActive => Volatile.Read(ref _isActive) == 1;

        public void SetRule(DeviceControlRule rule) => Volatile.Write(ref _rule, rule);

        public bool Enqueue(PacketDirection direction, byte[] packet) =>
            (direction == PacketDirection.Download ? _download.Writer : _upload.Writer).TryWrite(packet);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _isActive, 0);
            _cancellation.Cancel();
            _download.Writer.TryComplete();
            _upload.Writer.TryComplete();
            try { await Task.WhenAll(_downloadWorker, _uploadWorker); }
            catch { /* cleanup continues even if packet forwarding failed */ }
            _cancellation.Dispose();
        }

        private async Task RunQueueAsync(ChannelReader<byte[]> reader, bool download, CancellationToken cancellationToken)
        {
            var nextAvailableAt = Stopwatch.GetTimestamp();
            try
            {
                await foreach (var packet in reader.ReadAllAsync(cancellationToken))
                {
                    var rateMbps = download ? Rule.DownloadLimitMbps : Rule.UploadLimitMbps;
                    if (rateMbps is > 0)
                    {
                        var now = Stopwatch.GetTimestamp();
                        if (nextAvailableAt > now)
                        {
                            var wait = TimeSpan.FromSeconds((double)(nextAvailableAt - now) / Stopwatch.Frequency);
                            await Task.Delay(wait, cancellationToken);
                            now = Stopwatch.GetTimestamp();
                        }
                        var packetSeconds = (double)packet.Length * 8 / (rateMbps.Value * 1_000_000d);
                        nextAvailableAt = Math.Max(now, nextAvailableAt) + (long)(packetSeconds * Stopwatch.Frequency);
                    }
                    else
                    {
                        nextAvailableAt = Stopwatch.GetTimestamp();
                    }

                    _send(packet);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _onFailure(exception);
            }
        }

        private static Channel<byte[]> CreateQueue() => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }
}
